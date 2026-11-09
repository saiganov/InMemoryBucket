using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;

namespace InMemoryBucket.Tests;

/// <summary>
/// Server-side encryption settings are stored and echoed like S3 does; features the fake cannot honour answer 501 instead of
/// being dropped. MinIO without a KMS behaves differently, so these live outside the shared contract suite.
/// </summary>
public sealed class EncryptionAndUnsupportedHeadersTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o => o.CreateBuckets("enc"));
    private readonly AmazonS3Client _s3;

    public EncryptionAndUnsupportedHeadersTests() => _s3 = _server.CreateClient();

    public void Dispose()
    {
        _s3.Dispose();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Objects_are_reported_as_SSE_S3_by_default()
    {
        var put = await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "enc", Key = "plain", ContentBody = "x" }, Ct);
        var head = await _s3.GetObjectMetadataAsync("enc", "plain", Ct);

        Assert.Equal(ServerSideEncryptionMethod.AES256, put.ServerSideEncryptionMethod);
        Assert.Equal(ServerSideEncryptionMethod.AES256, head.ServerSideEncryptionMethod);
    }

    [Fact]
    public async Task KMS_settings_are_stored_and_echoed()
    {
        const string key = "arn:aws:kms:us-east-1:111122223333:key/1234abcd-12ab-34cd-56ef-1234567890ab";
        var put = await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "enc",
            Key = "kms",
            ContentBody = "x",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = key,
            BucketKeyEnabled = true,
        }, Ct);

        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, put.ServerSideEncryptionMethod);
        Assert.Equal(key, put.ServerSideEncryptionKeyManagementServiceKeyId);

        using var get = await _s3.GetObjectAsync("enc", "kms", Ct);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, get.ServerSideEncryptionMethod);
        Assert.Equal(key, get.ServerSideEncryptionKeyManagementServiceKeyId);
        Assert.True(get.BucketKeyEnabled);

        // A copy is encrypted as the copy request asks, not as the source was.
        await _s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = "enc", SourceKey = "kms", DestinationBucket = "enc", DestinationKey = "copy" }, Ct);
        var copy = await _s3.GetObjectMetadataAsync("enc", "copy", Ct);
        Assert.Equal(ServerSideEncryptionMethod.AES256, copy.ServerSideEncryptionMethod);
        Assert.Null(copy.ServerSideEncryptionKeyManagementServiceKeyId);
    }

    [Fact]
    public async Task Multipart_uploads_keep_the_encryption_from_initiate()
    {
        var init = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = "enc",
            Key = "mp",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
        }, Ct);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, init.ServerSideEncryptionMethod);

        var part = await _s3.UploadPartAsync(new UploadPartRequest { BucketName = "enc", Key = "mp", UploadId = init.UploadId, PartNumber = 1, InputStream = new MemoryStream([1, 2, 3]) }, Ct);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, part.ServerSideEncryptionMethod);

        var done = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = "enc", Key = "mp", UploadId = init.UploadId, PartETags = [new PartETag(1, part.ETag)] }, Ct);
        Assert.Equal(ServerSideEncryptionMethod.AWSKMS, done.ServerSideEncryptionMethod);
    }

    [Fact]
    public async Task An_unknown_encryption_algorithm_is_InvalidArgument()
    {
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "enc",
            Key = "bad",
            ContentBody = "x",
            ServerSideEncryptionMethod = new ServerSideEncryptionMethod("ROT13"),
        }, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("InvalidArgument", error.ErrorCode);
    }

    public static TheoryData<string, Action<PutObjectRequest>> UnsupportedPuts => new()
    {
        { "SSE-C", r => { r.ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256; r.ServerSideEncryptionCustomerProvidedKey = Convert.ToBase64String(new byte[32]); } },
        { "Object Lock", r => { r.ObjectLockMode = ObjectLockMode.Governance; r.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(1); } },
        { "website redirects", r => r.WebsiteRedirectLocation = "/elsewhere" },
        { "x-amz-write-offset-bytes", r => r.WriteOffsetBytes = 0 },
    };

    [Theory]
    [MemberData(nameof(UnsupportedPuts))]
    public async Task Headers_for_unsupported_features_answer_501(string feature, Action<PutObjectRequest> configure)
    {
        var request = new PutObjectRequest { BucketName = "enc", Key = "unsupported", ContentBody = "x" };
        configure(request);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(request, Ct));

        Assert.Equal(HttpStatusCode.NotImplemented, error.StatusCode);
        Assert.Contains(feature, error.Message);
        Assert.Null(_server.GetObject("enc", "unsupported"));
    }

    [Fact]
    public async Task Reading_one_part_answers_501()
    {
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "enc", Key = "whole", ContentBody = "x" }, Ct);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectAsync(new GetObjectRequest { BucketName = "enc", Key = "whole", PartNumber = 1 }, Ct));

        Assert.Equal(HttpStatusCode.NotImplemented, error.StatusCode);
    }
}
