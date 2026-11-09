using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;
using InMemoryBucket.Hosting;
using Minio;
using Minio.DataModel.Args;

namespace InMemoryBucket.Hosting.Tests;

/// <summary>What only a real socket can show: presigned URLs from a plain client, the MinIO SDK over TCP, hooks and HEAD/304 through Kestrel.</summary>
public sealed class HostingTests : IAsyncLifetime
{
    private InMemoryBucketHost _host = null!;
    private AmazonS3Client _s3 = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await InMemoryBucketHost.StartAsync(o =>
        {
            o.CreateBuckets("hosted");
            o.ValidateSignatures = true;
        }, cancellationToken: Ct);
        _s3 = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "inmemorybucket"), new AmazonS3Config
        {
            ServiceURL = _host.BaseAddress.ToString().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _s3.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Listens_on_a_free_loopback_port_and_serves_the_sdk()
    {
        Assert.Equal("127.0.0.1", _host.BaseAddress.Host);
        Assert.NotEqual(0, _host.Port);

        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "hosted", Key = "a/b c.txt", ContentBody = "over tcp" }, Ct);
        using var get = await _s3.GetObjectAsync("hosted", "a/b c.txt", Ct);
        Assert.Equal("over tcp", await new StreamReader(get.ResponseStream).ReadToEndAsync(Ct));

        var head = await _s3.GetObjectMetadataAsync("hosted", "a/b c.txt", Ct);
        Assert.Equal(8, head.ContentLength);

        var record = _host.Server.Requests.Last!;
        Assert.Equal(S3Operation.HeadObject, record.Operation);
        Assert.Equal("inmemorybucket", record.AccessKey);
    }

    [Fact]
    public async Task Multipart_upload_streams_aws_chunked_parts_through_kestrel()
    {
        var payload = new byte[17 * 1024 * 1024]; // above TransferUtility's 16 MB multipart threshold
        Random.Shared.NextBytes(payload);
        using var transfer = new TransferUtility(_s3);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = "hosted",
            Key = "big.bin",
            InputStream = new MemoryStream(payload),
            PartSize = 5 * 1024 * 1024,
        }, Ct);

        var stored = _host.Server.GetObject("hosted", "big.bin")!;
        Assert.Equal(payload.Length, stored.Size);
        Assert.True(payload.AsSpan().SequenceEqual(stored.Data.Span));
        Assert.Contains(_host.Server.Requests, r => r.Operation == S3Operation.CompleteMultipartUpload && r.StatusCode == 200);
    }

    [Fact]
    public async Task Presigned_urls_work_from_a_plain_client_and_fail_when_tampered()
    {
        _host.Server.Seed("hosted", "doc.txt", "signed doc");
        using var http = _host.CreateHttpClient();

        // The SDK presigns with https unless told otherwise, whatever the ServiceURL scheme.
        var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "hosted", Key = "doc.txt", Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Amazon.S3.Protocol.HTTP });
        var ok = await http.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("signed doc", await ok.Content.ReadAsStringAsync(Ct));

        var tampered = await http.GetAsync(url.Replace("doc.txt", "other.txt"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await tampered.Content.ReadAsStringAsync(Ct));

        var anonymous = await http.GetAsync("/hosted/doc.txt", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Fact]
    public async Task Head_and_not_modified_responses_carry_no_body()
    {
        _host.Server.Seed("hosted", "cached.txt", "payload");
        var etag = (await _s3.GetObjectMetadataAsync("hosted", "cached.txt", Ct)).ETag;

        var notModified = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectAsync(new GetObjectRequest { BucketName = "hosted", Key = "cached.txt", EtagToNotMatch = etag }, Ct));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        var missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectMetadataAsync("hosted", "nope", Ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var http = _host.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/hosted/cached.txt");
        var response = await http.SendAsync(request, Ct); // anonymous: strict mode says 403, and HEAD must still have no body
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Virtual_hosted_addressing_resolves_against_localhost()
    {
        _host.Server.Options.ValidateSignatures = false;
        _host.Server.Seed("hosted", "vh.txt", "virtual");
        using var http = _host.CreateHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/vh.txt");
        request.Headers.Host = $"hosted.localhost:{_host.Port}";
        var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("virtual", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("hosted", _host.Server.Requests.Last!.Bucket);
    }

    [Fact]
    public async Task Faults_and_logging_apply_over_tcp_too()
    {
        _host.Server.Faults.Once(r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.PutObjectAsync(new PutObjectRequest { BucketName = "hosted", Key = "k", ContentBody = "x" }, Ct));
        Assert.Equal("SlowDown", error.ErrorCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);

        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "hosted", Key = "k", ContentBody = "x" }, Ct);
        Assert.Equal([503, 200], _host.Server.Requests.Select(r => r.StatusCode));
    }

    [Fact]
    public async Task Minio_sdk_talks_to_the_port()
    {
        using var minio = new MinioClient()
            .WithEndpoint("127.0.0.1", _host.Port)
            .WithCredentials("inmemorybucket", "inmemorybucket")
            .Build();

        var data = Encoding.UTF8.GetBytes("from minio");
        using var stream = new MemoryStream(data);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket("hosted").WithObject("minio.txt").WithStreamData(stream).WithObjectSize(data.Length), Ct);

        using var buffer = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs().WithBucket("hosted").WithObject("minio.txt").WithCallbackStream(async (s, ct) => await s.CopyToAsync(buffer, ct)), Ct);
        Assert.Equal("from minio", Encoding.UTF8.GetString(buffer.ToArray()));

        var presigned = await minio.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket("hosted").WithObject("minio.txt").WithExpiry(60));
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(presigned, Ct)).StatusCode);
    }

    [Fact]
    public async Task Fixed_port_and_a_second_host_in_the_same_process()
    {
        await using var other = await InMemoryBucketHost.StartAsync(new InMemoryBucketServer(o => o.CreateBuckets("second")), cancellationToken: Ct);
        Assert.NotEqual(_host.Port, other.Port);
        using var http = other.CreateHttpClient();
        var response = await http.GetAsync("/second?list-type=2", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<Name>second</Name>", await response.Content.ReadAsStringAsync(Ct));
    }
}
