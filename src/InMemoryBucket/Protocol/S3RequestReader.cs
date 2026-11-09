using InMemoryBucket.Core;

namespace InMemoryBucket.Protocol;

/// <summary>
/// Turns the raw parts of an HTTP request into an <see cref="S3Request"/>: splits bucket and key for path-style and
/// virtual-hosted addressing, decodes aws-chunked bodies and collects declared checksums. Shared by the in-process
/// handler and the Kestrel host so both speak exactly the same dialect.
/// </summary>
internal static class S3RequestReader
{
    /// <param name="method">HTTP method, any casing.</param>
    /// <param name="host">The Host header (or the URI authority when the client sent none).</param>
    /// <param name="rawPath">Absolute path as sent, still percent-encoded, with its leading '/'.</param>
    /// <param name="rawQuery">Query string as sent, with or without the leading '?'.</param>
    /// <param name="headers">Request and content headers as sent, case-insensitive. The reader may copy it before changing it.</param>
    /// <param name="body">The request body exactly as sent; aws-chunked framing is removed here.</param>
    /// <param name="serverHost">The host name the server answers on, to recognise <c>{bucket}.{serverHost}</c> addressing.</param>
    public static S3Request Read(string method, string host, string rawPath, string rawQuery, Dictionary<string, string> headers, byte[] body, string serverHost)
    {
        var path = rawPath.TrimStart('/');
        string bucket;
        string key;
        if (host.EndsWith("." + serverHost, StringComparison.OrdinalIgnoreCase))
        {
            // Virtual-hosted style: {bucket}.{host}/{key}
            bucket = host[..^(serverHost.Length + 1)];
            key = Uri.UnescapeDataString(path);
        }
        else
        {
            var slash = path.IndexOf('/');
            bucket = slash < 0 ? path : path[..slash];
            key = slash < 0 ? string.Empty : Uri.UnescapeDataString(path[(slash + 1)..]);
        }

        Dictionary<string, string>? asSent = null;
        var declared = ChecksumSet.Empty;
        if (AwsChunkedDecoder.IsChunked(headers))
        {
            asSent = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            long? hint = headers.TryGetValue("x-amz-decoded-content-length", out var h) && long.TryParse(h, out var parsed) ? parsed : null;
            var (payload, trailers) = AwsChunkedDecoder.Decode(body, hint);
            body = payload;
            foreach (var (name, value) in trailers)
            {
                headers[name] = value;
            }

            headers["Content-Length"] = body.Length.ToString();
            if (headers.TryGetValue("Content-Encoding", out var encoding))
            {
                var rest = encoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(e => !e.Equals("aws-chunked", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (rest.Length > 0)
                {
                    headers["Content-Encoding"] = string.Join(", ", rest);
                }
                else
                {
                    headers.Remove("Content-Encoding");
                }
            }
        }

        foreach (var algorithm in Enum.GetValues<ChecksumAlgorithm>())
        {
            if (headers.TryGetValue("x-amz-checksum-" + Checksums.HeaderSuffix(algorithm), out var value))
            {
                declared = declared.With(algorithm, value);
            }
        }

        return new S3Request
        {
            Method = method.ToUpperInvariant(),
            Bucket = bucket,
            Key = key,
            Query = ParseQuery(rawQuery),
            Headers = headers,
            Body = body,
            DeclaredChecksums = declared,
            RawPath = rawPath.Length == 0 ? "/" : rawPath,
            RawQuery = rawQuery.TrimStart('?'),
            HeadersAsSent = asSent,
            Host = host,
        };
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[name] = value;
        }

        return result;
    }
}
