using System.Text;

namespace InMemoryBucket.Core;

/// <summary>Size limits S3 enforces when an object is written.</summary>
internal static class ObjectLimits
{
    /// <summary>A key is at most 1024 bytes of UTF-8.</summary>
    public const int MaxKeyBytes = 1024;

    /// <summary>User metadata is at most 2 KB: the UTF-8 bytes of every name (without <c>x-amz-meta-</c>) and value, summed.</summary>
    public const int MaxUserMetadataBytes = 2048;

    public static void Check(string key, ObjectAttributes attributes)
    {
        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            throw new S3Exception(S3ErrorCode.KeyTooLongError);
        }

        var metadataBytes = attributes.UserMetadata.Sum(m => Encoding.UTF8.GetByteCount(m.Key) + Encoding.UTF8.GetByteCount(m.Value));
        if (metadataBytes > MaxUserMetadataBytes)
        {
            throw new S3Exception(S3ErrorCode.MetadataTooLarge);
        }
    }
}
