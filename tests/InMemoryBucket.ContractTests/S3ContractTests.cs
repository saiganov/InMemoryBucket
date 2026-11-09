using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;

namespace InMemoryBucket.ContractTests;

/// <summary>
/// The S3 contract InMemoryBucket promises, written against <see cref="IAmazonS3"/> only so the same tests run against
/// the fake on every PR and against MinIO nightly. Every test gets its own bucket and cleans it up.
/// </summary>
public abstract class S3ContractTests : IAsyncLifetime
{
    private static readonly Random Rng = new();

    protected S3ContractTests(S3Backend backend)
    {
        Backend = backend;
        S3 = backend.Client;
    }

    protected S3Backend Backend { get; }

    protected IAmazonS3 S3 { get; }

    /// <summary>A fresh bucket for this test.</summary>
    protected string Bucket { get; private set; } = string.Empty;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Bucket = NewBucketName();
        await S3.PutBucketAsync(Bucket, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await DeleteBucketRecursivelyAsync(Bucket);
    }

    protected static string NewBucketName() => "contract-" + Guid.NewGuid().ToString("N")[..16];

    protected async Task DeleteBucketRecursivelyAsync(string bucket)
    {
        try
        {
            var uploads = await S3.ListMultipartUploadsAsync(bucket, CancellationToken.None);
            foreach (var upload in uploads.MultipartUploads ?? [])
            {
                await S3.AbortMultipartUploadAsync(bucket, upload.Key, upload.UploadId, CancellationToken.None);
            }

            ListObjectsV2Response page;
            var request = new ListObjectsV2Request { BucketName = bucket };
            do
            {
                page = await S3.ListObjectsV2Async(request, CancellationToken.None);
                if (page.S3Objects is { Count: > 0 } objects)
                {
                    await S3.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = bucket,
                        Objects = objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                        Quiet = true,
                    }, CancellationToken.None);
                }

                request.ContinuationToken = page.NextContinuationToken;
            }
            while (page.IsTruncated == true);

