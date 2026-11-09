using System.Diagnostics;
using System.Globalization;
using System.Text;
using InMemoryBucket.Core;
using InMemoryBucket.Hooks;
using InMemoryBucket.Operations;
using InMemoryBucket.Protocol;
using Microsoft.Extensions.Logging;

namespace InMemoryBucket;

/// <summary>
/// Everything between the transport and the store: presigned-URL expiry, fault rules, the operation itself,
/// the request log and tracing. The HttpMessageHandler and (later) the Kestrel host both end up here.
/// </summary>
internal sealed class S3RequestPipeline
{
    private readonly S3RequestProcessor _processor;
    private readonly InMemoryBucketOptions _options;
    private readonly FaultRules _faults;
    private readonly RequestLog _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _reportedHeaders = new(StringComparer.OrdinalIgnoreCase);

    public S3RequestPipeline(S3RequestProcessor processor, InMemoryBucketOptions options, FaultRules faults, RequestLog log)
    {
        _processor = processor;
        _options = options;
        _faults = faults;
        _log = log;
    }

    /// <summary>Handles one request. Throws only when a <see cref="FaultAction.Throw"/> rule says so or the caller cancelled.</summary>
    public async ValueTask<S3Response> ExecuteAsync(S3Request request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var record = new RecordedRequest(
            request,
            S3Router.Resolve(request),
            Authorization.AccessKey(request),
            _options.TimeProvider.GetUtcNow(),
            _options.CaptureBodies ? request.Body : default(ReadOnlyMemory<byte>?)); // not "null": that would convert via byte[] to an empty memory

        LogRequest(record, request);
        WarnAboutUnknownHeaders(record, request);

        S3Response response;
        try
        {
            var fault = await _faults.ApplyAsync(record, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            switch (fault)
            {
                case FaultAction.Throw t:
                    record.Faulted = true;
                    Complete(record, null, started);
                    _options.Logger?.LogDebug("{Request} threw {Exception} (fault rule)", record, t.Exception.GetType().Name);
                    throw t.Exception;

                case FaultAction.Error e:
                    record.Faulted = true;
                    response = _processor.Error(request, new S3Exception(e.Code, e.Message, request.Resource));
                    break;

                default:
                    response = Authorize(request) is { } denied
                        ? _processor.Error(request, denied)
                        : _processor.Process(request);
                    break;
            }
        }
        catch (Exception) when (record.StatusCode == 0 && !record.Faulted)
        {
            Complete(record, null, started);
            throw;
        }

        Complete(record, response, started);
        LogResponse(record, response);
        return response;
    }

    /// <summary>Expiry first (S3 reports an expired URL before a bad signature), then the signature when strict mode is on.</summary>
    private S3Exception? Authorize(S3Request request)
        => Authorization.CheckPresignedExpiry(request, _options)
           ?? (_options.ValidateSignatures ? SigV4Verifier.Verify(request, _options) : null);

    private void Complete(RecordedRequest record, S3Response? response, long started)
    {
        record.Duration = Stopwatch.GetElapsedTime(started);
        if (response is not null)
        {
            record.StatusCode = response.StatusCode;
            record.ErrorCode = response.ErrorCode;
        }

        _log.Add(record);
    }

    /// <summary>Once per header name: an <c>x-amz-*</c> header the fake ignores may change what S3 would do.</summary>
    private void WarnAboutUnknownHeaders(RecordedRequest record, S3Request request)
    {
        var logger = _options.Logger;
        if (logger is null || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        foreach (var name in S3Headers.Unknown(request))
        {
            if (_reportedHeaders.TryAdd(name, true))
            {
                logger.LogWarning("{Operation}: header {Header} is not supported by InMemoryBucket and was ignored; S3 may behave differently", record.Operation, name);
            }
        }
    }

    private void LogRequest(RecordedRequest record, S3Request request)
    {
        var logger = _options.Logger;
        if (logger is null || !logger.IsEnabled(LogLevel.Trace))
        {
            return;
        }

        var text = new StringBuilder()
            .Append(request.Method).Append(' ').Append(request.Resource).Append(QueryString(request)).AppendLine();
        foreach (var (name, value) in request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(name).Append(": ").Append(name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : value).AppendLine();
        }

        if (request.Body.Length > 0 && IsText(request.Header("Content-Type")))
        {
            text.AppendLine().Append(Encoding.UTF8.GetString(request.Body.Span));
        }

        logger.LogTrace("--> {Operation}\n{Request}", record.Operation, text.ToString());
    }

    private void LogResponse(RecordedRequest record, S3Response response)
    {
        var logger = _options.Logger;
        if (logger is null)
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Trace))
        {
            var text = new StringBuilder();
            foreach (var (name, value) in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(name).Append(": ").Append(value).AppendLine();
            }

            if (response.Body.Length > 0 && IsText(response.Headers.TryGetValue("Content-Type", out var ct) ? ct : null))
            {
                text.AppendLine().Append(Encoding.UTF8.GetString(response.Body.Span));
            }

            logger.LogTrace("<-- {StatusCode} in {Duration}\n{Response}", response.StatusCode, record.Duration, text.ToString());
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("{Request} in {Duration}", record, record.Duration);
        }
    }

