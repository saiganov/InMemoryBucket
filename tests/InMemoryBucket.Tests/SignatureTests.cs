using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using InMemoryBucket.AwsSdk;
using Microsoft.Extensions.Time.Testing;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace InMemoryBucket.Tests;

/// <summary>Strict SigV4 mode: real SDKs pass, wrong keys, regions, clocks and tampered URLs fail the way they fail on S3.</summary>
public sealed class SignatureTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o =>
    {
        o.CreateBuckets("signed-bucket");
        o.ValidateSignatures = true;
    });

    public void Dispose() => _server.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Aws_sdk_requests_of_every_shape_verify()
    {
        using var s3 = _server.CreateClient();

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "signed-bucket", Key = "plain.txt", ContentBody = "hello" }, Ct);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "signed-bucket", Key = "odd keys/with space+plus (and) ünïcödé.txt", ContentBody = "odd" }, Ct);
        using (var get = await s3.GetObjectAsync("signed-bucket", "plain.txt", Ct))
        using (var reader = new StreamReader(get.ResponseStream))
        {
            Assert.Equal("hello", await reader.ReadToEndAsync(Ct));
        }

        var list = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "signed-bucket", Prefix = "odd keys/", MaxKeys = 10 }, Ct);
        Assert.Equal("odd keys/with space+plus (and) ünïcödé.txt", Assert.Single(list.S3Objects).Key);

        await s3.CopyObjectAsync("signed-bucket", "plain.txt", "signed-bucket", "copy.txt", Ct);
        await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = "signed-bucket", Objects = [new KeyVersion { Key = "copy.txt" }] }, Ct);
        await s3.GetObjectMetadataAsync("signed-bucket", "plain.txt", Ct);
        Assert.Contains((await s3.ListBucketsAsync(Ct)).Buckets, b => b.BucketName == "signed-bucket");

        // aws-chunked with trailing checksum, several parts in parallel (TransferUtility switches to multipart above 16 MB).
        var payload = new byte[17 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        using var upload = new TransferUtility(s3);
        await upload.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = "signed-bucket",
            Key = "big.bin",
            InputStream = new MemoryStream(payload),
            PartSize = 5 * 1024 * 1024,
        }, Ct);
        Assert.Equal(payload.Length, _server.GetObject("signed-bucket", "big.bin")!.Size);
        Assert.Contains(_server.Requests, r => r.Operation == InMemoryBucket.Protocol.S3Operation.CompleteMultipartUpload && r.StatusCode == 200);

        Assert.All(_server.Requests, r => Assert.Equal("inmemorybucket", r.AccessKey));
        Assert.DoesNotContain(_server.Requests, r => r.StatusCode is 403 or 400);
    }

    [Fact]
    public async Task Virtual_hosted_addressing_verifies_too()
    {
        using var s3 = _server.CreateClient(c => c.ForcePathStyle = false);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "signed-bucket", Key = "a/b c.txt", ContentBody = "vh" }, Ct);
        using var get = await s3.GetObjectAsync("signed-bucket", "a/b c.txt", Ct);
        Assert.Equal(HttpStatusCode.OK, get.HttpStatusCode);
        Assert.Equal("vh", await new StreamReader(get.ResponseStream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Wrong_secret_is_SignatureDoesNotMatch()
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "not-the-secret"), _server.CreateClientConfig());

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = "signed-bucket", Key = "k", ContentBody = "x" }, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("SignatureDoesNotMatch", error.ErrorCode);
        Assert.Null(_server.GetObject("signed-bucket", "k"));
    }

    [Fact]
    public async Task Unknown_access_key_is_InvalidAccessKeyId()
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("AKIANOBODY", "whatever"), _server.CreateClientConfig());

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "signed-bucket" }, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("InvalidAccessKeyId", error.ErrorCode);
    }

    [Fact]
    public async Task Wrong_region_in_the_credential_scope_is_AuthorizationHeaderMalformed()
    {
        using var s3 = _server.CreateClient(c => c.AuthenticationRegion = "eu-west-1");

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "signed-bucket" }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("AuthorizationHeaderMalformed", error.ErrorCode);
        Assert.Contains("expecting 'us-east-1'", error.Message);
    }

    [Fact]
    public async Task Anonymous_requests_are_AccessDenied()
    {
        _server.Seed("signed-bucket", "public.txt", "x");
        using var http = _server.CreateHttpClient();

        var response = await http.GetAsync("/signed-bucket/public.txt", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<Code>AccessDenied</Code>", body);
        Assert.Contains("Anonymous access is forbidden", body);
    }

    [Fact]
    public async Task Skewed_clock_is_RequestTimeTooSkewed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-20));
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("signed-bucket");
            o.ValidateSignatures = true;
            o.TimeProvider = clock;
            // The AWS SDK remembers a clock correction per host after RequestTimeTooSkewed; keep it away from the other tests' host.
            o.Host = "skewed.in-memory.local";
        });
        using var s3 = server.CreateClient();

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "signed-bucket" }, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("RequestTimeTooSkewed", error.ErrorCode);

        clock.Advance(TimeSpan.FromMinutes(10));
        await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "signed-bucket" }, Ct);
    }

    [Fact]
    public async Task Presigned_url_verifies_and_tampering_breaks_it()
    {
        _server.Seed("signed-bucket", "doc.txt", "contents");
        _server.Seed("signed-bucket", "other.txt", "other");
        using var s3 = _server.CreateClient();
        using var http = _server.CreateHttpClient();

        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "signed-bucket", Key = "doc.txt", Expires = DateTime.UtcNow.AddMinutes(5) });
        var ok = await http.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("contents", await ok.Content.ReadAsStringAsync(Ct));

        var otherKey = await http.GetAsync(url.Replace("doc.txt", "other.txt"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, otherKey.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await otherKey.Content.ReadAsStringAsync(Ct));

        var expires = System.Text.RegularExpressions.Regex.Match(url, @"X-Amz-Expires=(\d+)").Groups[1].Value;
        var longerLife = await http.GetAsync(url.Replace("X-Amz-Expires=" + expires, "X-Amz-Expires=" + (int.Parse(expires) * 10)), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, longerLife.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await longerLife.Content.ReadAsStringAsync(Ct));

        using var stranger = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "guess"), _server.CreateClientConfig());
        var forged = await stranger.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "signed-bucket", Key = "doc.txt", Expires = DateTime.UtcNow.AddMinutes(5) });
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(forged, Ct)).StatusCode);

        var put = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "signed-bucket", Key = "uploaded.txt", Verb = HttpVerb.PUT, Expires = DateTime.UtcNow.AddMinutes(5) });
        var uploaded = await http.PutAsync(put, new StringContent("via presigned put"), Ct);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal("via presigned put", Encoding.UTF8.GetString(_server.GetObject("signed-bucket", "uploaded.txt")!.Data.Span));
    }

    [Fact]
    public async Task Custom_credentials_replace_the_defaults()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("signed-bucket");
            o.ValidateSignatures = true;
            o.UseCredentials("AKIAEXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        });

        using var matching = server.CreateClient();
        await matching.PutObjectAsync(new PutObjectRequest { BucketName = "signed-bucket", Key = "k", ContentBody = "x" }, Ct);
        Assert.Equal("AKIAEXAMPLE", server.Requests.Last!.AccessKey);

        using var stale = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "inmemorybucket"), server.CreateClientConfig());
        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => stale.GetObjectAsync("signed-bucket", "k", Ct));
        Assert.Equal("InvalidAccessKeyId", error.ErrorCode);
    }

    [Fact]
    public async Task Minio_sdk_signs_plain_sha256_payloads_and_presigned_urls_that_verify()
    {
        using var http = _server.CreateHttpClient();
        using var minio = new MinioClient()
            .WithEndpoint(_server.Options.Host)
            .WithCredentials("inmemorybucket", "inmemorybucket")
            .WithHttpClient(http)
            .Build();

        var data = Encoding.UTF8.GetBytes("minio signed");
        using var stream = new MemoryStream(data);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket("signed-bucket").WithObject("dir/minio file.txt").WithStreamData(stream).WithObjectSize(data.Length), Ct);
        var stat = await minio.StatObjectAsync(new StatObjectArgs().WithBucket("signed-bucket").WithObject("dir/minio file.txt"), Ct);
        Assert.Equal(data.Length, stat.Size);

        var keys = new List<string>();
        await foreach (var item in minio.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket("signed-bucket").WithPrefix("dir/"), Ct))
        {
            keys.Add(item.Key);
        }

        Assert.Equal(["dir/minio file.txt"], keys);

        var presigned = await minio.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket("signed-bucket").WithObject("dir/minio file.txt").WithExpiry(300));
        var response = await http.GetAsync(presigned, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("minio signed", await response.Content.ReadAsStringAsync(Ct));

        await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket("signed-bucket").WithObject("dir/minio file.txt"), Ct);
        Assert.Empty(_server.ListKeys("signed-bucket"));
    }

    [Fact]
    public async Task Minio_sdk_with_wrong_secret_is_rejected()
    {
        using var http = _server.CreateHttpClient();
        using var minio = new MinioClient()
            .WithEndpoint(_server.Options.Host)
            .WithCredentials("inmemorybucket", "wrong")
            .WithHttpClient(http)
            .Build();

        // Not StatObject: the MinIO SDK trips over a 403 without a body (HEAD), against any server.
        var data = "x"u8.ToArray();
        using var stream = new MemoryStream(data);
        var error = await Assert.ThrowsAsync<AuthorizationException>(() => minio.PutObjectAsync(new PutObjectArgs().WithBucket("signed-bucket").WithObject("k").WithStreamData(stream).WithObjectSize(data.Length), Ct));
        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SignatureDoesNotMatch", _server.Requests.Last!.ErrorCode);
        Assert.Null(_server.GetObject("signed-bucket", "k"));
    }

    [Fact]
    public async Task Body_changed_after_signing_is_XAmzContentSHA256Mismatch()
    {
        using var http = new HttpClient(new BodySwappingHandler(_server.CreateHandler())) { BaseAddress = _server.BaseAddress };
        using var minio = new MinioClient()
            .WithEndpoint(_server.Options.Host)
            .WithCredentials("inmemorybucket", "inmemorybucket")
            .WithHttpClient(http)
            .Build();

        var data = Encoding.UTF8.GetBytes("original");
        using var stream = new MemoryStream(data);
        var error = await Assert.ThrowsAnyAsync<MinioException>(() => minio.PutObjectAsync(new PutObjectArgs().WithBucket("signed-bucket").WithObject("swapped").WithStreamData(stream).WithObjectSize(data.Length), Ct));
        Assert.Contains("x-amz-content-sha256", error.ServerMessage ?? error.Message);
        Assert.Equal("XAmzContentSHA256Mismatch", _server.Requests.Last!.ErrorCode);
        Assert.Null(_server.GetObject("signed-bucket", "swapped"));
    }

    /// <summary>Replaces a PUT body with same-length garbage after the SDK signed it.</summary>
    private sealed class BodySwappingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.Content is not null)
            {
                var original = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var swapped = new ByteArrayContent(new byte[original.Length]);
                foreach (var header in request.Content.Headers)
                {
                    swapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                request.Content = swapped;
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