            await S3.DeleteBucketAsync(bucket, CancellationToken.None);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchBucket")
        {
            // Already gone: the test deleted it itself.
        }
    }

    protected async Task PutAsync(string key, string body, string? contentType = null, Action<PutObjectRequest>? configure = null)
    {
        var request = new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = body };
        if (contentType is not null)
        {
            request.ContentType = contentType;
        }

        configure?.Invoke(request);
        await S3.PutObjectAsync(request, Ct);
    }

    protected async Task<string> ReadAsync(string key, string? bucket = null)
    {
        using var response = await S3.GetObjectAsync(bucket ?? Bucket, key, Ct);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync(Ct);
    }

    protected static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        lock (Rng)
        {
            Rng.NextBytes(bytes);
        }

        return bytes;
    }

    protected static string Md5Hex(ReadOnlySpan<byte> data) => "\"" + Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant() + "\"";

    // ---------------------------------------------------------------- objects

    [Fact]
    public async Task Put_then_get_roundtrips_bytes_metadata_and_content_type()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "docs/hello.txt",
            ContentBody = "hello world",
            ContentType = "text/plain",
            Metadata = { ["owner"] = "vasilii" },
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, put.HttpStatusCode);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", put.ETag);

        using var get = await S3.GetObjectAsync(Bucket, "docs/hello.txt", Ct);
        using var reader = new StreamReader(get.ResponseStream);

        Assert.Equal("hello world", await reader.ReadToEndAsync(Ct));
        Assert.Equal("text/plain", get.Headers.ContentType);
        Assert.Equal("vasilii", get.Metadata["owner"]);
        Assert.Equal(put.ETag, get.ETag);
        Assert.Equal(11, get.ContentLength);
    }

    [Fact]
    public async Task Head_object_returns_metadata_without_a_body()
    {
        await PutAsync("meta", "0123456789", "application/json", r =>
        {
            r.Metadata["Trace-Id"] = "abc";
            r.Headers.CacheControl = "max-age=60";
        });

        var head = await S3.GetObjectMetadataAsync(Bucket, "meta", Ct);

        Assert.Equal(10, head.ContentLength);
        Assert.Equal("application/json", head.Headers.ContentType);
        Assert.Equal("abc", head.Metadata["trace-id"]);
        Assert.Equal("max-age=60", head.Headers.CacheControl);
        Assert.Equal(Md5Hex("0123456789"u8), head.ETag);
        Assert.True(head.LastModified > DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task Put_overwrites_an_existing_key()
    {
        await PutAsync("k", "one");
        await PutAsync("k", "two", "text/plain");

        using var get = await S3.GetObjectAsync(Bucket, "k", Ct);
        Assert.Equal("two", await new StreamReader(get.ResponseStream).ReadToEndAsync(Ct));
        Assert.Equal("text/plain", get.Headers.ContentType);
    }

    [Fact]
    public async Task Put_with_checksum_algorithm_is_verified_and_echoed()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "checked",
            ContentBody = "hello world",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        }, Ct);

        Assert.Equal("uU0nuZNNPgilLlLX2n2r+sSE7+N6U4DukIj3rOLvzek=", put.ChecksumSHA256);

        var head = await S3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = Bucket,
            Key = "checked",
            ChecksumMode = ChecksumMode.ENABLED,
        }, Ct);

        Assert.Equal("uU0nuZNNPgilLlLX2n2r+sSE7+N6U4DukIj3rOLvzek=", head.ChecksumSHA256);
    }

    [Fact]
    public async Task Keys_with_spaces_slashes_and_unicode_survive_url_encoding()
    {
        var keys = new[] { "folder with space/файл №1.txt", "a+b&c=d.txt", "emoji/🙂.bin" };
        foreach (var key in keys)
        {
            await PutAsync(key, key);
        }

        foreach (var key in keys)
        {
            Assert.Equal(key, await ReadAsync(key));
        }

        var raw = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket }, Ct);
        Assert.Equal(keys.Order(StringComparer.Ordinal), raw.S3Objects.Select(o => o.Key));

        // With encoding-type=url the SDK hands back the percent-encoded keys as S3 sent them; they must decode to the originals.
        // S3 uses form encoding here (space is "+", "/" is kept), so WebUtility.UrlDecode is the right inverse.
        var encoded = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Encoding = EncodingType.Url }, Ct);
        Assert.Equal(keys.Order(StringComparer.Ordinal), encoded.S3Objects.Select(o => WebUtility.UrlDecode(o.Key)));
        Assert.All(encoded.S3Objects, o => Assert.DoesNotContain(' ', o.Key));
        Assert.Contains("a%2Bb%26c%3Dd.txt", encoded.S3Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task Range_get_returns_partial_content()
    {
        await PutAsync("range", "0123456789");

        using var middle = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = "range", ByteRange = new ByteRange(2, 5) }, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, middle.HttpStatusCode);
        Assert.Equal("2345", await new StreamReader(middle.ResponseStream).ReadToEndAsync(Ct));
        Assert.Equal("bytes 2-5/10", middle.ContentRange);

        using var tail = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = "range", ByteRange = new ByteRange("bytes=-3") }, Ct);
        Assert.Equal("789", await new StreamReader(tail.ResponseStream).ReadToEndAsync(Ct));

        using var open = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = "range", ByteRange = new ByteRange("bytes=7-") }, Ct);
        Assert.Equal("789", await new StreamReader(open.ResponseStream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Range_past_the_end_is_InvalidRange()
    {
        await PutAsync("range", "0123456789");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "range", ByteRange = new ByteRange(20, 30) }, Ct));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        Assert.Equal("InvalidRange", error.ErrorCode);
    }

    [Fact]
    public async Task Conditional_get_honours_etag_and_date_headers()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "cond", ContentBody = "v1" }, Ct);

        using var matched = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = "cond", EtagToMatch = put.ETag }, Ct);
        Assert.Equal(HttpStatusCode.OK, matched.HttpStatusCode);

        var notModified = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "cond", EtagToNotMatch = put.ETag }, Ct));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        var mismatch = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "cond", EtagToMatch = "\"00000000000000000000000000000000\"" }, Ct));
        Assert.Equal(HttpStatusCode.PreconditionFailed, mismatch.StatusCode);

        var unchangedSince = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "cond", ModifiedSinceDate = DateTime.UtcNow.AddDays(1) }, Ct));
        Assert.Equal(HttpStatusCode.NotModified, unchangedSince.StatusCode);

        using var modifiedSinceLongAgo = await S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "cond", ModifiedSinceDate = DateTime.UtcNow.AddDays(-1) }, Ct);
        Assert.Equal(HttpStatusCode.OK, modifiedSinceLongAgo.HttpStatusCode);

        var modifiedSince = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(
            new GetObjectRequest { BucketName = Bucket, Key = "cond", UnmodifiedSinceDate = DateTime.UtcNow.AddDays(-1) }, Ct));
        Assert.Equal(HttpStatusCode.PreconditionFailed, modifiedSince.StatusCode);
    }

    // ---------------------------------------------------------------- tagging

    private async Task<Dictionary<string, string>> TagsAsync(string key)
    {
        var response = await S3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = Bucket, Key = key }, Ct);
        return (response.Tagging ?? []).ToDictionary(t => t.Key, t => t.Value);
    }

    [Fact]
    public async Task PutObject_stores_tags_from_the_tagging_header()
    {
        await PutAsync("tagged", "x", configure: r => r.TagSet = [new Tag { Key = "env", Value = "test run" }, new Tag { Key = "team", Value = "a+b" }]);

        Assert.Equal(new Dictionary<string, string> { ["env"] = "test run", ["team"] = "a+b" }, await TagsAsync("tagged"));

        using var get = await S3.GetObjectAsync(Bucket, "tagged", Ct);
        Assert.Equal(2, get.TagCount);
    }

    [Fact]
    public async Task Put_and_delete_object_tagging_keep_the_object_itself()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "t", ContentBody = "x" }, Ct);
        Assert.Empty(await TagsAsync("t"));

        await S3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = Bucket,
            Key = "t",
            Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "v" }, new Tag { Key = "blank", Value = " " }] },
        }, Ct);
        Assert.Equal(new Dictionary<string, string> { ["k"] = "v", ["blank"] = " " }, await TagsAsync("t"));

        var head = await S3.GetObjectMetadataAsync(Bucket, "t", Ct);
        Assert.Equal(put.ETag, head.ETag);

        await S3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = Bucket, Key = "t" }, Ct);
        Assert.Empty(await TagsAsync("t"));
        Assert.Equal("x", await ReadAsync("t"));
    }

    [Fact]
    public async Task Tagging_a_missing_key_is_NoSuchKey()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => TagsAsync("nope"));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("NoSuchKey", error.ErrorCode);
    }

    [Fact]
    public async Task CopyObject_copies_tags_or_replaces_them_by_directive()
    {
        await PutAsync("src", "x", configure: r => r.TagSet = [new Tag { Key = "from", Value = "source" }]);

        await S3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = Bucket, SourceKey = "src", DestinationBucket = Bucket, DestinationKey = "copied" }, Ct);
        Assert.Equal(new Dictionary<string, string> { ["from"] = "source" }, await TagsAsync("copied"));

        await S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = "src",
            DestinationBucket = Bucket,
            DestinationKey = "replaced",
            TaggingDirective = TaggingDirective.REPLACE,
            TagSet = [new Tag { Key = "new", Value = "tag" }],
        }, Ct);
        Assert.Equal(new Dictionary<string, string> { ["new"] = "tag" }, await TagsAsync("replaced"));
    }

    [Fact]
    public async Task Multipart_upload_keeps_tags_from_the_initiate_request()
    {
        var init = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = "mp-tags",
            TagSet = [new Tag { Key = "kind", Value = "multipart" }],
        }, Ct);
        var part = await S3.UploadPartAsync(new UploadPartRequest { BucketName = Bucket, Key = "mp-tags", UploadId = init.UploadId, PartNumber = 1, InputStream = new MemoryStream(RandomBytes(16)), PartSize = 16 }, Ct);
        await S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = Bucket, Key = "mp-tags", UploadId = init.UploadId, PartETags = [new PartETag(1, part.ETag)] }, Ct);

        Assert.Equal(new Dictionary<string, string> { ["kind"] = "multipart" }, await TagsAsync("mp-tags"));
    }

    [Fact]
    public async Task Tag_limits_are_InvalidTag()
    {
        await PutAsync("limits", "x");

        var tooMany = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = Bucket,
            Key = "limits",
            Tagging = new Tagging { TagSet = Enumerable.Range(0, 11).Select(i => new Tag { Key = "k" + i, Value = "v" }).ToList() },
        }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("InvalidTag", tooMany.ErrorCode);

        var longKey = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("limits2", "x", configure: r => r.TagSet = [new Tag { Key = new string('k', 129), Value = "v" }]));
        Assert.Equal("InvalidTag", longKey.ErrorCode);

        var longValue = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("limits3", "x", configure: r => r.TagSet = [new Tag { Key = "k", Value = new string('v', 257) }]));
        Assert.Equal("InvalidTag", longValue.ErrorCode);

        Assert.Empty(await TagsAsync("limits"));
    }

    [Fact]
    public async Task PutObject_with_If_None_Match_star_writes_only_a_new_key()
    {
        await PutAsync("once", "first", configure: r => r.IfNoneMatch = "*");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("once", "second", configure: r => r.IfNoneMatch = "*"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Equal("PreconditionFailed", error.ErrorCode);
        Assert.Equal("first", await ReadAsync("once"));
    }

    [Fact]
    public async Task PutObject_with_If_Match_replaces_only_the_expected_version()
    {
        var v1 = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "cas", ContentBody = "v1" }, Ct);

        var v2 = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "cas", ContentBody = "v2", IfMatch = v1.ETag }, Ct);
        Assert.Equal(HttpStatusCode.OK, v2.HttpStatusCode);

        var stale = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("cas", "v3", configure: r => r.IfMatch = v1.ETag));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("v2", await ReadAsync("cas"));

        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("cas-missing", "v1", configure: r => r.IfMatch = v1.ETag));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("NoSuchKey", missing.ErrorCode);
    }

    [Fact]
    public async Task Racing_PutObjects_with_If_None_Match_star_let_exactly_one_win()
    {
        var attempts = Enumerable.Range(0, 16).Select(async i =>
        {
            try
            {
                await PutAsync("race", "writer-" + i, configure: r => r.IfNoneMatch = "*");
                return true;
            }
            catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                return false;
            }
        });

        var won = await Task.WhenAll(attempts);

        Assert.Single(won, w => w);
        Assert.StartsWith("writer-", await ReadAsync("race"));
    }

    [Fact]
    public async Task CompleteMultipartUpload_honours_If_None_Match_star()
    {
        await PutAsync("mp-cond", "existing");
        var init = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "mp-cond" }, Ct);
        var part = await S3.UploadPartAsync(new UploadPartRequest { BucketName = Bucket, Key = "mp-cond", UploadId = init.UploadId, PartNumber = 1, InputStream = new MemoryStream(RandomBytes(16)), PartSize = 16 }, Ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = "mp-cond",
            UploadId = init.UploadId,
            PartETags = [new PartETag(1, part.ETag)],
            IfNoneMatch = "*",
        }, Ct));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Equal("existing", await ReadAsync("mp-cond"));
        await S3.AbortMultipartUploadAsync(Bucket, "mp-cond", init.UploadId, Ct);
    }

    // ---------------------------------------------------------------- limits

    /// <summary>A key of exactly <paramref name="bytes"/> UTF-8 bytes, split into short segments so file-backed servers accept it.</summary>
    private static string KeyOfBytes(int bytes, char fill = 'k')
    {
        var builder = new System.Text.StringBuilder();
        var size = 0;
        var charBytes = System.Text.Encoding.UTF8.GetByteCount(fill.ToString());
        while (size < bytes)
        {
            if (builder.Length > 0 && builder.Length % 100 == 99 && bytes - size >= 1)
            {
                builder.Append('/');
                size += 1;
            }
            else if (bytes - size >= charBytes)
            {
                builder.Append(fill);
                size += charBytes;
            }
            else
            {
                builder.Append('k');
                size += 1;
            }
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Keys_longer_than_1024_bytes_are_KeyTooLongError()
    {
        var longest = KeyOfBytes(1024);
        await PutAsync(longest, "fits");
        Assert.Equal("fits", await ReadAsync(longest));

        var tooLong = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync(KeyOfBytes(1025), "x"));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("KeyTooLongError", tooLong.ErrorCode);

        // The limit counts bytes, not characters: about 520 two-byte characters are already too long.
        var multibyte = KeyOfBytes(1025, 'é');
        Assert.True(multibyte.Length < 1024);
        var tooManyBytes = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync(multibyte, "x"));
        Assert.Equal("KeyTooLongError", tooManyBytes.ErrorCode);

        var multipart = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = Bucket, Key = KeyOfBytes(1025) }, Ct));
        Assert.Equal("KeyTooLongError", multipart.ErrorCode);
    }

    [Fact]
    public async Task User_metadata_over_2_KB_is_MetadataTooLarge()
    {
        await PutAsync("meta-ok", "x", configure: r => r.Metadata["note"] = new string('m', 1500));
        var head = await S3.GetObjectMetadataAsync(Bucket, "meta-ok", Ct);
        Assert.Equal(1500, head.Metadata["note"].Length);

        var tooLarge = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("meta-big", "x", configure: r =>
        {
            r.Metadata["a"] = new string('m', 1500);
            r.Metadata["b"] = new string('m', 1500);
        }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal("MetadataTooLarge", tooLarge.ErrorCode);

        var copy = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
        {
            var request = new CopyObjectRequest
            {
                SourceBucket = Bucket,
                SourceKey = "meta-ok",
                DestinationBucket = Bucket,
                DestinationKey = "meta-copy",
                MetadataDirective = S3MetadataDirective.REPLACE,
            };
            request.Metadata["big"] = new string('m', 3000);
            return S3.CopyObjectAsync(request, Ct);
        });
        Assert.Equal("MetadataTooLarge", copy.ErrorCode);
    }

    [Fact]
    public async Task New_objects_report_default_encryption_where_the_backend_applies_it()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "sse", ContentBody = "x" }, Ct);
        var head = await S3.GetObjectMetadataAsync(Bucket, "sse", Ct);

        var expected = Backend.ReportsDefaultEncryption ? ServerSideEncryptionMethod.AES256 : null;
        Assert.Equal(expected, put.ServerSideEncryptionMethod);
        Assert.Equal(expected, head.ServerSideEncryptionMethod);
    }

    [Fact]
    public async Task Missing_key_surfaces_as_NoSuchKey()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(Bucket, "nope", Ct));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("NoSuchKey", error.ErrorCode);
    }

    [Fact]
    public async Task Head_of_missing_key_is_404()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectMetadataAsync(Bucket, "nope", Ct));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Delete_object_then_get_is_NoSuchKey_and_delete_is_idempotent()
    {
        await PutAsync("gone", "x");

        var delete = await S3.DeleteObjectAsync(Bucket, "gone", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.HttpStatusCode);

        var again = await S3.DeleteObjectAsync(Bucket, "gone", Ct);
        Assert.Equal(HttpStatusCode.NoContent, again.HttpStatusCode);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectAsync(Bucket, "gone", Ct));
        Assert.Equal("NoSuchKey", error.ErrorCode);
    }

    [Fact]
    public async Task DeleteObjects_removes_many_keys_and_reports_each()
    {
        foreach (var key in new[] { "d/1", "d/2", "d/3", "keep" })
        {
            await PutAsync(key, key);
        }

        var response = await S3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects = [new KeyVersion { Key = "d/1" }, new KeyVersion { Key = "d/2" }, new KeyVersion { Key = "d/3" }, new KeyVersion { Key = "never-existed" }],
        }, Ct);

        Assert.Equal(["d/1", "d/2", "d/3", "never-existed"], response.DeletedObjects.Select(d => d.Key).Order(StringComparer.Ordinal));
        Assert.Empty(response.DeleteErrors ?? []);

        var remaining = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket }, Ct);
        Assert.Equal(["keep"], remaining.S3Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task DeleteObjects_in_quiet_mode_reports_nothing()
    {
        await PutAsync("q", "x");

        var response = await S3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects = [new KeyVersion { Key = "q" }],
            Quiet = true,
        }, Ct);

        Assert.Empty(response.DeletedObjects ?? []);
        Assert.Empty(response.DeleteErrors ?? []);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectMetadataAsync(Bucket, "q", Ct));
    }

    // ------------------------------------------------------------------- copy

    [Fact]
    public async Task CopyObject_copies_bytes_and_metadata_by_default()
    {
        await PutAsync("src", "payload", "text/plain", r => r.Metadata["origin"] = "src");

        var copy = await S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = "src",
            DestinationBucket = Bucket,
            DestinationKey = "dst",
        }, Ct);

        Assert.Equal(Md5Hex("payload"u8), copy.ETag);

        using var get = await S3.GetObjectAsync(Bucket, "dst", Ct);
        Assert.Equal("payload", await new StreamReader(get.ResponseStream).ReadToEndAsync(Ct));
        Assert.Equal("text/plain", get.Headers.ContentType);
        Assert.Equal("src", get.Metadata["origin"]);
        Assert.Equal("payload", await ReadAsync("src"));
    }

    [Fact]
    public async Task CopyObject_across_buckets_with_REPLACE_directive_replaces_metadata()
    {
        var other = NewBucketName();
        await S3.PutBucketAsync(other, Ct);
        try
        {
            await PutAsync("src", "payload", "text/plain", r => r.Metadata["origin"] = "src");

            await S3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = Bucket,
                SourceKey = "src",
                DestinationBucket = other,
                DestinationKey = "dst",
                MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = "application/octet-stream",
                Metadata = { ["origin"] = "copy" },
            }, Ct);

            using var get = await S3.GetObjectAsync(other, "dst", Ct);
            Assert.Equal("payload", await new StreamReader(get.ResponseStream).ReadToEndAsync(Ct));
            Assert.Equal("application/octet-stream", get.Headers.ContentType);
            Assert.Equal("copy", get.Metadata["origin"]);
        }
        finally
        {
            await DeleteBucketRecursivelyAsync(other);
        }
    }

    [Fact]
    public async Task CopyObject_onto_itself_without_changes_is_InvalidRequest()
    {
        await PutAsync("same", "x");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.CopyObjectAsync(Bucket, "same", Bucket, "same", Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("InvalidRequest", error.ErrorCode);
    }

    [Fact]
    public async Task CopyObject_onto_itself_with_REPLACE_updates_metadata()
    {
        await PutAsync("same", "x", "text/plain");

        await S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = "same",
            DestinationBucket = Bucket,
            DestinationKey = "same",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "text/html",
        }, Ct);

        var head = await S3.GetObjectMetadataAsync(Bucket, "same", Ct);
        Assert.Equal("text/html", head.Headers.ContentType);
    }

    [Fact]
    public async Task CopyObject_from_missing_source_is_NoSuchKey()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.CopyObjectAsync(Bucket, "missing", Bucket, "dst", Ct));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("NoSuchKey", error.ErrorCode);
    }

    [Fact]
    public async Task CopyObject_with_failing_source_precondition_is_PreconditionFailed()
    {
        await PutAsync("src", "x");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = "src",
            DestinationBucket = Bucket,
            DestinationKey = "dst",
            ETagToMatch = "\"00000000000000000000000000000000\"",
        }, Ct));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Equal("PreconditionFailed", error.ErrorCode);
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task ListObjectsV2_with_prefix_delimiter_and_paging()
    {
        foreach (var key in new[] { "a/1", "a/2", "b/1", "root1", "root2", "root3" })
        {
            await PutAsync(key, key);
        }

        var top = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Delimiter = "/" }, Ct);
        Assert.Equal(["root1", "root2", "root3"], top.S3Objects.Select(o => o.Key));
        Assert.Equal(["a/", "b/"], top.CommonPrefixes);
        Assert.Equal(5, top.KeyCount);

        var under = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = "a/" }, Ct);
        Assert.Equal(["a/1", "a/2"], under.S3Objects.Select(o => o.Key));

        var paged = new List<string>();
        var request = new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 2 };
        ListObjectsV2Response page;
        var pages = 0;
        do
        {
            page = await S3.ListObjectsV2Async(request, Ct);
            pages++;
            paged.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        Assert.Equal(Backend.MayReturnTrailingEmptyPage ? 4 : 3, pages);
        Assert.Equal(["a/1", "a/2", "b/1", "root1", "root2", "root3"], paged);
    }

    [Fact]
    public async Task ListObjectsV2_start_after_and_empty_results()
    {
        foreach (var key in new[] { "k1", "k2", "k3" })
        {
            await PutAsync(key, key);
        }

        var after = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, StartAfter = "k1" }, Ct);
        Assert.Equal(["k2", "k3"], after.S3Objects.Select(o => o.Key));

        var none = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = "zzz" }, Ct);
        Assert.Empty(none.S3Objects ?? []);
        Assert.NotEqual(true, none.IsTruncated);
    }

    [Fact]
    public async Task ListObjects_v1_pages_with_markers()
    {
        foreach (var key in new[] { "a/1", "a/2", "b/1", "root1", "root2" })
        {
            await PutAsync(key, key);
        }

        var top = await S3.ListObjectsAsync(new ListObjectsRequest { BucketName = Bucket, Delimiter = "/" }, Ct);
        Assert.Equal(["root1", "root2"], top.S3Objects.Select(o => o.Key));
        Assert.Equal(["a/", "b/"], top.CommonPrefixes);
        Assert.NotNull(top.S3Objects[0].Owner);

        var paged = new List<string>();
        var request = new ListObjectsRequest { BucketName = Bucket, MaxKeys = 2 };
        ListObjectsResponse page;
        do
        {
            page = await S3.ListObjectsAsync(request, Ct);
            paged.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            request.Marker = page.NextMarker ?? page.S3Objects?.LastOrDefault()?.Key;
        }
        while (page.IsTruncated == true);

        Assert.Equal(["a/1", "a/2", "b/1", "root1", "root2"], paged);
    }

    [Fact]
    public async Task ListObjectsV2_pages_through_common_prefixes_one_at_a_time()
    {
        foreach (var key in new[] { "a/1", "a/2", "a/3", "b/1", "c" })
        {
            await PutAsync(key, key);
        }

        var seen = new List<string>();
        var request = new ListObjectsV2Request { BucketName = Bucket, Delimiter = "/", MaxKeys = 1 };
        ListObjectsV2Response page;
        var pages = 0;
        do
        {
            page = await S3.ListObjectsV2Async(request, Ct);
            Assert.True(++pages <= 5, "listing did not terminate");
            seen.AddRange(page.CommonPrefixes ?? []);
            seen.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        Assert.Equal(["a/", "b/", "c"], seen);
    }

    [Fact]
    public async Task ListObjects_v1_pages_through_common_prefixes_one_at_a_time()
    {
        foreach (var key in new[] { "a/1", "a/2", "a/3", "b/1", "c" })
        {
            await PutAsync(key, key);
        }

        var seen = new List<string>();
        var request = new ListObjectsRequest { BucketName = Bucket, Delimiter = "/", MaxKeys = 1 };
        ListObjectsResponse page;
        var pages = 0;
        do
        {
            page = await S3.ListObjectsAsync(request, Ct);
            Assert.True(++pages <= 5, "listing did not terminate");
            seen.AddRange(page.CommonPrefixes ?? []);
            seen.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            request.Marker = page.NextMarker;
        }
        while (page.IsTruncated == true);

        Assert.Equal(["a/", "b/", "c"], seen);
    }

    [Fact]
    public async Task ListObjectsV2_reports_size_etag_and_last_modified()
    {
        var put = await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "sized", ContentBody = "0123456789" }, Ct);

        var listed = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket }, Ct);
        var entry = Assert.Single(listed.S3Objects);

        Assert.Equal("sized", entry.Key);
        Assert.Equal(10, entry.Size);
        Assert.Equal(put.ETag, entry.ETag);
        Assert.True(entry.LastModified > DateTime.UtcNow.AddDays(-1));
    }

    // ---------------------------------------------------------------- buckets

    [Fact]
    public async Task Bucket_lifecycle_create_list_head_delete()
    {
        var name = NewBucketName();
        await S3.PutBucketAsync(name, Ct);
        try
        {
            var buckets = await S3.ListBucketsAsync(Ct);
            Assert.Contains(buckets.Buckets, b => b.BucketName == name);
            Assert.True(await AmazonS3Util.DoesS3BucketExistV2Async(S3, name));
        }
        finally
        {
            await S3.DeleteBucketAsync(name, Ct);
        }

        Assert.False(await AmazonS3Util.DoesS3BucketExistV2Async(S3, name));
    }

    [Fact]
    public async Task Missing_bucket_surfaces_as_NoSuchBucket()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => S3.PutObjectAsync(new PutObjectRequest { BucketName = NewBucketName(), Key = "k", ContentBody = "x" }, Ct));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("NoSuchBucket", error.ErrorCode);
    }

    [Fact]
    public async Task Deleting_a_non_empty_bucket_is_BucketNotEmpty()
    {
        await PutAsync("k", "x");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.DeleteBucketAsync(Bucket, Ct));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("BucketNotEmpty", error.ErrorCode);
    }

    [Fact]
    public async Task Repeated_CreateBucket_follows_the_backend_region_rule()
    {
        if (Backend.RepeatedCreateBucketConflicts)
        {
            var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.PutBucketAsync(Bucket, Ct));
            Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
            Assert.Equal("BucketAlreadyOwnedByYou", error.ErrorCode);
        }
        else
        {
            var response = await S3.PutBucketAsync(Bucket, Ct);
            Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        }
    }

    // -------------------------------------------------------------- multipart

    [Fact]
    public async Task TransferUtility_uploads_20MB_through_multipart_and_bytes_match()
    {
        var payload = RandomBytes(20 * 1024 * 1024);
        var transfer = new TransferUtility(S3, new TransferUtilityConfig { MinSizeBeforePartUpload = 5 * 1024 * 1024 });

        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = Bucket,
            Key = "big.bin",
            InputStream = new MemoryStream(payload),
            PartSize = 5 * 1024 * 1024,
            ContentType = "application/octet-stream",
        }, Ct);

        var head = await S3.GetObjectMetadataAsync(Bucket, "big.bin", Ct);
        Assert.Equal(payload.Length, head.ContentLength);
        Assert.EndsWith("-4\"", head.ETag);

        using var get = await S3.GetObjectAsync(Bucket, "big.bin", Ct);
        using var buffer = new MemoryStream();
        await get.ResponseStream.CopyToAsync(buffer, Ct);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(buffer.ToArray()));
    }

    [Fact]
    public async Task Multipart_upload_driven_by_hand_lists_parts_and_rejects_bad_etags()
    {
        var init = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "parts" }, Ct);
        var part1 = RandomBytes(5 * 1024 * 1024);
        var part2 = RandomBytes(1024);

        var up1 = await S3.UploadPartAsync(new UploadPartRequest { BucketName = Bucket, Key = "parts", UploadId = init.UploadId, PartNumber = 1, InputStream = new MemoryStream(part1), PartSize = part1.Length }, Ct);
        var up2 = await S3.UploadPartAsync(new UploadPartRequest { BucketName = Bucket, Key = "parts", UploadId = init.UploadId, PartNumber = 2, InputStream = new MemoryStream(part2), PartSize = part2.Length }, Ct);

        var listed = await S3.ListPartsAsync(Bucket, "parts", init.UploadId, Ct);
        Assert.Equal([1, 2], listed.Parts.Select(p => p.PartNumber));
        Assert.Equal(part1.Length, listed.Parts[0].Size);

        var badEtag = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = "parts",
            UploadId = init.UploadId,
            PartETags = [new PartETag(1, "\"00000000000000000000000000000000\""), new PartETag(2, up2.ETag)],
        }, Ct));
        Assert.Equal("InvalidPart", badEtag.ErrorCode);

        var done = await S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = "parts",
            UploadId = init.UploadId,
            PartETags = [new PartETag(1, up1.ETag), new PartETag(2, up2.ETag)],
        }, Ct);
        Assert.EndsWith("-2\"", done.ETag);

        var head = await S3.GetObjectMetadataAsync(Bucket, "parts", Ct);
        Assert.Equal(part1.Length + part2.Length, head.ContentLength);
    }

    [Fact]
    public async Task Abort_discards_the_upload()
    {
        var init = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "aborted" }, Ct);
        await S3.UploadPartAsync(new UploadPartRequest { BucketName = Bucket, Key = "aborted", UploadId = init.UploadId, PartNumber = 1, InputStream = new MemoryStream(RandomBytes(16)), PartSize = 16 }, Ct);

        await S3.AbortMultipartUploadAsync(Bucket, "aborted", init.UploadId, Ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.ListPartsAsync(Bucket, "aborted", init.UploadId, Ct));
        Assert.Equal("NoSuchUpload", error.ErrorCode);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => S3.GetObjectMetadataAsync(Bucket, "aborted", Ct));
    }

    [Fact]
    public async Task ListMultipartUploads_shows_in_progress_uploads_and_TransferUtility_can_abort_them()
    {
        var first = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "mp/one" }, Ct);
        var second = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "mp/two" }, Ct);
        var third = await S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "other" }, Ct);

        var exact = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = "mp/one" }, Ct);
        var only = Assert.Single(exact.MultipartUploads);
        Assert.Equal("mp/one", only.Key);
        Assert.Equal(first.UploadId, only.UploadId);
        Assert.True(only.Initiated > DateTime.UtcNow.AddDays(-1));

        if (Backend.ListsMultipartUploadsByPrefix)
        {
            var listed = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = "mp/" }, Ct);
            Assert.Equal(["mp/one", "mp/two"], listed.MultipartUploads.Select(u => u.Key));
            Assert.Equal([first.UploadId, second.UploadId], listed.MultipartUploads.Select(u => u.UploadId));

            var rolledUp = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Delimiter = "/" }, Ct);
            Assert.Equal(["other"], rolledUp.MultipartUploads.Select(u => u.Key));
            Assert.Equal(["mp/"], rolledUp.CommonPrefixes);

            await new TransferUtility(S3).AbortMultipartUploadsAsync(Bucket, DateTime.UtcNow.AddMinutes(5), Ct);

            var after = await S3.ListMultipartUploadsAsync(Bucket, Ct);
            Assert.Empty(after.MultipartUploads ?? []);
        }
        else
        {
            // See KNOWN_DIFFERENCES.md: MinIO answers prefix listings only for exact keys, so abort by hand.
            await S3.AbortMultipartUploadAsync(Bucket, "mp/one", first.UploadId, Ct);
            await S3.AbortMultipartUploadAsync(Bucket, "mp/two", second.UploadId, Ct);
            await S3.AbortMultipartUploadAsync(Bucket, "other", third.UploadId, Ct);
        }

        var gone = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = "mp/one" }, Ct);
        Assert.Empty(gone.MultipartUploads ?? []);
    }
}
