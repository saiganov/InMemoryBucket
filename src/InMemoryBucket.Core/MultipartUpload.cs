using System.Collections.Concurrent;

namespace InMemoryBucket.Core;

/// <summary>One uploaded part. <see cref="Data"/> is owned by the upload until completion assembles the object.</summary>
internal sealed record UploadedPart(
    int PartNumber,
    byte[] Data,
    byte[] Md5,
    string ETag,
    ChecksumSet Checksums,
    DateTimeOffset LastModified);

/// <summary>A part as listed by the client in CompleteMultipartUpload.</summary>
internal sealed record CompletedPart(int PartNumber, string ETag, ChecksumSet Checksums);

/// <summary>
/// An in-progress multipart upload. Parts land in a concurrent dictionary because the AWS SDK uploads them in parallel.
/// </summary>
public sealed class MultipartUpload
{
    internal MultipartUpload(string uploadId, string key, ObjectAttributes attributes, DateTimeOffset initiated, ChecksumAlgorithm? checksumAlgorithm, string? checksumType)
    {
        UploadId = uploadId;
        Key = key;
        Attributes = attributes;
        Initiated = initiated;
        ChecksumAlgorithm = checksumAlgorithm;
        ChecksumType = checksumType;
    }

    /// <summary>The upload id returned by CreateMultipartUpload.</summary>
    public string UploadId { get; }

    /// <summary>The key the object will be stored under when the upload completes.</summary>
    public string Key { get; }

    /// <summary>The attributes given at initiation, which the completed object gets.</summary>
    public ObjectAttributes Attributes { get; }

    /// <summary>When the upload was created.</summary>
    public DateTimeOffset Initiated { get; }

    /// <summary>Algorithm declared at initiation via <c>x-amz-checksum-algorithm</c>, if any.</summary>
    public ChecksumAlgorithm? ChecksumAlgorithm { get; }

    /// <summary><c>COMPOSITE</c> or <c>FULL_OBJECT</c> as declared at initiation, if any.</summary>
    public string? ChecksumType { get; }

    internal ConcurrentDictionary<int, UploadedPart> Parts { get; } = new();
}
