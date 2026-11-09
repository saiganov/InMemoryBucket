using Amazon.Runtime;
using Amazon.S3;

namespace InMemoryBucket.AwsSdk;

/// <summary>Creates AWS SDK clients bound to an <see cref="InMemoryBucketServer"/>.</summary>
public static class InMemoryBucketServerExtensions
{
    /// <summary>
    /// A real <see cref="AmazonS3Client"/> whose every request goes to the in-memory server. Path-style addressing, the first pair
    /// from <see cref="InMemoryBucketOptions.Credentials"/> (inmemorybucket/inmemorybucket by default), no retries by default.
    /// </summary>
    public static AmazonS3Client CreateClient(this InMemoryBucketServer server, Action<AmazonS3Config>? configure = null)
    {
        var config = server.CreateClientConfig();
        configure?.Invoke(config);
        return new AmazonS3Client(server.CreateCredentials(), config);
    }

    /// <summary>Credentials the server accepts when <see cref="InMemoryBucketOptions.ValidateSignatures"/> is on.</summary>
    public static AWSCredentials CreateCredentials(this InMemoryBucketServer server)
    {
        var pair = server.Options.Credentials.FirstOrDefault();
        return pair.Key is null
            ? new BasicAWSCredentials("inmemorybucket", "inmemorybucket")
            : new BasicAWSCredentials(pair.Key, pair.Value);
    }

    /// <summary>
    /// The configuration <see cref="CreateClient"/> uses, for callers that construct the client themselves.
    /// </summary>
    public static AmazonS3Config CreateClientConfig(this InMemoryBucketServer server) => new()
    {
        ServiceURL = server.BaseAddress.ToString().TrimEnd('/'),
        ForcePathStyle = true,
        AuthenticationRegion = server.Options.Region,
        HttpClientFactory = new InMemoryBucketHttpClientFactory(server),
        // Retrying hides injected faults and wastes test time; callers who test retry policies can turn it back on.
        MaxErrorRetry = 0,
    };
}
