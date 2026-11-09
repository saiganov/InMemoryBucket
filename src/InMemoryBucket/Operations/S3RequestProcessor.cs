using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Operations;

/// <summary>
/// Dispatches a parsed request to the operation that handles it and turns <see cref="S3Exception"/> into S3 error responses.
/// Split across partial files by operation family.
/// </summary>
internal sealed partial class S3RequestProcessor
{
    private static readonly string[] StoredHeaderNames =
    [
        "Cache-Control", "Content-Disposition", "Content-Encoding", "Content-Language", "Expires",
    ];

    private readonly ObjectStore _store;
    private readonly InMemoryBucketOptions _options;
    private long _requestCounter;

    /// <summary>Restarts x-amz-request-id at 1, so a test that calls Reset gets the same ids as a fresh server.</summary>
    internal void ResetRequestIds() => Interlocked.Exchange(ref _requestCounter, 0);

    public S3RequestProcessor(ObjectStore store, InMemoryBucketOptions options)
    {
        _store = store;
        _options = options;
    }

    public S3Response Process(S3Request request)
    {
        var operation = S3Router.Resolve(request);
        S3Response response;
        try
        {
            S3Headers.RejectUnsupported(request, operation);
            response = operation switch
            {
                S3Operation.ListBuckets => ListBuckets(),
                S3Operation.CreateBucket => CreateBucket(request),
                S3Operation.HeadBucket => HeadBucket(request),
                S3Operation.DeleteBucket => DeleteBucket(request),
                S3Operation.GetBucketLocation => GetBucketLocation(request),
                S3Operation.GetBucketAcl => GetBucketAcl(request),
                S3Operation.ListObjects => ListObjects(request),
                S3Operation.ListObjectsV2 => ListObjectsV2(request),
                S3Operation.ListMultipartUploads => ListMultipartUploads(request),
                S3Operation.DeleteObjects => DeleteObjects(request),
                S3Operation.PutObject => PutObject(request),
                S3Operation.CopyObject => CopyObject(request),
                S3Operation.GetObject => GetObject(request, includeBody: true),
                S3Operation.HeadObject => GetObject(request, includeBody: false),
                S3Operation.DeleteObject => DeleteObject(request),
                S3Operation.CreateMultipartUpload => CreateMultipartUpload(request),
                S3Operation.UploadPart => UploadPart(request),
                S3Operation.CompleteMultipartUpload => CompleteMultipartUpload(request),
                S3Operation.AbortMultipartUpload => AbortMultipartUpload(request),
                S3Operation.ListParts => ListParts(request),
                S3Operation.GetObjectTagging => GetObjectTagging(request),
                S3Operation.PutObjectTagging => PutObjectTagging(request),
                S3Operation.DeleteObjectTagging => DeleteObjectTagging(request),
                _ => throw new S3Exception(
                    S3ErrorCode.NotImplemented,
                    $"InMemoryBucket does not implement {operation} ({request.Method} {request.Resource}{QueryString(request)}).",
                    request.Resource),
            };
        }
        catch (S3Exception error)
        {
            var id = NextRequestId();
            return Stamp(ErrorResponse(request, error, id), id);
        }

        return Stamp(response, NextRequestId());
    }

    private const string HostId = "InMemoryBucket";

    private string NextRequestId() => Interlocked.Increment(ref _requestCounter).ToString("X16", CultureInfo.InvariantCulture);

    private S3Response Stamp(S3Response response, string requestId)
    {
        response.Headers.TryAdd("x-amz-request-id", requestId);
        response.Headers.TryAdd("x-amz-id-2", HostId);
        response.Headers.TryAdd("Date", S3Xml.Rfc1123(Now));
        response.Headers.TryAdd("Server", "InMemoryBucket");
        return response;
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    /// <summary>The standard S3 error document for <paramref name="error"/>, stamped like any other response.</summary>
    public S3Response Error(S3Request request, S3Exception error)
    {
        var id = NextRequestId();
        return Stamp(ErrorResponse(request, error, id), id);
    }

    /// <summary>S3's error document; <c>RequestId</c> and <c>HostId</c> repeat the x-amz-request-id and x-amz-id-2 headers, as on S3.</summary>
    private static S3Response ErrorResponse(S3Request request, S3Exception error, string requestId)
    {
        var status = error.Code.HttpStatus;
        if (request.Method == "HEAD" || status == 304)
        {
            return new S3Response { StatusCode = status, ErrorCode = error.Code.Code };
        }

        var body = S3Xml.Document("Error", w =>
        {
            w.Element("Code", error.Code.Code);
            w.Element("Message", error.Message);
            w.Element("Resource", error.Resource ?? request.Resource);
            w.Element("RequestId", requestId);
            w.Element("HostId", HostId);
        }, inS3Namespace: false);

        return new S3Response { StatusCode = status, Body = body, ErrorCode = error.Code.Code }.WithHeader("Content-Type", "application/xml");
    }

    private static S3Response Xml(int status, byte[] body)
        => new S3Response { StatusCode = status, Body = body }.WithHeader("Content-Type", "application/xml");

    private static S3Response NoContent() => new() { StatusCode = 204 };

    private static string QueryString(S3Request request)
        => request.Query.Count == 0 ? string.Empty : "?" + string.Join("&", request.Query.Select(kv => kv.Value.Length == 0 ? kv.Key : kv.Key + "=" + kv.Value));

    private Bucket RequireBucket(S3Request request) => _store.GetBucket(request.Bucket);

    /// <summary>Content type, user metadata and the standard headers S3 stores from a PUT or an initiated multipart upload.</summary>
    private static ObjectAttributes ReadAttributes(S3Request request)
    {
        var metadata = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Headers)
        {
            if (name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                metadata[name["x-amz-meta-".Length..].ToLowerInvariant()] = value;
            }
            else if (StoredHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                headers[name] = value;
            }
        }

        return new ObjectAttributes(
            request.Header("Content-Type") ?? "binary/octet-stream",
            metadata.ToImmutable(),
            headers.ToImmutable(),
            request.Header("x-amz-storage-class") ?? "STANDARD")
        {
            Tags = ObjectTags.ParseHeader(request.Header("x-amz-tagging")),
            Encryption = ReadEncryption(request),
        };
    }

