namespace InMemoryBucket.Core;

/// <summary>
/// Conditional write headers as S3 applies them to PutObject and CompleteMultipartUpload:
/// <c>If-None-Match: *</c> writes only when the key does not exist, <c>If-Match: etag</c> only when the current object has that ETag.
/// The check runs under the bucket's write lock, so two racing conditional writes cannot both succeed.
/// </summary>
internal readonly record struct WriteCondition(string? IfMatch, string? IfNoneMatch)
{
    public static WriteCondition None => default;

    public bool IsEmpty => IfMatch is null && IfNoneMatch is null;

    /// <summary>Rejects header values S3 does not support. Call before doing any work for the request.</summary>
    public void Validate()
    {
        if (IfNoneMatch is not null && IfNoneMatch.Trim() != "*")
        {
            throw new S3Exception(S3ErrorCode.NotImplemented, "If-None-Match on writes supports only the '*' value.");
        }
    }

    internal void Check(string bucket, string key, S3Object? current)
    {
        if (IfMatch is not null)
        {
            if (current is null)
            {
                throw new S3Exception(S3ErrorCode.NoSuchKey, resource: "/" + bucket + "/" + key);
            }

            if (IfMatch.Trim() != "*" && !ETags.AreEqual(IfMatch.Trim(), current.ETag))
            {
                throw new S3Exception(S3ErrorCode.PreconditionFailed);
            }
        }

        if (IfNoneMatch is not null && current is not null)
        {
            throw new S3Exception(S3ErrorCode.PreconditionFailed);
        }
    }
}
