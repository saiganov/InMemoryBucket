using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using InMemoryBucket.Core;
using InMemoryBucket.Hooks;
using InMemoryBucket.Operations;

namespace InMemoryBucket;

/// <summary>
/// The in-process S3 server. Hand its <see cref="CreateHandler"/> to any HTTP client and the client talks to the in-memory store.
/// One instance per test or per fixture; both are cheap.
/// </summary>
public sealed class InMemoryBucketServer : IDisposable
{
    /// <summary>Creates a server with an empty store and the buckets from <see cref="InMemoryBucketOptions.Buckets"/>.</summary>
    /// <param name="configure">Sets the options; called once, before the store is built.</param>
    public InMemoryBucketServer(Action<InMemoryBucketOptions>? configure = null)
    {
        Options = new InMemoryBucketOptions();
        configure?.Invoke(Options);
        Store = new ObjectStore(Options.TimeProvider, Options.UploadIdGenerator, Options.MaxTotalBytes);
        Faults = new FaultRules();
        Requests = new RequestLog(Options.RequestLogCapacity);
        Processor = new S3RequestProcessor(Store, Options);
        Pipeline = new S3RequestPipeline(Processor, Options, Faults, Requests);
        CreateConfiguredBuckets();
    }

    private volatile bool _disposed;

    /// <summary>The options the server was built with. Set them in the constructor's delegate; see <see cref="InMemoryBucketOptions"/> for the ones read only at construction.</summary>
    public InMemoryBucketOptions Options { get; }

    /// <summary>Read-only view of the store for assertions: buckets, their objects and in-progress multipart uploads. Write with <see cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/> or through a client.</summary>
    public ObjectStore Store { get; }

    /// <summary>Fault injection: fail, delay or break matching requests before they reach the store.</summary>
    public FaultRules Faults { get; }

    /// <summary>Every request handled so far, oldest first, bounded by <see cref="InMemoryBucketOptions.RequestLogCapacity"/>.</summary>
    public RequestLog Requests { get; }

    internal S3RequestProcessor Processor { get; }

    internal S3RequestPipeline Pipeline { get; }

    /// <summary><c>http://{Options.Host}/</c>: the address a client bound to <see cref="CreateHandler"/> should use. Nothing listens on it.</summary>
    public Uri BaseAddress => new($"http://{Options.Host}/");

    /// <summary>A handler that answers S3 requests from memory. Not disposed by the server; dispose the client that owns it.</summary>
    public HttpMessageHandler CreateHandler() => new InMemoryBucketHandler(this);

    /// <summary>An <see cref="HttpClient"/> over a new <see cref="CreateHandler"/>, with <see cref="BaseAddress"/> set. The caller disposes it.</summary>
    /// <returns>The client.</returns>
    public HttpClient CreateHttpClient() => new(CreateHandler()) { BaseAddress = BaseAddress };

    /// <summary>
    /// Runs one already-parsed request through fault rules, authorization, the store and the request log.
    /// The in-process handler and the Kestrel host both end up here; use it to put the fake behind any other transport.
    /// </summary>
    public ValueTask<Protocol.S3Response> HandleAsync(Protocol.S3Request request, CancellationToken cancellationToken = default)
        => Pipeline.ExecuteAsync(request, cancellationToken);