    private static ServerSideEncryption ReadEncryption(S3Request request)
        => ServerSideEncryption.Parse(
            request.Header("x-amz-server-side-encryption"),
            request.Header("x-amz-server-side-encryption-aws-kms-key-id"),
            request.Header("x-amz-server-side-encryption-bucket-key-enabled"));

    /// <summary>The encryption headers S3 returns on every object write and read.</summary>
    private static void AddEncryptionHeaders(S3Response response, ServerSideEncryption encryption)
    {
        response.Headers["x-amz-server-side-encryption"] = encryption.Algorithm;
        if (encryption.KmsKeyId is not null)
        {
            response.Headers["x-amz-server-side-encryption-aws-kms-key-id"] = encryption.KmsKeyId;
        }

        if (encryption.BucketKeyEnabled)
        {
            response.Headers["x-amz-server-side-encryption-bucket-key-enabled"] = "true";
        }
    }

    private void VerifyUploadIntegrity(S3Request request, ReadOnlySpan<byte> payload)
    {
        if (!_options.ValidateChecksums)
        {
            return;
        }

        Checksums.Verify(request.DeclaredChecksums, payload);

        if (request.Header("Content-MD5") is { } md5)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(md5);
            }
            catch (FormatException)
            {
                throw new S3Exception(S3ErrorCode.InvalidDigest, "The Content-MD5 you specified was invalid.");
            }

            if (!expected.AsSpan().SequenceEqual(ETags.RawMd5(payload)))
            {
                throw new S3Exception(S3ErrorCode.BadDigest, "The Content-MD5 you specified did not match what we received.");
            }
        }
    }

    private static void AddChecksumHeaders(S3Response response, ChecksumSet checksums, string? checksumType)
    {
        foreach (var (algorithm, value) in checksums.Entries())
        {
            response.Headers["x-amz-checksum-" + Checksums.HeaderSuffix(algorithm)] = value;
        }

        if (checksumType is not null && !checksums.IsEmpty)
        {
            response.Headers["x-amz-checksum-type"] = checksumType;
        }
    }

    private static string UrlEncodeKey(string key) => Uri.EscapeDataString(key);

    /// <summary>
    /// The encoding S3 applies to keys, prefixes and delimiters when <c>encoding-type=url</c> is requested:
    /// unreserved characters and <c>/</c> stay, a space becomes <c>+</c>, everything else is percent-encoded UTF-8.
    /// The AWS SDK for .NET does not decode these, so clients see exactly this form; it round-trips through <c>WebUtility.UrlDecode</c>.
    /// </summary>
    internal static string S3UrlEncode(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>Parses a request body as XML and checks the root element name; anything else is MalformedXML, as S3 answers.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="expectedRoot">The root element S3 expects.</param>
    /// <param name="preserveWhitespace">Keep whitespace-only text, for documents whose values may legitimately be just spaces (tags).</param>
    private static System.Xml.Linq.XElement ParseXmlBody(ReadOnlyMemory<byte> body, string expectedRoot, bool preserveWhitespace = false)
    {
        System.Xml.Linq.XDocument document;
        try
        {
            document = System.Xml.Linq.XDocument.Parse(
                Utf8(body),
                preserveWhitespace ? System.Xml.Linq.LoadOptions.PreserveWhitespace : System.Xml.Linq.LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        if (document.Root is null || document.Root.Name.LocalName != expectedRoot)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        return document.Root;
    }

    private static string? ChildValue(System.Xml.Linq.XElement element, string name)
        => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static string Utf8(ReadOnlyMemory<byte> body) => Encoding.UTF8.GetString(body.Span);
}
