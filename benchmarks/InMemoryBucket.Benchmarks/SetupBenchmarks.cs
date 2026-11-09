using Amazon.S3.Model;
using BenchmarkDotNet.Attributes;
using InMemoryBucket.AwsSdk;

namespace InMemoryBucket.Benchmarks;

/// <summary>
/// What every test pays when it builds its own fake: a server, a client, one PutObject and one GetObject, then disposal.
/// </summary>
[MemoryDiagnoser]
public class SetupBenchmarks
{
    private static readonly byte[] Payload = "hello"u8.ToArray();

    [Benchmark]
    public async Task NewServerPerTest()
    {
        using var server = new InMemoryBucketServer(o => o.CreateBuckets("test"));
        using var client = server.CreateClient();
        using var body = new MemoryStream(Payload, writable: false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "test", Key = "k", InputStream = body });
        using var response = await client.GetObjectAsync("test", "k");
        await response.ResponseStream.CopyToAsync(Stream.Null);
    }
}
