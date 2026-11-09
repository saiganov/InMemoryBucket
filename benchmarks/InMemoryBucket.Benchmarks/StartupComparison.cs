using System.Diagnostics;
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using InMemoryBucket.AwsSdk;

namespace InMemoryBucket.Benchmarks;

/// <summary>
/// Time to the first green test: from the start of a fresh test process to a PutObject and GetObject that succeeded,
/// with InMemoryBucket and with MinIO started by Testcontainers (the image already pulled). Each run is a new process,
/// so JIT and SDK start-up are counted, as they are in a real test run.
/// </summary>
/// <remarks>
/// <c>startup [runs]</c> runs both and prints the medians; <c>startup fake</c> and <c>startup minio</c> are one run each.
/// The MinIO image is the one the contract tests use; override it with INMEMORYBUCKET_MINIO_IMAGE and INMEMORYBUCKET_MINIO_COMMAND.
/// </remarks>
internal static class StartupComparison
{
    private const string DefaultMinioImage = "cgr.dev/chainguard/minio:latest";
    private const int MinioPort = 9000;
    private static readonly byte[] Payload = "hello"u8.ToArray();

    public static async Task RunAsync(string[] args)
    {
        switch (args)
        {
            case ["fake"]:
                await MeasureAsync(FakeAsync);
                return;
            case ["minio"]:
                await MeasureAsync(MinioAsync);
                return;
        }

        var runs = args is [var n] ? int.Parse(n, CultureInfo.InvariantCulture) : 5;
        var fake = Median(Enumerable.Range(0, runs).Select(_ => RunChild("fake")).ToList());
        var minio = Median(Enumerable.Range(0, runs).Select(_ => RunChild("minio")).ToList());
        Console.WriteLine($"Time to the first green test, median of {runs} runs:");
        Console.WriteLine($"  InMemoryBucket          {fake,8:F0} ms");
        Console.WriteLine($"  MinIO via Testcontainers{minio,8:F0} ms");
        Console.WriteLine($"  ratio                   {minio / fake,8:F1}x");
    }

    // The time is taken when the round trip succeeds, before disposal: a test is green at that point.
    private static async Task MeasureAsync(Func<Stopwatch, Task<TimeSpan>> scenario)
    {
        var elapsed = await scenario(Stopwatch.StartNew());
        Console.WriteLine(elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
    }

    private static async Task<TimeSpan> FakeAsync(Stopwatch stopwatch)
    {
        using var server = new InMemoryBucketServer(o => o.CreateBuckets("test"));
        using var client = server.CreateClient();
        await RoundTripAsync(client);
        return stopwatch.Elapsed;
    }

    private static async Task<TimeSpan> MinioAsync(Stopwatch stopwatch)
    {
        var command = (Env("INMEMORYBUCKET_MINIO_COMMAND") ?? "server /data --address :9000").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await using var container = new ContainerBuilder(Env("INMEMORYBUCKET_MINIO_IMAGE") ?? DefaultMinioImage)
            .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
            .WithCommand(command)
            .WithTmpfsMount("/data")
            .WithPortBinding(MinioPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(MinioPort).ForPath("/minio/health/ready")))
            .Build();
        await container.StartAsync();
        using var client = new AmazonS3Client("minioadmin", "minioadmin", new AmazonS3Config
        {
            ServiceURL = $"http://{container.Hostname}:{container.GetMappedPublicPort(MinioPort)}",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
        await client.PutBucketAsync("test");
        await RoundTripAsync(client);
        return stopwatch.Elapsed;
    }

    private static async Task RoundTripAsync(IAmazonS3 client)
    {
        using var body = new MemoryStream(Payload, writable: false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "test", Key = "k", InputStream = body });
        using var response = await client.GetObjectAsync("test", "k");
        await response.ResponseStream.CopyToAsync(Stream.Null);
    }

    private static double RunChild(string scenario)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, ["startup", scenario])
        {
            RedirectStandardOutput = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
        {
            start.ArgumentList.Insert(0, typeof(StartupComparison).Assembly.Location);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'startup {scenario}' failed with exit code {process.ExitCode}.");
        }

        return double.Parse(output.Trim().Split('\n')[^1], CultureInfo.InvariantCulture);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    private static string? Env(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
