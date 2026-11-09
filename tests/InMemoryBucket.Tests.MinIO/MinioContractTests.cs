using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using InMemoryBucket.ContractTests;

namespace InMemoryBucket.Tests.MinIO;

/// <summary>
/// A MinIO started by Testcontainers, or the one named by INMEMORYBUCKET_MINIO_ENDPOINT when set.
/// The client is configured exactly like a production client pointed at MinIO: path style, SigV4, no retries.
/// </summary>
/// <remarks>
/// MinIO left Docker Hub in September 2026 and quay.io/minio/minio now refuses anonymous pulls, so the default is the
/// Chainguard build, whose <c>latest</c> tag is public. The image runs as a non-root user, hence the tmpfs data directory.
/// Override the image with INMEMORYBUCKET_MINIO_IMAGE and, for images with their own entrypoint (for example
/// <c>bitnamilegacy/minio</c>, which needs <c>/opt/bitnami/scripts/minio/run.sh</c>), the command with INMEMORYBUCKET_MINIO_COMMAND.
/// </remarks>
public sealed class MinioBackend : S3Backend, IAsyncLifetime
{
    private const string DefaultImage = "cgr.dev/chainguard/minio:latest";
    private const int MinioPort = 9000;

    private IContainer? _container;
    private IAmazonS3? _client;

    public override S3Flavor Flavor => S3Flavor.MinIO;

    public override IAmazonS3 Client => _client ?? throw new InvalidOperationException("MinIO backend is not initialised.");

    public async ValueTask InitializeAsync()
    {
        string endpoint;
        var accessKey = Env("INMEMORYBUCKET_MINIO_ACCESS_KEY") ?? "minioadmin";
        var secretKey = Env("INMEMORYBUCKET_MINIO_SECRET_KEY") ?? "minioadmin";

        if (Env("INMEMORYBUCKET_MINIO_ENDPOINT") is { } configured)
        {
            endpoint = configured;
        }
        else
        {
            var command = (Env("INMEMORYBUCKET_MINIO_COMMAND") ?? "server /data --address :9000").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _container = new ContainerBuilder(Env("INMEMORYBUCKET_MINIO_IMAGE") ?? DefaultImage)
                .WithEnvironment("MINIO_ROOT_USER", accessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
                .WithCommand(command)
                .WithTmpfsMount("/data")
                .WithPortBinding(MinioPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(MinioPort).ForPath("/minio/health/ready")))
                .Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
            endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(MinioPort)}";
        }

        _client = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string? Env(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

public sealed class MinioContractTests(MinioBackend backend) : S3ContractTests(backend), IClassFixture<MinioBackend>;
