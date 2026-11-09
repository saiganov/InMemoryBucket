using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace InMemoryBucket.Sample.Resilience;

public sealed class DocumentStoreOptions
{
    public string Bucket { get; set; } = "documents";

    /// <summary>Attempts per operation, including the first.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base of the exponential backoff: 100 ms, 200 ms, 400 ms...</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long one S3 call may take before it is abandoned and retried.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class DocumentStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Saves and loads documents with a small policy: retry 5xx, throttling and transport errors with backoff, give up on
/// 4xx at once, time out slow calls, and fall back to the last good copy on read. Nothing here knows about InMemoryBucket;
/// the SDK client is expected to have its own retries turned off (the store owns the policy).
/// </summary>
public sealed class DocumentStore(IAmazonS3 s3, DocumentStoreOptions options, TimeProvider clock, ILogger<DocumentStore> logger)
{
    private readonly Dictionary<string, string> _lastGood = new();

    public int Attempts { get; private set; }

    public Task SaveAsync(string name, string content, CancellationToken ct)
        => WithRetryAsync("save " + name, async attemptCt =>
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = name,
                ContentBody = content,
                ContentType = "text/plain; charset=utf-8",
            }, attemptCt);
            lock (_lastGood)
            {
                _lastGood[name] = content;
            }

            return 0;
        }, ct);

    /// <summary>Reads the document; when S3 keeps failing, serves the last copy this process saw, or throws when there is none.</summary>
    public async Task<string> LoadAsync(string name, CancellationToken ct)
    {
        try
        {
            var text = await WithRetryAsync("load " + name, async attemptCt =>
            {
                using var response = await s3.GetObjectAsync(options.Bucket, name, attemptCt);
                using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
                return await reader.ReadToEndAsync(attemptCt);
            }, ct);
            lock (_lastGood)
            {
                _lastGood[name] = text;
            }

            return text;
        }
        catch (DocumentStoreException e) when (e.InnerException is not AmazonS3Exception { StatusCode: HttpStatusCode.NotFound })
        {
            lock (_lastGood)
            {
                if (_lastGood.TryGetValue(name, out var cached))
                {
                    logger.LogWarning(e, "Serving {Name} from the last good copy", name);
                    return cached;
                }
            }

            throw;
        }
    }

    private async Task<T> WithRetryAsync<T>(string what, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            Attempts++;
            using var timeout = new CancellationTokenSource(options.AttemptTimeout, clock);
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                return await action(attemptCts.Token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && IsTransient(e, timeout.IsCancellationRequested))
            {
                if (attempt >= options.MaxAttempts)
                {
                    throw new DocumentStoreException($"Could not {what} after {attempt} attempts.", e);
                }

                var delay = options.BackoffBase * Math.Pow(2, attempt - 1);
                logger.LogWarning("{What}: attempt {Attempt} failed with {Error}, retrying in {Delay}", what, attempt, Describe(e), delay);
                await Task.Delay(delay, clock, ct);
            }
            catch (AmazonS3Exception e)
            {
                throw new DocumentStoreException($"Could not {what}: {e.ErrorCode}.", e);
            }
        }
    }

    private static bool IsTransient(Exception e, bool timedOut) => e switch
    {
        OperationCanceledException => timedOut,
        AmazonS3Exception { StatusCode: >= HttpStatusCode.InternalServerError } => true,
        AmazonS3Exception { ErrorCode: "SlowDown" or "RequestTimeout" } => true,
        AmazonS3Exception { InnerException: HttpRequestException or IOException } => true,
        HttpRequestException or IOException => true,
        _ => false,
    };

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "timeout",
        AmazonS3Exception s3 => s3.ErrorCode ?? s3.StatusCode.ToString(),
        _ => e.GetType().Name,
    };
}
