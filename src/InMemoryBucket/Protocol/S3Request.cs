using InMemoryBucket.Core;

namespace InMemoryBucket.Protocol;

/// <summary>
/// A parsed S3 request, independent of the HTTP stack that delivered it. The body is already decoded from aws-chunked.
/// </summary>
public sealed class S3Request
{
    /// <summary>The HTTP method in upper case, such as <c>PUT</c>.</summary>
    public required string Method { get; init; }

    /// <summary>Empty for service-level requests such as ListBuckets.</summary>
    public required string Bucket { get; init; }

    /// <summary>Empty for bucket-level requests. Already URL-decoded.</summary>
    public required string Key { get; init; }

    /// <summary>Decoded query parameters; a flag such as <c>?uploads</c> maps to an empty string.</summary>
    public required IReadOnlyDictionary<string, string> Query { get; init; }

    /// <summary>Request and content headers together, case-insensitive, multiple values joined with ", ".</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Decoded payload.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Checksums the client declared in <c>x-amz-checksum-*</c> headers or aws-chunked trailers.</summary>
    public required ChecksumSet DeclaredChecksums { get; init; }

    /// <summary>The absolute path exactly as the client sent it, still percent-encoded. What SigV4 signs.</summary>
    public string RawPath { get; init; } = "/";

    /// <summary>The query string as sent, without the leading '?'.</summary>
    public string RawQuery { get; init; } = string.Empty;

    /// <summary>
    /// Headers as the client sent them, before aws-chunked decoding rewrote Content-Length and Content-Encoding and merged trailers.
    /// Null when nothing was rewritten; use <see cref="Headers"/> then.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HeadersAsSent { get; init; }

    /// <summary>Host the request was addressed to, as it appears in the signed <c>host</c> header.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary><c>/bucket/key</c> or <c>/bucket</c>: the <c>&lt;Resource&gt;</c> of error responses.</summary>
    public string Resource => Key.Length > 0 ? "/" + Bucket + "/" + Key : "/" + Bucket;

    /// <summary>The value of a header, case-insensitive.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null when the header is absent.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Whether the query has the parameter, with or without a value.</summary>
    /// <param name="name">The parameter name, case-sensitive.</param>
    /// <returns>True when the parameter is present.</returns>
    public bool HasQuery(string name) => Query.ContainsKey(name);

    /// <summary>The decoded value of a query parameter.</summary>
    /// <param name="name">The parameter name, case-sensitive.</param>
    /// <returns>The value, an empty string for a flag, or null when the parameter is absent.</returns>
    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Builds a request from the raw parts of an HTTP request, as the in-process handler and the Kestrel host do: splits bucket
    /// and key for path-style and virtual-hosted addressing, removes aws-chunked framing and collects declared checksums.
    /// Pass the result to <see cref="InMemoryBucketServer.HandleAsync"/> to put the fake behind another transport.
    /// </summary>
    /// <param name="method">HTTP method, any casing.</param>
    /// <param name="host">The Host header (or the URI authority when the client sent none).</param>
    /// <param name="rawPath">Absolute path as sent, still percent-encoded, with its leading '/'.</param>
    /// <param name="rawQuery">Query string as sent, with or without the leading '?'.</param>
    /// <param name="headers">Request and content headers as sent, case-insensitive. May be copied before it is changed.</param>
    /// <param name="body">The request body exactly as sent.</param>
    /// <param name="serverHost">The host name the server answers on, to recognise <c>{bucket}.{serverHost}</c> addressing.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="S3Exception">The request is malformed, for example broken aws-chunked framing; answer it as an S3 error.</exception>
    public static S3Request Parse(string method, string host, string rawPath, string rawQuery, Dictionary<string, string> headers, byte[] body, string serverHost)
        => S3RequestReader.Read(method, host, rawPath, rawQuery, headers, body, serverHost);
}

/// <summary>
/// An S3 response, independent of the HTTP stack that will send it: status, headers and body.
/// </summary>
public sealed class S3Response
{
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Response headers, case-insensitive, including content headers such as Content-Type.</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The response body; empty for HEAD and most write operations.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Content-Length to report when <see cref="Body"/> is intentionally empty, as in a HEAD response.</summary>
    public long? DeclaredContentLength { get; init; }

    /// <summary>S3 error code when this is an error response, for the request log.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Sets a header, replacing an existing value.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This response, for chaining.</returns>
    public S3Response WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}
