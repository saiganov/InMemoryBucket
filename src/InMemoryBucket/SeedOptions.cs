namespace InMemoryBucket;

/// <summary>
/// Optional attributes for <see cref="InMemoryBucketServer.Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/>.
/// Use with a target-typed <c>new()</c>:
/// <code>server.Seed("uploads", "a.json", json, new() { ContentType = "application/json", Metadata = { ["owner"] = "alice" } });</code>
/// </summary>
public sealed class SeedOptions
{
    /// <summary>The object's Content-Type, printable ASCII. S3's default, <c>binary/octet-stream</c>, when not set, empty or whitespace.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// User metadata, without the <c>x-amz-meta-</c> prefix. Names are case-insensitive (setting <c>Owner</c> after
    /// <c>owner</c> replaces it) and stored in lower case, as S3 returns them. Names must be valid HTTP header names and
    /// values printable ASCII; surrounding spaces are trimmed, as they are on the wire.
    /// </summary>
    public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The object's tag set. S3's tag limits apply.</summary>
    public IDictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>LastModified of the stored object, converted to UTC. The server's <see cref="InMemoryBucketOptions.TimeProvider"/> when not set.</summary>
    public DateTimeOffset? LastModified { get; init; }
}
