using System.Text;

namespace InMemoryBucket.Core;

internal sealed record ListObjectsV2Query(
    string Prefix = "",
    string? Delimiter = null,
    int MaxKeys = 1000,
    string? ContinuationToken = null,
    string? StartAfter = null);

internal sealed record ListObjectsV2Result(
    IReadOnlyList<S3Object> Contents,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextContinuationToken,
    int KeyCount);

internal static class ContinuationTokens
{
    public static string Encode(string lastKey) => Convert.ToBase64String(Encoding.UTF8.GetBytes(lastKey));

    public static string Decode(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "The continuation token provided is incorrect");
        }
    }
}
