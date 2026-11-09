using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using InMemoryBucket.AwsSdk;
using Microsoft.Extensions.DependencyInjection;

namespace InMemoryBucket.Tests;

/// <summary>
/// Behaviour specific to the fake that the shared contract suite cannot express: options, DI, hooks and the 501 policy.
/// The S3 protocol itself is covered by <see cref="InMemoryContractTests"/>.
/// </summary>
public sealed class AwsSdkTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o => o.CreateBuckets("uploads"));
    private readonly AmazonS3Client _s3;

    public AwsSdkTests()
    {
        _s3 = _server.CreateClient();
    }

    public void Dispose()
    {
        _s3.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task PutObject_with_If_None_Match_other_than_star_is_501()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = "uploads",
                Key = "k",
                ContentBody = "v",
                IfNoneMatch = "\"5d41402abc4b2a76b9719d911017c592\"",
            }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotImplemented, error.StatusCode);
        Assert.False(_server.Store.TryGetBucket("uploads", out var bucket) && bucket.TryGet("k", out _));
    }

    [Fact]
    public async Task Seed_copies_the_callers_buffer()
    {
        var buffer = "original"u8.ToArray();
        _server.Seed("uploads", "seeded", buffer);

        buffer.AsSpan().Fill((byte)'X');

        using var response = await _s3.GetObjectAsync("uploads", "seeded", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal("original", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unsupported_operation_fails_loudly_with_501()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = "uploads",
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotImplemented, error.StatusCode);
        Assert.Equal("NotImplemented", error.ErrorCode);
    }

    [Fact]
    public async Task Repeated_CreateBucket_depends_on_region()
    {
        using var euServer = new InMemoryBucketServer(o => o.Region = "eu-central-1");
        using var eu = euServer.CreateClient();

        await _s3.PutBucketAsync("twice", TestContext.Current.CancellationToken);
        await _s3.PutBucketAsync("twice", TestContext.Current.CancellationToken); // us-east-1: 200, as AWS

        await eu.PutBucketAsync("twice", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => eu.PutBucketAsync("twice", TestContext.Current.CancellationToken));
        Assert.Equal("BucketAlreadyOwnedByYou", error.ErrorCode);
    }

    [Fact]
    public async Task Seed_inspect_and_reset_bypass_http()
    {
        _server.Seed("uploads", "seeded/a.txt", "seeded", new() { ContentType = "text/plain" });

        using var get = await _s3.GetObjectAsync("uploads", "seeded/a.txt", TestContext.Current.CancellationToken);
        Assert.Equal("text/plain", get.Headers.ContentType);

        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "uploads", Key = "seeded/b.txt", ContentBody = "via sdk" }, TestContext.Current.CancellationToken);
        Assert.Equal(["seeded/a.txt", "seeded/b.txt"], _server.ListKeys("uploads", "seeded/"));
        Assert.Equal("via sdk", Encoding.UTF8.GetString(_server.GetObject("uploads", "seeded/b.txt")!.Data.Span));

        _server.Reset();
        Assert.Empty(_server.ListKeys("uploads"));
        Assert.True(await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, "uploads"));
    }

    [Fact]
    public async Task AddInMemoryBucket_replaces_IAmazonS3_in_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonS3>(_ => throw new InvalidOperationException("production client must not be built"));
        services.AddInMemoryBucket(o => o.CreateBuckets("di-bucket"));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAmazonS3>();
        var server = provider.GetRequiredService<InMemoryBucketServer>();

        await client.PutObjectAsync(new PutObjectRequest { BucketName = "di-bucket", Key = "k", ContentBody = "v" }, TestContext.Current.CancellationToken);

        Assert.Equal("v", Encoding.UTF8.GetString(server.GetObject("di-bucket", "k")!.Data.Span));
    }
}
