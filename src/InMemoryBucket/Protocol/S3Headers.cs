using InMemoryBucket.Core;

namespace InMemoryBucket.Protocol;

/// <summary>
/// Request headers the fake refuses with 501 because it cannot honour them, and the <c>x-amz-*</c> headers it knows,
/// so that anything else can be reported instead of being silently dropped.
/// </summary>
internal static class S3Headers
{
    /// <summary>Header prefix (or exact name) to the feature it asks for. Matched case-insensitively.</summary>
    private static readonly (string Prefix, string Feature)[] Unsupported =
    [
        ("x-amz-server-side-encryption-customer-", "SSE-C (customer-provided keys)"),
        ("x-amz-copy-source-server-side-encryption-customer-", "SSE-C (customer-provided keys)"),
        ("x-amz-object-lock-", "Object Lock"),
        ("x-amz-website-redirect-location", "website redirects"),
        ("x-amz-write-offset-bytes", "appending with x-amz-write-offset-bytes"),
    ];

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-amz-date", "x-amz-content-sha256", "x-amz-security-token", "x-amz-user-agent", "x-amz-decoded-content-length",
        "x-amz-trailer", "x-amz-trailer-signature", "x-amz-sdk-checksum-algorithm", "x-amz-checksum-algorithm", "x-amz-checksum-type", "x-amz-checksum-mode",
        "x-amz-copy-source", "x-amz-copy-source-if-match", "x-amz-copy-source-if-none-match",
        "x-amz-copy-source-if-modified-since", "x-amz-copy-source-if-unmodified-since", "x-amz-metadata-directive",
        "x-amz-tagging", "x-amz-tagging-directive", "x-amz-storage-class",
        "x-amz-server-side-encryption", "x-amz-server-side-encryption-aws-kms-key-id", "x-amz-server-side-encryption-bucket-key-enabled",
        "x-amz-expected-bucket-owner", "x-amz-source-expected-bucket-owner", "x-amz-request-payer", "x-amz-api-version",
        "x-amz-bucket-region", "x-amz-bypass-governance-retention",
    };

    private static readonly string[] KnownPrefixes = ["x-amz-meta-", "x-amz-checksum-"];

    /// <summary>Throws 501 NotImplemented when the request asks for something the fake would otherwise ignore.</summary>
    public static void RejectUnsupported(S3Request request, S3Operation operation)
    {
        foreach (var name in request.Headers.Keys)
        {
            foreach (var (prefix, feature) in Unsupported)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new S3Exception(S3ErrorCode.NotImplemented, $"InMemoryBucket does not implement {feature} (header {name}).", request.Resource);
                }
            }
        }

        if (operation is S3Operation.GetObject or S3Operation.HeadObject && request.HasQuery("partNumber"))
        {
            throw new S3Exception(S3ErrorCode.NotImplemented, "InMemoryBucket does not implement reading one part of an object (?partNumber).", request.Resource);
        }
    }

    /// <summary>The <c>x-amz-*</c> request headers the fake neither uses nor rejects.</summary>
    public static IEnumerable<string> Unknown(S3Request request)
        => request.Headers.Keys.Where(name =>
            name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
            && !Known.Contains(name)
            && !KnownPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            && !Unsupported.Any(u => name.StartsWith(u.Prefix, StringComparison.OrdinalIgnoreCase)));
}
