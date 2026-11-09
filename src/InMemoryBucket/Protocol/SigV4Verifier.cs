using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InMemoryBucket.Core;

namespace InMemoryBucket.Protocol;

/// <summary>
/// Recomputes an AWS Signature Version 4 for a request (Authorization header or presigned query) and compares it with
/// what the client sent. Follows the S3 flavour of the algorithm: the path is signed once-encoded as sent, the query is
/// re-encoded and sorted, the payload hash is whatever x-amz-content-sha256 claims (UNSIGNED-PAYLOAD for presigned URLs).
/// Chunk signatures inside aws-chunked bodies are not verified.
/// </summary>
internal static class SigV4Verifier
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string DateFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Null when the signature is valid; otherwise the error S3 would answer with.</summary>
    public static S3Exception? Verify(S3Request request, InMemoryBucketOptions options)
    {
        var headers = request.HeadersAsSent ?? request.Headers;
        var now = options.TimeProvider.GetUtcNow();

        if (request.QueryValue("X-Amz-Signature") is { } querySignature)
        {
            return VerifyPresigned(request, headers, querySignature, options, now);
        }

        var authorization = headers.TryGetValue("Authorization", out var a) ? a.Trim() : null;
        if (string.IsNullOrEmpty(authorization))
        {
            if (request.QueryValue("Signature") is not null || request.QueryValue("AWSAccessKeyId") is not null)
            {
                return new S3Exception(S3ErrorCode.InvalidRequest, "The authorization mechanism you have provided is not supported. Please use AWS4-HMAC-SHA256.", request.Resource);
            }

            return new S3Exception(S3ErrorCode.AccessDenied, "Anonymous access is forbidden for this operation", request.Resource);
        }

        if (!authorization.StartsWith(Algorithm + " ", StringComparison.Ordinal))
        {
            return authorization.StartsWith("AWS ", StringComparison.Ordinal)
                ? new S3Exception(S3ErrorCode.InvalidRequest, "The authorization mechanism you have provided is not supported. Please use AWS4-HMAC-SHA256.", request.Resource)
                : new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "The authorization header is malformed; unsupported algorithm.", request.Resource);
        }

        string? credential = null, signedHeaders = null, signature = null;
        foreach (var part in authorization[Algorithm.Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                return new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "The authorization header is malformed; a component is missing its value.", request.Resource);
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            switch (name)
            {
                case "Credential": credential = value; break;
                case "SignedHeaders": signedHeaders = value; break;
                case "Signature": signature = value; break;
            }
        }

        if (credential is null || signedHeaders is null || signature is null)
        {
            return new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "The authorization header is malformed; Credential, SignedHeaders and Signature are all required.", request.Resource);
        }

        if (!headers.TryGetValue("x-amz-date", out var dateText) && !headers.TryGetValue("Date", out dateText))
        {
            return new S3Exception(S3ErrorCode.AccessDenied, "AWS authentication requires a valid Date or x-amz-date header", request.Resource);
        }

        if (!TryParseAmzDate(dateText, out var requestTime))
        {
            return new S3Exception(S3ErrorCode.AccessDenied, "X-Amz-Date must be in the ISO8601 Long Format \"yyyyMMdd'T'HHmmss'Z'\"", request.Resource);
        }

        if ((now - requestTime).Duration() > options.ClockSkewTolerance)
        {
            return new S3Exception(S3ErrorCode.RequestTimeTooSkewed, null, request.Resource);
        }

        if (!TryParseScope(credential, S3ErrorCode.AuthorizationHeaderMalformed, request, options, out var scope, out var error))
        {
            return error;
        }

        var payloadHash = headers.TryGetValue("x-amz-content-sha256", out var declaredHash) ? declaredHash.Trim() : "UNSIGNED-PAYLOAD";
        if (IsHex(payloadHash) && payloadHash.Length == 64)
        {
            var actual = Convert.ToHexString(SHA256.HashData(request.Body.Span)).ToLowerInvariant();
            if (!actual.Equals(payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                return new S3Exception(S3ErrorCode.XAmzContentSHA256Mismatch, null, request.Resource);
            }
        }

        return Compare(request, headers, scope, signedHeaders, dateText, payloadHash, signature, request.Query, options);
    }

    private static S3Exception? VerifyPresigned(S3Request request, IReadOnlyDictionary<string, string> headers, string signature, InMemoryBucketOptions options, DateTimeOffset now)
    {
        if (request.QueryValue("X-Amz-Algorithm") != Algorithm)
        {
            return new S3Exception(S3ErrorCode.AuthorizationQueryParametersError, "X-Amz-Algorithm only supports \"AWS4-HMAC-SHA256\"", request.Resource);
        }

        if (request.QueryValue("X-Amz-Credential") is not { } credential
            || request.QueryValue("X-Amz-SignedHeaders") is not { } signedHeaders
            || request.QueryValue("X-Amz-Date") is not { } dateText)
        {
            return new S3Exception(S3ErrorCode.AuthorizationQueryParametersError, "Query-string authentication requires the X-Amz-Algorithm, X-Amz-Credential, X-Amz-Signature, X-Amz-Date, X-Amz-SignedHeaders and X-Amz-Expires parameters.", request.Resource);
        }

        if (!TryParseAmzDate(dateText, out var signedAt))
        {
            return new S3Exception(S3ErrorCode.AuthorizationQueryParametersError, "X-Amz-Date must be in the ISO8601 Long Format \"yyyyMMdd'T'HHmmss'Z'\"", request.Resource);
        }

        if (signedAt - now > options.ClockSkewTolerance)
        {
            return new S3Exception(S3ErrorCode.AccessDenied, "Request is not yet valid", request.Resource);
        }

        if (!TryParseScope(credential, S3ErrorCode.AuthorizationQueryParametersError, request, options, out var scope, out var error))
        {
            return error;
        }

        // The signature itself is not part of what it signs.
        var query = request.Query.Where(kv => kv.Key != "X-Amz-Signature").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return Compare(request, headers, scope, signedHeaders, dateText, "UNSIGNED-PAYLOAD", signature, query, options);
    }

    private static S3Exception? Compare(
        S3Request request,
        IReadOnlyDictionary<string, string> headers,
        Scope scope,
        string signedHeaders,
        string dateText,
        string payloadHash,
        string expectedSignature,
        IReadOnlyDictionary<string, string> query,
        InMemoryBucketOptions options)
    {
        var signedNames = signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var canonicalHeaders = new StringBuilder();
        foreach (var name in signedNames)
        {
            string? value;
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                value = headers.TryGetValue("Host", out var h) ? h : request.Host;
            }
            else if (!headers.TryGetValue(name, out value))
            {
                value = string.Empty;
            }

            canonicalHeaders.Append(name.ToLowerInvariant()).Append(':').Append(CollapseSpaces(value)).Append('\n');
        }

        var canonicalQuery = string.Join("&", query
            .Select(kv => (Name: UriEncode(kv.Key), Value: UriEncode(kv.Value)))
            .OrderBy(kv => kv.Name, StringComparer.Ordinal)
            .ThenBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(kv => kv.Name + "=" + kv.Value));

        var secret = options.Credentials.TryGetValue(scope.AccessKey, out var s) ? s : null;
        if (secret is null)
        {
            return new S3Exception(S3ErrorCode.InvalidAccessKeyId, null, request.Resource);
        }

        var signingKey = SigningKey(secret, scope);
        var scopeText = scope.Date + "/" + scope.Region + "/" + scope.Service + "/aws4_request";

        // The path is signed exactly as the client encoded it. Candidates cover clients whose URL stack normalised the
        // path after signing (e.g. percent-encoded unreserved characters).
        foreach (var canonicalUri in PathCandidates(request))
        {
            var canonicalRequest = string.Join("\n",
                request.Method,
                canonicalUri,
                canonicalQuery,
                canonicalHeaders.ToString(),
                string.Join(";", signedNames.Select(n => n.ToLowerInvariant())),
                payloadHash);

            var stringToSign = string.Join("\n",
                Algorithm,
                dateText,
                scopeText,
                Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

            var actual = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
            if (FixedTimeEquals(actual, expectedSignature))
            {
                return null;
            }
        }

        return new S3Exception(S3ErrorCode.SignatureDoesNotMatch, null, request.Resource);
    }

    private static IEnumerable<string> PathCandidates(S3Request request)
    {
        yield return request.RawPath;

        // Re-encode the decoded path the way S3 and the SDKs do: unreserved characters and '/' kept, everything else %XX.
        var decoded = Uri.UnescapeDataString(request.RawPath);
        var reencoded = UriEncode(decoded, keepSlash: true);
        if (reencoded != request.RawPath)
        {
            yield return reencoded;
        }
    }

    private static bool TryParseScope(string credential, S3ErrorCode malformed, S3Request request, InMemoryBucketOptions options, out Scope scope, out S3Exception? error)
    {
        scope = default;
        error = null;
        var parts = credential.Split('/');
        if (parts.Length != 5 || parts[4] != "aws4_request")
        {
            error = new S3Exception(malformed, $"Error parsing the Credential parameter; the Credential is mal-formed; expecting \"<YOUR-AKID>/YYYYMMDD/REGION/SERVICE/aws4_request\".", request.Resource);
            return false;
        }

        if (parts[3] != "s3")
        {
            error = new S3Exception(malformed, $"Credential should be scoped to correct service: 's3'. ", request.Resource);
            return false;
        }

        if (!string.Equals(parts[2], options.Region, StringComparison.Ordinal))
        {
            error = new S3Exception(malformed, $"Credential should be scoped to a valid region, not '{parts[2]}'. The authorization header is malformed; the region '{parts[2]}' is wrong; expecting '{options.Region}'", request.Resource);
            return false;
        }

        scope = new Scope(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    private static byte[] SigningKey(string secret, Scope scope)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(scope.Date));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(scope.Region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(scope.Service));
        return HMACSHA256.HashData(kService, "aws4_request"u8.ToArray());
    }

    private static bool TryParseAmzDate(string text, out DateTimeOffset value)
        => DateTimeOffset.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)
           || DateTimeOffset.TryParseExact(text.Trim(), "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value.Trim())
        {
            if (c is ' ' or '\t')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>RFC 3986 encoding as AWS specifies it for SigV4: A-Z a-z 0-9 - _ . ~ stay, everything else becomes %XX (uppercase).</summary>
    internal static string UriEncode(string value, bool keepSlash = false)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static bool IsHex(string text) => text.All(Uri.IsHexDigit);

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var a = Encoding.ASCII.GetBytes(actual);
        var b = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private readonly record struct Scope(string AccessKey, string Date, string Region, string Service);
}
