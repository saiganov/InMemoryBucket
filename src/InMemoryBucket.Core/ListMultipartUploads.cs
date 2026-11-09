namespace InMemoryBucket.Core;

internal sealed record ListMultipartUploadsQuery(
    string Prefix = "",
    string? Delimiter = null,
    int MaxUploads = 1000,
    string? KeyMarker = null,
    string? UploadIdMarker = null);

internal sealed record ListMultipartUploadsResult(
    IReadOnlyList<MultipartUpload> Uploads,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextUploadIdMarker);
