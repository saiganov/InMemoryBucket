using Amazon.Runtime;
using Amazon.S3;

namespace InMemoryBucket.AwsSdk;

/// <summary>
/// Plugs the in-memory handler into the AWS SDK's HTTP pipeline.
/// The SDK's own HttpClient cache is static and keyed by config, so it is bypassed: two servers in one process must never share a handler.
/// One <see cref="HttpClient"/> per factory, which the SDK reuses for the lifetime of the <see cref="AmazonS3Client"/>.
/// </summary>
internal sealed class InMemoryBucketHttpClientFactory : HttpClientFactory
{
    private readonly Lazy<HttpClient> _client;

    public InMemoryBucketHttpClientFactory(InMemoryBucketServer server)
    {
        _client = new Lazy<HttpClient>(() => new HttpClient(server.CreateHandler(), disposeHandler: true));
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client.Value;

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
}
