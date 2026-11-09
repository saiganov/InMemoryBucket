using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace InMemoryBucket.Tests;

/// <summary>
/// The MinIO .NET SDK talking to the fake through <see cref="InMemoryBucketServer.CreateHttpClient"/>. It signs payloads with
/// a plain SHA-256 (no aws-chunked), uses ListObjectsV2 and presigned URLs, so it exercises paths the AWS SDK does not.
/// </summary>
public sealed class MinioSdkTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o => o.CreateBuckets("minio-bucket"));
    private readonly HttpClient _http;
    private readonly IMinioClient _minio;

    public MinioSdkTests()
    {
        _http = _server.CreateHttpClient();
        _minio = new MinioClient()
            .WithEndpoint(_server.Options.Host)
            .WithCredentials("inmemorybucket", "inmemorybucket")
            .WithHttpClient(_http)
            .Build();
    }

    public void Dispose()
    {
        _minio.Dispose();
        _http.Dispose();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task PutAsync(string key, byte[] data, string contentType = "application/octet-stream")
    {
        using var stream = new MemoryStream(data);
        await _minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket("minio-bucket")
            .WithObject(key)
            .WithStreamData(stream)
            .WithObjectSize(data.Length)
            .WithContentType(contentType), Ct);
    }

    private async Task<byte[]> GetAsync(string key)
    {
        using var buffer = new MemoryStream();
        await _minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket("minio-bucket")
            .WithObject(key)
            .WithCallbackStream(async (stream, ct) => await stream.CopyToAsync(buffer, ct)), Ct);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Bucket_lifecycle()
    {
        Assert.False(await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket("fresh-bucket"), Ct));

        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket("fresh-bucket"), Ct);
        Assert.True(await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket("fresh-bucket"), Ct));

        var buckets = await _minio.ListBucketsAsync(Ct);
        Assert.Contains(buckets.Buckets, b => b.Name == "fresh-bucket");

        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket("fresh-bucket"), Ct);
        Assert.False(await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket("fresh-bucket"), Ct));
    }

    [Fact]
    public async Task Put_stat_get_and_remove_roundtrip()
    {
        var data = Encoding.UTF8.GetBytes("hello from minio");
        await PutAsync("docs/hello.txt", data, "text/plain");

        var stat = await _minio.StatObjectAsync(new StatObjectArgs().WithBucket("minio-bucket").WithObject("docs/hello.txt"), Ct);
        Assert.Equal(data.Length, stat.Size);
        Assert.Equal("text/plain", stat.ContentType);
        Assert.Equal(Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(), stat.ETag.Trim('"'));

        Assert.Equal(data, await GetAsync("docs/hello.txt"));

        await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("minio-bucket").WithObject("docs/hello.txt"), Ct);
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => _minio.StatObjectAsync(new StatObjectArgs().WithBucket("minio-bucket").WithObject("docs/hello.txt"), Ct));
    }

    [Fact]
    public async Task Large_upload_goes_through_multipart_and_reads_back()
    {
        var data = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        await PutAsync("big.bin", data);

        var stored = _server.GetObject("minio-bucket", "big.bin");
        Assert.NotNull(stored);
        Assert.Equal(data.Length, stored.Size);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(await GetAsync("big.bin")));
        Assert.Contains(_server.Requests, r => r.Operation == Protocol.S3Operation.CompleteMultipartUpload);
    }

    [Fact]
    public async Task Listing_with_prefix_recursive_and_non_recursive()
    {
        foreach (var key in new[] { "a/1", "a/2", "a/sub/3", "b/1", "root" })
        {
            await PutAsync(key, [1]);
        }

        var recursive = new List<string>();
        await foreach (var item in _minio.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket("minio-bucket").WithPrefix("a/").WithRecursive(true), Ct))
        {
            recursive.Add(item.Key);
        }

        Assert.Equal(["a/1", "a/2", "a/sub/3"], recursive);

        var top = new List<(string Key, bool IsDir)>();
        await foreach (var item in _minio.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket("minio-bucket").WithRecursive(false), Ct))
        {
            top.Add((item.Key, item.IsDir));
        }

        Assert.Equal([("a/", true), ("b/", true), ("root", false)], top.OrderBy(t => t.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Remove_many_objects()
    {
        foreach (var key in new[] { "d/1", "d/2", "keep" })
        {
            await PutAsync(key, [1]);
        }

        var errors = await _minio.RemoveObjectsAsync(new RemoveObjectsArgs().WithBucket("minio-bucket").WithObjects(["d/1", "d/2"]), Ct);

        Assert.Empty(errors);
        Assert.Equal(["keep"], _server.ListKeys("minio-bucket"));
    }

    [Fact]
    public async Task Copy_object()
    {
        await PutAsync("src", "payload"u8.ToArray(), "text/plain");

        await _minio.CopyObjectAsync(new CopyObjectArgs()
            .WithBucket("minio-bucket")
            .WithObject("dst")
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket("minio-bucket").WithObject("src")), Ct);

        Assert.Equal("payload"u8.ToArray(), await GetAsync("dst"));
        var stat = await _minio.StatObjectAsync(new StatObjectArgs().WithBucket("minio-bucket").WithObject("dst"), Ct);
        Assert.Equal("text/plain", stat.ContentType);
    }

    [Fact]
    public async Task Presigned_get_and_put_urls_work_over_plain_http()
    {
        await PutAsync("signed.txt", "presigned"u8.ToArray(), "text/plain");

        var getUrl = await _minio.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket("minio-bucket").WithObject("signed.txt").WithExpiry(60));
        var get = await _http.GetAsync(getUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("presigned", await get.Content.ReadAsStringAsync(Ct));

        var putUrl = await _minio.PresignedPutObjectAsync(new PresignedPutObjectArgs().WithBucket("minio-bucket").WithObject("uploaded.txt").WithExpiry(60));
        var put = await _http.PutAsync(putUrl, new StringContent("via presigned put"), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("via presigned put", Encoding.UTF8.GetString(_server.GetObject("minio-bucket", "uploaded.txt")!.Data.Span));
    }

    [Fact]
    public async Task Errors_arrive_as_minio_exceptions()
    {
        await Assert.ThrowsAsync<BucketNotFoundException>(() => _minio.StatObjectAsync(new StatObjectArgs().WithBucket("ghost-bucket").WithObject("k"), Ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => _minio.StatObjectAsync(new StatObjectArgs().WithBucket("minio-bucket").WithObject("missing"), Ct));
    }
}
