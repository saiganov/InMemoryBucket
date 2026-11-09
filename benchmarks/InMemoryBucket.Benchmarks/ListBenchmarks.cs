using Amazon.S3;
using Amazon.S3.Model;
using BenchmarkDotNet.Attributes;
using InMemoryBucket.AwsSdk;

namespace InMemoryBucket.Benchmarks;

/// <summary>Paging through every key of a large bucket with ListObjectsV2 (1,000 keys a page, as S3 returns).</summary>
[MemoryDiagnoser]
public class ListBenchmarks
{
    private const string Bucket = "bench";

    private InMemoryBucketServer _server = null!;
    private IAmazonS3 _client = null!;

    [Params(10_000)]
    public int Keys { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _server = new InMemoryBucketServer(o => o.CreateBuckets(Bucket));
        _client = _server.CreateClient();
        for (var i = 0; i < Keys; i++)
        {
            _server.Seed(Bucket, $"dir{i % 100:D3}/file{i:D6}.json", "{}");
        }

        // A paging bug would otherwise show up as a faster benchmark.
        var listed = ListAllKeys().GetAwaiter().GetResult();
        if (listed != Keys)
        {
            throw new InvalidOperationException($"Listed {listed} keys, expected {Keys}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Benchmark]
    public async Task<int> ListAllKeys()
    {
        var count = 0;
        var request = new ListObjectsV2Request { BucketName = Bucket };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request);
            count += response.S3Objects?.Count ?? 0;
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return count;
    }
}
