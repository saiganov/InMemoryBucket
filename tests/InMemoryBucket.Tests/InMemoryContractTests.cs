using Amazon.S3;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.ContractTests;

namespace InMemoryBucket.Tests;

/// <summary>One server for the whole class; every contract test creates its own bucket, so they run in parallel.</summary>
public sealed class InMemoryBackend : S3Backend, IDisposable
{
    private readonly InMemoryBucketServer _server = new();

    public InMemoryBackend()
    {
        Client = _server.CreateClient();
    }

    public override S3Flavor Flavor => S3Flavor.InMemory;

    public override IAmazonS3 Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _server.Dispose();
    }
}

public sealed class InMemoryContractTests(InMemoryBackend backend) : S3ContractTests(backend), IClassFixture<InMemoryBackend>;
