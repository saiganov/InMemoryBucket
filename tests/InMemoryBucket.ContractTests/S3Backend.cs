using Amazon.S3;

namespace InMemoryBucket.ContractTests;

public enum S3Flavor
{
    /// <summary>InMemoryBucket itself.</summary>
    InMemory,

    /// <summary>A real MinIO server, the reference implementation the contract is checked against.</summary>
    MinIO,
}

/// <summary>
/// What a contract test run needs from a backend: a client and the few places where documented behaviour legitimately differs.
/// Differences are listed in KNOWN_DIFFERENCES.md; a test branches on them instead of being skipped.
/// </summary>
public abstract class S3Backend
{
    public abstract S3Flavor Flavor { get; }

    public abstract IAmazonS3 Client { get; }

    /// <summary>AWS answers 200 to a repeated CreateBucket in us-east-1 only; MinIO answers 409 everywhere.</summary>
    public virtual bool RepeatedCreateBucketConflicts => Flavor == S3Flavor.MinIO;

    /// <summary>
    /// AWS lists in-progress uploads under any prefix and rolls them up by delimiter. MinIO deliberately answers only for a
    /// prefix equal to an exact key, so TransferUtility.AbortMultipartUploadsAsync is a no-op there.
    /// </summary>
    public virtual bool ListsMultipartUploadsByPrefix => Flavor != S3Flavor.MinIO;

    /// <summary>
    /// When the number of keys is a multiple of max-keys, AWS ends the listing on the last full page. MinIO may need one
    /// more, empty page to notice it is done.
    /// </summary>
    public virtual bool MayReturnTrailingEmptyPage => Flavor == S3Flavor.MinIO;

    /// <summary>
    /// AWS encrypts every new object with SSE-S3 and says so in <c>x-amz-server-side-encryption: AES256</c>. MinIO without a
    /// KMS stores objects unencrypted and sends no such header.
    /// </summary>
    public virtual bool ReportsDefaultEncryption => Flavor != S3Flavor.MinIO;
}
