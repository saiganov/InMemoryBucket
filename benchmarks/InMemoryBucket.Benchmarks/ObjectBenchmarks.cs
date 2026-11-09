using Amazon.S3;
using Amazon.S3.Model;
using BenchmarkDotNet.Attributes;
using InMemoryBucket.AwsSdk;

namespace InMemoryBucket.Benchmarks;

/// <summary>PutObject and GetObject through a real <see cref="AmazonS3Client"/>, as a test sees them.</summary>
[MemoryDiagnoser]
public class ObjectBenchmarks
{
    private const string Bucket = "bench";

    private InMemoryBucketServer _server = null!;
    private IAmazonS3 _client = null!;
    private byte[] _payload = null!;

    [Params(1024, 1024 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _server = new InMemoryBucketServer(o => o.CreateBuckets(Bucket));
        _client = _server.CreateClient();
        _payload = new byte[Size];
        Random.Shared.NextBytes(_payload);
        _server.Seed(Bucket, "existing", _payload);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Benchmark]
    public async Task PutObject()
    {
        using var body = new MemoryStream(_payload, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "put", InputStream = body });
    }

    [Benchmark]
    public async Task<long> GetObject()
    {
        using var response = await _client.GetObjectAsync(Bucket, "existing");
        using var sink = new MemoryStream(Size);
        await response.ResponseStream.CopyToAsync(sink);
        return sink.Length;
    }
}
