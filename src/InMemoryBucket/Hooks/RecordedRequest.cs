using InMemoryBucket.Protocol;

namespace InMemoryBucket.Hooks;

/// <summary>
/// One request as the server saw it. Fault rules receive the same object before the operation runs, when
/// <see cref="StatusCode"/> and <see cref="Duration"/> are not known yet (0 and zero).
/// </summary>
public sealed class RecordedRequest
{
    internal RecordedRequest(S3Request request, S3Operation operation, string? accessKey, DateTimeOffset timestamp, ReadOnlyMemory<byte>? body)
    {
        Operation = operation;
        Method = request.Method;
        Bucket = request.Bucket.Length == 0 ? null : request.Bucket;
        Key = request.Key.Length == 0 ? null : request.Key;
        Query = request.Query;
        Headers = request.Headers;
        AccessKey = accessKey;
        Timestamp = timestamp;
        Body = body;
        ContentLength = request.Body.Length;
    }

    /// <summary>The S3 operation the request was routed to.</summary>
    public S3Operation Operation { get; }

    /// <summary>The HTTP method, such as <c>PUT</c>.</summary>
    public string Method { get; }

    /// <summary>Null for service-level requests such as ListBuckets.</summary>
    public string? Bucket { get; }

    /// <summary>Null for bucket-level requests.</summary>
    public string? Key { get; }

    /// <summary>Decoded query parameters.</summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>Request headers, case-insensitive. For aws-chunked uploads these are the decoded headers plus the trailers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Access key id from the Authorization header or the X-Amz-Credential query parameter, if any.</summary>
    public string? AccessKey { get; }

    /// <summary>When the server received the request, by <see cref="InMemoryBucketOptions.TimeProvider"/>.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Decoded body length, even when <see cref="Body"/> is not captured.</summary>
    public long ContentLength { get; }

    /// <summary>The decoded body, only when <see cref="InMemoryBucketOptions.CaptureBodies"/> is on.</summary>
    public ReadOnlyMemory<byte>? Body { get; }

    /// <summary>The HTTP status of the response; 0 while the request is still running and for requests the server threw on instead of answering (a <see cref="FaultAction.Throw"/> rule, cancellation, <see cref="InMemoryBucketOptions.MaxTotalBytes"/>).</summary>
    public int StatusCode { get; internal set; }

    /// <summary>S3 error code of the response, null for a success.</summary>
    public string? ErrorCode { get; internal set; }

    /// <summary>How long the server took to answer; zero while the request is still running.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>Set when a <see cref="FaultRules"/> rule answered or threw instead of the store.</summary>
    public bool Faulted { get; internal set; }

    /// <summary>The value of a request header, case-insensitive.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null when the header is absent.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>A one-line summary, for example <c>PutObject PUT /bucket/key -&gt; 200</c>.</summary>
    /// <returns>The summary.</returns>
    public override string ToString()
        => $"{Operation} {Method} {(Bucket is null ? "/" : "/" + Bucket + (Key is null ? "" : "/" + Key))} -> {StatusCode}{(ErrorCode is null ? "" : " " + ErrorCode)}";
}