    /// <summary>Drops every bucket, object and upload, clears fault rules and the request log, then recreates the buckets from <see cref="InMemoryBucketOptions.Buckets"/>.</summary>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Store.Clear();
        Processor.ResetRequestIds();
        Faults.Clear();
        Requests.Clear();
        CreateConfiguredBuckets();
    }

    /// <summary>
    /// Stores an object directly, bypassing HTTP. The bytes are copied, so the caller may reuse its buffer;
    /// <c>byte[]</c>, <c>"..."u8</c> and collection expressions all convert to <see cref="ReadOnlySpan{T}"/>.
    /// The bucket must exist (<see cref="InMemoryBucketOptions.CreateBuckets"/>, or CreateBucket through a client);
    /// S3's key, metadata and tag limits apply.
    /// </summary>
    /// <param name="bucket">An existing bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="data">The object's content.</param>
    /// <param name="options">Content-Type, metadata, tags and LastModified; S3's defaults when null.</param>
    /// <returns>The stored object, as GetObject would return it.</returns>
    /// <exception cref="InvalidOperationException">The bucket does not exist.</exception>
    /// <exception cref="ArgumentException">The key is empty, ContentType or a metadata value is not printable ASCII, a metadata name is not a valid header name, or a metadata or tag value is null.</exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    /// <exception cref="S3Exception">The key, metadata or tags break S3's limits.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The overloads differ only in the content's type; new settings go into SeedOptions, not new parameters.")]
    public S3Object Seed(string bucket, string key, ReadOnlySpan<byte> data, SeedOptions? options = null)
    {
        var (b, attributes, lastModified) = PrepareSeed(bucket, key, options);
        return b.Put(key, data.ToArray(), attributes, ChecksumSet.Empty, lastModified: lastModified); // copy: the caller may reuse its buffer
    }

    /// <summary>Stores <paramref name="content"/> as UTF-8. See <see cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/>.</summary>
    /// <inheritdoc cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)" path="/exception"/>
    /// <param name="bucket">An existing bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="content">The object's content, encoded as UTF-8.</param>
    /// <param name="options">Content-Type, metadata, tags and LastModified; S3's defaults when null.</param>
    /// <returns>The stored object.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "See the ReadOnlySpan overload.")]
    public S3Object Seed(string bucket, string key, string content, SeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (b, attributes, lastModified) = PrepareSeed(bucket, key, options);
        return b.Put(key, Encoding.UTF8.GetBytes(content), attributes, ChecksumSet.Empty, lastModified: lastModified);
    }

    /// <summary>Reads <paramref name="content"/> to its end and stores it; the stream is not disposed. See <see cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/>.</summary>
    /// <inheritdoc cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)" path="/exception"/>
    /// <param name="bucket">An existing bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="content">The object's content, read from the current position.</param>
    /// <param name="options">Content-Type, metadata, tags and LastModified; S3's defaults when null.</param>
    /// <returns>The stored object.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "See the ReadOnlySpan overload.")]
    public S3Object Seed(string bucket, string key, Stream content, SeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (b, attributes, lastModified) = PrepareSeed(bucket, key, options); // validate first: a failed Seed leaves the stream unread
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return b.Put(key, buffer.ToArray(), attributes, ChecksumSet.Empty, lastModified: lastModified);
    }

    /// <summary>Checks everything that does not depend on the content, before the content is read.</summary>
    private (Bucket Bucket, ObjectAttributes Attributes, DateTimeOffset? LastModified) PrepareSeed(string bucket, string key, SeedOptions? options)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentException.ThrowIfNullOrEmpty(key); // S3 keys are at least one byte
        var b = ExistingBucket(bucket, $"seed '{key}'");

        var attributes = ObjectAttributes.Default;
        if (options is not null)
        {
            attributes = attributes with
            {
                ContentType = SeedContentType(options.ContentType) ?? attributes.ContentType,
                UserMetadata = SeedMetadata(options.Metadata, attributes.UserMetadata),
                Tags = ObjectTags.Create(SeedTags(options.Tags)),
            };
        }

        ObjectLimits.Check(key, attributes); // here, not only in Put, so a failed Seed leaves a stream unread
        return (b, attributes, options?.LastModified?.ToUniversalTime());
    }

    private Bucket ExistingBucket(string bucket, string action)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Store.TryGetBucket(bucket, out var b)
            ? b
            : throw new InvalidOperationException(
                $"Cannot {action}: bucket '{bucket}' does not exist. Create it with InMemoryBucketOptions.CreateBuckets(\"{bucket}\") or through the client first.");
    }

    /// <summary>The validated, trimmed Content-Type, or null for S3's default when it is null, empty or whitespace.</summary>
    private static string? SeedContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? null : HeaderValue(contentType, nameof(SeedOptions.ContentType));

    /// <summary>A value that can go into a response header as it is: printable ASCII and tab, surrounding whitespace trimmed as HTTP does.</summary>
    private static string HeaderValue(string? value, string what)
        => value is not null && value.All(c => c is '\t' or (>= ' ' and <= '~'))
            ? value.Trim(' ', '\t')
            : throw new ArgumentException($"{what} must be printable ASCII; S3 needs RFC 2047 encoding for anything else.", what);

    /// <summary>
    /// Applies what the HTTP path gets for free: names are header tokens, values are printable ASCII (S3 itself needs
    /// RFC 2047 for anything else) with surrounding whitespace trimmed as HTTP does, names are lower-cased.
    /// </summary>
    private static ImmutableDictionary<string, string> SeedMetadata(IDictionary<string, string> source, ImmutableDictionary<string, string> metadata)
    {
        foreach (var (name, value) in source)
        {
            if (name.Length == 0 || !name.All(IsHeaderTokenChar))
            {
                throw new ArgumentException($"Metadata name '{name}' is not a valid HTTP header name.", nameof(SeedOptions.Metadata));
            }

            metadata = metadata.SetItem(name.ToLowerInvariant(), HeaderValue(value, $"Metadata '{name}'"));
        }

        return metadata;
    }

    private static IDictionary<string, string> SeedTags(IDictionary<string, string> tags)
    {
        foreach (var (name, value) in tags)
        {
            if (value is null)
            {
                throw new ArgumentException($"Tag '{name}' has a null value.", nameof(SeedOptions.Tags));
            }
        }

        return tags;
    }

    // RFC 9110 token characters, the only ones an x-amz-meta-* header name can carry.
    private static bool IsHeaderTokenChar(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary>The object stored under <paramref name="key"/>, or null when there is none.</summary>
    /// <param name="bucket">An existing bucket.</param>
    /// <param name="key">The object key.</param>
    /// <returns>An immutable snapshot of the object, or null.</returns>
    /// <exception cref="InvalidOperationException">The bucket does not exist, so a typo in its name fails the test instead of passing as "no object".</exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public S3Object? GetObject(string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExistingBucket(bucket, $"get '{key}'").TryGet(key, out var obj) ? obj : null;
    }

    /// <summary>The keys in <paramref name="bucket"/> that start with <paramref name="prefix"/>, in key order.</summary>
    /// <param name="bucket">An existing bucket.</param>
    /// <param name="prefix">Only keys starting with it, compared ordinally; empty for all.</param>
    /// <returns>The matching keys.</returns>
    /// <exception cref="InvalidOperationException">The bucket does not exist, so a typo in its name fails the test instead of passing as "empty".</exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public IReadOnlyList<string> ListKeys(string bucket, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ExistingBucket(bucket, "list keys").Objects.Select(o => o.Key).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>
    /// Drops everything stored. Afterwards <see cref="Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/>,
    /// <see cref="GetObject"/>, <see cref="ListKeys"/> and <see cref="Reset"/> throw <see cref="ObjectDisposedException"/>;
    /// handlers already handed out keep answering from the empty store until their clients are disposed.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        Store.Clear();
    }

    private void CreateConfiguredBuckets()
    {
        foreach (var name in Options.Buckets)
        {
            Store.TryCreateBucket(name, out _);
        }
    }
}
