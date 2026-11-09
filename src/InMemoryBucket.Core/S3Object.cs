using System.Collections.Immutable;

namespace InMemoryBucket.Core;

/// <summary>
/// Everything a client sets on an object besides its bytes: content type, user metadata and the standard HTTP headers S3 stores verbatim.
/// </summary>
/// <param name="ContentType">The object's Content-Type.</param>
/// <param name="UserMetadata">User metadata (<c>x-amz-meta-*</c>) without the prefix; names are case-insensitive.</param>
/// <param name="Headers">Standard headers S3 stores and returns, such as Cache-Control and Content-Disposition.</param>
/// <param name="StorageClass">The storage class S3 reports.</param>
public sealed record ObjectAttributes(
    string ContentType,
    ImmutableDictionary<string, string> UserMetadata,
    ImmutableDictionary<string, string> Headers,
    string StorageClass = "STANDARD")
{
    /// <summary>S3's defaults: <c>binary/octet-stream</c>, no metadata, no headers, <c>STANDARD</c>.</summary>
    public static readonly ObjectAttributes Default = new(
        "binary/octet-stream",
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    /// <summary>The object's tag set; see <see cref="ObjectTags"/> for the limits.</summary>
    public ImmutableDictionary<string, string> Tags { get; init; } = ObjectTags.Empty;

    /// <summary>The server-side encryption S3 reports for the object; SSE-S3 unless the writer asked for KMS.</summary>
    public ServerSideEncryption Encryption { get; init; } = ServerSideEncryption.Default;
}

/// <summary>
/// An immutable snapshot of a stored object. <see cref="Data"/> is owned by the store and must not be mutated.
/// </summary>
/// <param name="Key">The object key.</param>
/// <param name="Data">The object's bytes.</param>
/// <param name="ETag">The ETag as S3 returns it, with quotes.</param>
/// <param name="LastModified">When the object was written.</param>
/// <param name="Attributes">Content type, metadata, headers, tags and encryption.</param>
/// <param name="Checksums">The checksums stored with the object.</param>
/// <param name="ChecksumType"><c>COMPOSITE</c> or <c>FULL_OBJECT</c> for multipart objects with checksums; otherwise null.</param>
public sealed record S3Object(
    string Key,
    ReadOnlyMemory<byte> Data,
    string ETag,
    DateTimeOffset LastModified,
    ObjectAttributes Attributes,
    ChecksumSet Checksums,
    string? ChecksumType = null)
{
    /// <summary>The object size in bytes.</summary>
    public long Size => Data.Length;
}
