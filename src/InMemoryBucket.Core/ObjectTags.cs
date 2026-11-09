using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace InMemoryBucket.Core;

/// <summary>
/// Object tag sets and the limits S3 enforces on them: at most 10 tags, unique keys of 1–128 characters,
/// values of up to 256 characters, letters, digits, spaces and <c>+ - = . _ : / @</c> only, no <c>aws:</c> prefix.
/// </summary>
internal static partial class ObjectTags
{
    public const int MaxTags = 10;
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 256;

    public static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Builds a validated tag set, or throws <see cref="S3ErrorCode.InvalidTag"/> as S3 does.</summary>
    public static ImmutableDictionary<string, string> Create(IEnumerable<KeyValuePair<string, string>> tags)
    {
        var builder = Empty.ToBuilder();
        foreach (var (key, value) in tags)
        {
            if (key.Length == 0 || key.Length > MaxKeyLength || !Allowed().IsMatch(key))
            {
                throw new S3Exception(S3ErrorCode.InvalidTag, "The TagKey you have provided is invalid");
            }

            if (value.Length > MaxValueLength || !Allowed().IsMatch(value))
            {
                throw new S3Exception(S3ErrorCode.InvalidTag, "The TagValue you have provided is invalid");
            }

            if (key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
            {
                throw new S3Exception(S3ErrorCode.InvalidTag, "Your TagKey cannot be prefixed with aws:");
            }

            if (!builder.TryAdd(key, value))
            {
                throw new S3Exception(S3ErrorCode.InvalidTag, "Cannot provide multiple Tags with the same key");
            }
        }

        if (builder.Count > MaxTags)
        {
            throw new S3Exception(S3ErrorCode.InvalidTag, "Object tags cannot be greater than 10");
        }

        return builder.ToImmutable();
    }

    /// <summary>Parses the <c>x-amz-tagging</c> header: URL query encoding, <c>k1=v1&amp;k2=v2</c>.</summary>
    public static ImmutableDictionary<string, string> ParseHeader(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return Empty;
        }

        return Create(header.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            return KeyValuePair.Create(Decode(key), Decode(value));
        }));
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    [GeneratedRegex(@"\A[\p{L}\p{Z}\p{N}_.:/=+\-@]*\z")]
    private static partial Regex Allowed();
}
