namespace InMemoryBucket;

/// <summary>
/// Settings of an <see cref="InMemoryBucketServer"/>, set in the delegate passed to its constructor or to <c>AddInMemoryBucket()</c>.
/// <see cref="MaxTotalBytes"/>, <see cref="UploadIdGenerator"/> and <see cref="RequestLogCapacity"/> are read when the server is built.
/// Set <see cref="TimeProvider"/> there too: buckets keep the clock they were created with, so swapping it later mixes two clocks.
/// </summary>
public sealed class InMemoryBucketOptions
{
    /// <summary>Region reported by GetBucketLocation and HeadBucket. Also decides what a repeated CreateBucket returns: 200 in us-east-1 (as AWS does), 409 elsewhere.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Host name the fake answers on. Used to detect virtual-hosted addressing and to build Location URLs. Nothing listens on it.</summary>
    public string Host { get; set; } = "s3.in-memory.local";

    /// <summary>Buckets created when the server starts and after every <see cref="InMemoryBucketServer.Reset"/>.</summary>
    public IList<string> Buckets { get; } = new List<string>();

    /// <summary>Clock for LastModified, CreationDate and the Date header. Swap in a FakeTimeProvider for deterministic tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Upper bound on the bytes the store holds: object data plus parts of in-progress multipart uploads. A write that would
    /// pass it throws <see cref="InvalidOperationException"/> naming the current size and the limit, so a test that leaks data
    /// fails loudly instead of exhausting memory. In process the exception comes out of the client call (SendAsync); over
    /// InMemoryBucket.Hosting the client sees a 500. Completing a multipart upload needs room for the parts and the assembled
    /// object at once, as memory does. Null (the default) means no limit. Read when the server is constructed.
    /// </summary>
    public long? MaxTotalBytes { get; set; }

    /// <summary>
    /// Makes multipart upload ids. Null (the default) uses random GUIDs. Set a deterministic generator, together with a
    /// FakeTimeProvider in <see cref="TimeProvider"/>, and two runs of the same test produce byte-identical responses
    /// (x-amz-request-id is a per-server counter that <see cref="InMemoryBucketServer.Reset"/> restarts). Ids must be non-empty,
    /// URL-safe and unique among a bucket's in-progress uploads; a duplicate throws <see cref="InvalidOperationException"/>.
    /// Read when the server is constructed.
    /// </summary>
    public Func<string>? UploadIdGenerator { get; set; }

    /// <summary>Verify x-amz-checksum-* and Content-MD5 on uploads and answer BadDigest on mismatch, as S3 does.</summary>
    public bool ValidateChecksums { get; set; } = true;

    /// <summary>Where request traces go. Debug: one line per request; Trace: headers and XML bodies too. Null means silent.</summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    /// <summary>How many requests <see cref="InMemoryBucketServer.Requests"/> keeps; the oldest are dropped first.</summary>
    public int RequestLogCapacity { get; set; } = 10_000;

    /// <summary>Keep request bodies in the log. Off by default: a TransferUtility test would otherwise retain every part.</summary>
    public bool CaptureBodies { get; set; }

    /// <summary>Reject presigned URLs whose X-Amz-Expires window has passed, as S3 does. Uses <see cref="TimeProvider"/>.</summary>
    public bool EnforcePresignedExpiry { get; set; } = true;

    /// <summary>
    /// Verify every request's AWS Signature Version 4 (Authorization header or presigned query) against <see cref="Credentials"/>,
    /// and answer SignatureDoesNotMatch, InvalidAccessKeyId, RequestTimeTooSkewed or AccessDenied as S3 would.
    /// Off by default: a fake that accepts anything is easier to script. Turn it on to catch wrong keys, wrong regions or
    /// hand-built URLs. Chunk signatures inside aws-chunked bodies are not checked.
    /// </summary>
    public bool ValidateSignatures { get; set; }

    /// <summary>Access keys and their secrets that <see cref="ValidateSignatures"/> accepts. The default pair is what <c>CreateClient</c> uses.</summary>
    public IDictionary<string, string> Credentials { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["inmemorybucket"] = "inmemorybucket",
    };

    /// <summary>How far a signed request's x-amz-date may be from <see cref="TimeProvider"/> before RequestTimeTooSkewed. S3 allows 15 minutes.</summary>
    public TimeSpan ClockSkewTolerance { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The ID in the Owner elements of ListBuckets, GetBucketAcl, listings and multipart responses.</summary>
    public string OwnerId { get; set; } = "inmemorybucket";

    /// <summary>The owner display name reported next to <see cref="OwnerId"/>.</summary>
    public string OwnerDisplayName { get; set; } = "inmemorybucket";

    /// <summary>Replaces the default test credentials with one access key and secret.</summary>
    /// <param name="accessKey">The access key id.</param>
    /// <param name="secretKey">The secret access key.</param>
    /// <returns>These options, for chaining.</returns>
    public InMemoryBucketOptions UseCredentials(string accessKey, string secretKey)
    {
        Credentials.Clear();
        Credentials[accessKey] = secretKey;
        return this;
    }

    /// <summary>Adds buckets to <see cref="Buckets"/>: they exist when the server starts and again after every <see cref="InMemoryBucketServer.Reset"/>.</summary>
    /// <param name="names">Bucket names; S3 naming rules apply.</param>
    /// <returns>These options, for chaining.</returns>
    public InMemoryBucketOptions CreateBuckets(params string[] names)
    {
        foreach (var name in names)
        {
            Buckets.Add(name);
        }

        return this;
    }
}
