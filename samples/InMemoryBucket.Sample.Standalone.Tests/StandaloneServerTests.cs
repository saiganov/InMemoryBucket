using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.Sample.Standalone;

namespace InMemoryBucket.Sample.Standalone.Tests;

/// <summary>
/// The out-of-process story: anything that can speak HTTP to a port can use the fake. Here the "external client" is an
/// AmazonS3Client configured exactly as the printed AWS CLI instructions say, plus a raw HttpClient standing in for curl.
/// </summary>
public sealed class StandaloneServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Arguments_map_to_options()
    {
        var options = StandaloneOptions.Parse(["--port", "9100", "--bucket", "a", "--bucket", "b", "--access-key", "k", "--secret-key", "s", "--region", "eu-west-1", "--strict", "--verbose"]);

        Assert.Equal((9100, "k", "s", "eu-west-1", true, true), (options.Port, options.AccessKey, options.SecretKey, options.Region, options.Strict, options.Verbose));
        Assert.Equal(["a", "b"], options.Buckets);
        Assert.Equal(9000, StandaloneOptions.Parse([]).Port);
        Assert.Throws<ArgumentException>(() => StandaloneOptions.Parse(["--port"]));
        Assert.Throws<ArgumentException>(() => StandaloneOptions.Parse(["--colour", "red"]));
    }

    [Fact]
    public async Task External_client_uses_the_printed_endpoint_and_credentials()
    {
        var output = new StringWriter();
        var options = new StandaloneOptions(Port: 0, Buckets: ["uploads"], AccessKey: "AKIATEST", SecretKey: "s3cr3t", Strict: true);
        await using var host = await StandaloneServer.StartAsync(options, output, Ct);

        Assert.Contains($"listening on {host.BaseAddress}", output.ToString());
        Assert.Contains("AWS_ACCESS_KEY_ID=AKIATEST AWS_SECRET_ACCESS_KEY=s3cr3t", output.ToString());
        Assert.Contains($"aws --endpoint-url http://127.0.0.1:{host.Port} s3 ls", output.ToString());

        using var s3 = new AmazonS3Client(new BasicAWSCredentials("AKIATEST", "s3cr3t"), new AmazonS3Config
        {
            ServiceURL = host.BaseAddress.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "uploads", Key = "from-outside.txt", ContentBody = "hello over tcp" }, Ct);
        var listing = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "uploads" }, Ct);
        Assert.Equal(["from-outside.txt"], listing.S3Objects.Select(o => o.Key));

        // Strict mode: a client with the default credentials is turned away.
        using var stranger = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "inmemorybucket"), new AmazonS3Config
        {
            ServiceURL = host.BaseAddress.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });
        var denied = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => stranger.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "uploads" }, Ct));
        Assert.Equal("InvalidAccessKeyId", denied.ErrorCode);

        // curl-style: the bytes are really there for anyone who can reach the port.
        host.Server.Options.ValidateSignatures = false;
        using var curl = host.CreateHttpClient();
        var raw = await curl.GetAsync("/uploads/from-outside.txt", Ct);
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal("hello over tcp", await raw.Content.ReadAsStringAsync(Ct));

        Assert.Equal(4, host.Server.Requests.Count);
    }
}
