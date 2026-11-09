using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InMemoryBucket.Sample.Worker;

public sealed class WorkerOptions
{
    public string Bucket { get; set; } = "documents";

    public string InboxPrefix { get; set; } = "inbox/";

    public string ProcessedPrefix { get; set; } = "processed/";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public static class WorkerServiceCollectionExtensions
{
    /// <summary>The worker's own registrations, shared by Program.cs and the tests so both wire the same thing.</summary>
    public static IServiceCollection AddInboxProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection("Worker"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<InboxProcessor>();
        return services;
    }
}

/// <summary>
/// Polls <c>inbox/</c>, turns every text file into an upper-cased copy under <c>processed/</c> stamped with the processing
/// time, then deletes the original. A failure on one file leaves it in the inbox for the next tick. Nothing here knows
/// about InMemoryBucket; the clock comes from <see cref="TimeProvider"/> so tests can drive it.
/// </summary>
public sealed class InboxProcessor(IAmazonS3 s3, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<InboxProcessor> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, clock);
        do
        {
            await ProcessInboxAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over the inbox. Returns how many files were processed.</summary>
    public async Task<int> ProcessInboxAsync(CancellationToken ct)
    {
        var processed = 0;
        var request = new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = _options.InboxPrefix };
        ListObjectsV2Response page;
        do
        {
            page = await s3.ListObjectsV2Async(request, ct);
            foreach (var entry in page.S3Objects ?? [])
            {
                try
                {
                    await ProcessOneAsync(entry.Key, ct);
                    processed++;
                }
                catch (AmazonS3Exception e)
                {
                    logger.LogWarning(e, "Leaving {Key} in the inbox after {ErrorCode}", entry.Key, e.ErrorCode);
                }
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        return processed;
    }

    private async Task ProcessOneAsync(string key, CancellationToken ct)
    {
        using var source = await s3.GetObjectAsync(_options.Bucket, key, ct);
        using var reader = new StreamReader(source.ResponseStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);

        var target = _options.ProcessedPrefix + key[_options.InboxPrefix.Length..];
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = target,
            ContentBody = text.ToUpperInvariant(),
            ContentType = "text/plain",
            Metadata =
            {
                ["processed-at"] = clock.GetUtcNow().ToString("O"),
                ["source-key"] = key,
            },
        }, ct);

        await s3.DeleteObjectAsync(_options.Bucket, key, ct);
        logger.LogInformation("Processed {Key} -> {Target}", key, target);
    }
}