    private static bool IsText(string? contentType)
        => contentType is not null
           && (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

    private static string QueryString(S3Request request)
        => request.Query.Count == 0 ? string.Empty : "?" + string.Join("&", request.Query.Select(kv => kv.Value.Length == 0 ? kv.Key : kv.Key + "=" + kv.Value));
}

/// <summary>Reads what the request says about who signed it and when a presigned URL expires. <see cref="SigV4Verifier"/> checks the signature itself.</summary>
internal static class Authorization
{
    public static string? AccessKey(S3Request request)
    {
        var credential = request.QueryValue("X-Amz-Credential");
        if (credential is null && request.Header("Authorization") is { } header)
        {
            const string marker = "Credential=";
            var start = header.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = header.IndexOf(',', start);
                credential = end < 0 ? header[(start + marker.Length)..] : header[(start + marker.Length)..end];
            }
            else if (header.StartsWith("AWS ", StringComparison.Ordinal))
            {
                // SigV2: "AWS access:signature"
                var colon = header.IndexOf(':');
                return colon > 4 ? header[4..colon] : null;
            }
        }

        if (credential is null)
        {
            return request.QueryValue("AWSAccessKeyId");
        }

        var slash = credential.IndexOf('/');
        return (slash < 0 ? credential : credential[..slash]).Trim();
    }

    /// <summary>A presigned URL is valid from X-Amz-Date for X-Amz-Expires seconds (SigV4) or until the Expires epoch (SigV2).</summary>
    public static S3Exception? CheckPresignedExpiry(S3Request request, InMemoryBucketOptions options)
    {
        if (!options.EnforcePresignedExpiry)
        {
            return null;
        }

        var now = options.TimeProvider.GetUtcNow();
        if (request.QueryValue("X-Amz-Signature") is not null)
        {
            if (request.QueryValue("X-Amz-Date") is not { } dateText
                || !DateTimeOffset.TryParseExact(dateText, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
            {
                return new S3Exception(S3ErrorCode.AccessDenied, "X-Amz-Date must be in the ISO8601 Long Format \"yyyyMMdd'T'HHmmss'Z'\"");
            }

            if (!long.TryParse(request.QueryValue("X-Amz-Expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 1 || seconds > 604800)
            {
                return new S3Exception(S3ErrorCode.AccessDenied, "X-Amz-Expires must be non-negative and no longer than one week");
            }

            if (now > signedAt.AddSeconds(seconds))
            {
                return new S3Exception(S3ErrorCode.AccessDenied, "Request has expired");
            }
        }
        else if (request.QueryValue("Signature") is not null && request.QueryValue("Expires") is { } epochText)
        {
            if (!long.TryParse(epochText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                return new S3Exception(S3ErrorCode.AccessDenied, "Invalid date (should be seconds since epoch)");
            }

            if (now > DateTimeOffset.FromUnixTimeSeconds(epoch))
            {
                return new S3Exception(S3ErrorCode.AccessDenied, "Request has expired");
            }
        }

        return null;
    }
}
