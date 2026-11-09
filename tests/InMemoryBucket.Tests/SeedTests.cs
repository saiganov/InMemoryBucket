using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

/// <summary><see cref="InMemoryBucketServer.Seed(string, string, ReadOnlySpan{byte}, SeedOptions?)"/> and read-only inspection of the store.</summary>
public sealed class SeedTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o => o.CreateBuckets("uploads"));
    private readonly AmazonS3Client _s3;

    public SeedTests()
    {
        _s3 = _server.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _s3.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task Seed_with_options_is_visible_through_the_sdk()
    {
        var lastModified = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        _server.Seed("uploads", "report.json", "{}", new()
        {
            ContentType = "application/json",
            Metadata = { ["Owner"] = "alice" },
            Tags = { ["team"] = "billing" },
            LastModified = lastModified,
        });

        using var get = await _s3.GetObjectAsync("uploads", "report.json", Ct);
        Assert.Equal("application/json", get.Headers.ContentType);
        Assert.Equal("alice", get.Metadata["owner"]);
        Assert.Equal(lastModified.UtcDateTime, get.LastModified!.Value.ToUniversalTime());
        Assert.Equal(1, get.TagCount);

        var tagging = await _s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = "uploads", Key = "report.json" }, Ct);
        var tag = Assert.Single(tagging.Tagging);
        Assert.Equal(("team", "billing"), (tag.Key, tag.Value));
    }

    [Fact]
    public async Task Seed_without_options_uses_S3_defaults()
    {
        var stored = _server.Seed("uploads", "plain", "text");

        using var get = await _s3.GetObjectAsync("uploads", "plain", Ct);
        Assert.Equal("binary/octet-stream", get.Headers.ContentType);
        Assert.Empty(get.Metadata.Keys);
        Assert.Equal(stored.ETag, get.ETag);
    }

    [Fact]
    public void Seed_string_is_utf8()
    {
        var stored = _server.Seed("uploads", "utf8", "привет");

        Assert.Equal("привет"u8.ToArray(), stored.Data.ToArray());
    }

    [Fact]
    public void Seed_stream_reads_from_the_current_position_and_leaves_it_open()
    {
        using var stream = new MemoryStream("skip|keep"u8.ToArray());
        stream.Position = 5;

        var stored = _server.Seed("uploads", "from-stream", stream);

        Assert.Equal("keep", Encoding.UTF8.GetString(stored.Data.Span));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Seed_into_a_missing_bucket_throws_instead_of_creating_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _server.Seed("uplaods", "k", "v"));

        Assert.Contains("'uplaods'", error.Message);
        Assert.False(_server.Store.TryGetBucket("uplaods", out _));
    }

    [Fact]
    public void Seed_applies_S3_limits()
    {
        var tag = Assert.Throws<S3Exception>(() => _server.Seed("uploads", "k", "v", new() { Tags = { ["aws:reserved"] = "x" } }));
        Assert.Equal(S3ErrorCode.InvalidTag, tag.Code);

        var metadata = Assert.Throws<S3Exception>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["big"] = new string('x', 2048) } }));
        Assert.Equal(S3ErrorCode.MetadataTooLarge, metadata.Code);

        Assert.False(_server.Store.GetBucket("uploads").TryGet("k", out _));
    }

    [Fact]
    public async Task Store_shows_buckets_objects_and_in_progress_uploads()
    {
        _server.Seed("uploads", "done.txt", "done");
        var upload = await _s3.InitiateMultipartUploadAsync("uploads", "big.bin", Ct);

        var bucket = Assert.Single(_server.Store.Buckets);
        Assert.Equal("uploads", bucket.Name);
        Assert.Equal("done.txt", Assert.Single(bucket.Objects).Key);
        var pending = Assert.Single(bucket.Uploads);
        Assert.Equal(("big.bin", upload.UploadId), (pending.Key, pending.UploadId));
    }

    [Fact]
    public void Seed_takes_byte_arrays_utf8_literals_and_collection_expressions()
    {
        Assert.Equal("abc", Encoding.UTF8.GetString(_server.Seed("uploads", "array", "abc"u8.ToArray()).Data.Span));
        Assert.Equal("abc", Encoding.UTF8.GetString(_server.Seed("uploads", "literal", "abc"u8).Data.Span));
        Assert.Equal([1, 2, 3], _server.Seed("uploads", "collection", [1, 2, 3]).Data.ToArray());
    }

    [Fact]
    public void LastModified_defaults_to_the_servers_clock_and_is_stored_in_utc()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero));
        using var server = new InMemoryBucketServer(o =>
        {
            o.TimeProvider = clock;
            o.CreateBuckets("clock");
        });

        Assert.Equal(clock.GetUtcNow(), server.Seed("clock", "now", "x").LastModified);

        var local = new DateTimeOffset(2020, 1, 2, 8, 0, 0, TimeSpan.FromHours(5));
        var stored = server.Seed("clock", "then", "x", new() { LastModified = local });
        Assert.Equal(TimeSpan.Zero, stored.LastModified.Offset);
        Assert.Equal(local, stored.LastModified);
    }

    [Fact]
    public void Seed_rejects_metadata_the_http_path_could_not_carry()
    {
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["bad name"] = "x" } }));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["é"] = "x" } }));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["ok"] = "x\r\ny: z" } }));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["ok"] = null! } }));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Tags = { ["team"] = null! } }));

        Assert.False(_server.Store.GetBucket("uploads").TryGet("k", out _));
    }

    [Fact]
    public void Seed_lower_cases_metadata_names()
    {
        var stored = _server.Seed("uploads", "k", "v", new() { Metadata = { ["Owner-Name"] = "alice" } });

        Assert.Equal("alice", Assert.Single(stored.Attributes.UserMetadata, m => m.Key == "owner-name").Value);
    }

    [Fact]
    public void Seed_rejects_a_key_over_1024_bytes()
    {
        var error = Assert.Throws<S3Exception>(() => _server.Seed("uploads", new string('k', 1025), "v"));

        Assert.Equal(S3ErrorCode.KeyTooLongError, error.Code);
    }

    [Fact]
    public void Store_collections_are_snapshots()
    {
        _server.Seed("uploads", "first", "1");
        var objects = _server.Store.GetBucket("uploads").Objects;

        _server.Seed("uploads", "second", "2");

        Assert.Equal("first", Assert.Single(objects).Key);
    }

    [Fact]
    public void Seed_rejects_non_ascii_metadata_values_and_trims_whitespace()
    {
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { Metadata = { ["city"] = "café" } }));

        var stored = _server.Seed("uploads", "k", "v", new() { Metadata = { ["pad"] = "\t padded \t" } });
        Assert.Equal("padded", stored.Attributes.UserMetadata["pad"]);
    }

    [Fact]
    public void A_failed_stream_seed_leaves_the_stream_unread()
    {
        using var stream = new MemoryStream("data"u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => _server.Seed("missing", "k", stream));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", stream, new() { Metadata = { ["bad name"] = "x" } }));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Seed_rejects_a_content_type_that_cannot_be_a_header()
    {
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { ContentType = "text/plain\r\nX-Evil: 1" }));
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "k", "v", new() { ContentType = "text/plain; name=é" }));

        Assert.False(_server.Store.GetBucket("uploads").TryGet("k", out _));
    }

    [Fact]
    public void A_stream_seed_that_breaks_S3_limits_leaves_the_stream_unread()
    {
        using var stream = new MemoryStream("data"u8.ToArray());

        Assert.Throws<S3Exception>(() => _server.Seed("uploads", new string('k', 1025), stream));
        Assert.Throws<S3Exception>(() => _server.Seed("uploads", "k", stream, new() { Metadata = { ["big"] = new string('x', 3000) } }));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Read_helpers_fail_on_a_missing_bucket_and_return_nothing_for_a_missing_key()
    {
        Assert.Null(_server.GetObject("uploads", "missing"));
        Assert.Empty(_server.ListKeys("uploads", "missing/"));

        Assert.Throws<InvalidOperationException>(() => _server.GetObject("uplaods", "k"));
        Assert.Throws<InvalidOperationException>(() => _server.ListKeys("uplaods"));
    }

    [Fact]
    public void Seed_after_dispose_says_so()
    {
        var server = new InMemoryBucketServer(o => o.CreateBuckets("disposed"));
        server.Dispose();

        Assert.Throws<ObjectDisposedException>(() => server.Seed("disposed", "k", "v"));
        Assert.Throws<ObjectDisposedException>(() => server.GetObject("disposed", "k"));
        Assert.Throws<ObjectDisposedException>(server.Reset);
    }

    [Fact]
    public void A_blank_content_type_falls_back_to_the_default()
    {
        Assert.Equal("binary/octet-stream", _server.Seed("uploads", "blank", "v", new() { ContentType = "   " }).Attributes.ContentType);
        Assert.Equal("text/plain", _server.Seed("uploads", "padded", "v", new() { ContentType = " text/plain " }).Attributes.ContentType);
    }

    [Fact]
    public void Seed_rejects_an_empty_key()
    {
        Assert.Throws<ArgumentException>(() => _server.Seed("uploads", "", "v"));

        Assert.Empty(_server.ListKeys("uploads"));
    }
}
