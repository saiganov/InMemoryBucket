using Amazon.Runtime;
using Amazon.S3;
using InMemoryBucket.ContractTests;
using InMemoryBucket.Hosting;

namespace InMemoryBucket.Hosting.Tests;

/// <summary>The whole contract suite over real TCP with strict signatures on, so Kestrel sees the same bytes a real server would.</summary>
public sealed class HostedBackend : S3Backend, IAsyncLifetime
{
    private InMemoryBucketHost? _host;
    private AmazonS3Client? _client;

    public override S3Flavor Flavor => S3Flavor.InMemory;

    public override IAmazonS3 Client => _client ?? throw new InvalidOperationException("Not started.");

    public InMemoryBucketHost Host => _host ?? throw new InvalidOperationException("Not started.");

    public async ValueTask InitializeAsync()
    {
        _host = await InMemoryBucketHost.StartAsync(o => o.ValidateSignatures = true);
        _client = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "inmemorybucket"), new AmazonS3Config
        {
            ServiceURL = _host.BaseAddress.ToString().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

public sealed class HostedContractTests(HostedBackend backend) : S3ContractTests(backend), IClassFixture<HostedBackend>;
