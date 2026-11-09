using InMemoryBucket.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace InMemoryBucket.Hosting;

/// <summary>
/// Terminal middleware: every request that reaches it is an S3 request for the given server. Mount it on its own
/// Kestrel endpoint (see <see cref="InMemoryBucketHost"/>) or under a branch of an existing pipeline.
/// </summary>
internal sealed class InMemoryBucketMiddleware
{
    private readonly InMemoryBucketServer _server;

    public InMemoryBucketMiddleware(RequestDelegate next, InMemoryBucketServer server)
    {
        _ = next; // terminal
        _server = server;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var s3Request = await ReadAsync(context, _server.Options.Host).ConfigureAwait(false);
        var s3Response = await _server.HandleAsync(s3Request, context.RequestAborted).ConfigureAwait(false);
        await WriteAsync(context, s3Request, s3Response).ConfigureAwait(false);
    }

    internal static async Task<S3Request> ReadAsync(HttpContext context, string serverHost)
    {
        var request = context.Request;
        var host = request.Host.Value ?? string.Empty;

        // RawTarget keeps the percent-encoding the client (and its signature) used; Request.Path is already decoded.
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        string rawPath;
        string rawQuery;
        if (!string.IsNullOrEmpty(rawTarget) && rawTarget[0] == '/')
        {
            var question = rawTarget.IndexOf('?');
            rawPath = question < 0 ? rawTarget : rawTarget[..question];
            rawQuery = question < 0 ? string.Empty : rawTarget[(question + 1)..];
        }
        else
        {
            rawPath = request.PathBase.Add(request.Path).ToUriComponent();
            rawQuery = request.QueryString.Value ?? string.Empty;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = header.Value.Count == 1 ? header.Value[0] ?? string.Empty : string.Join(", ", header.Value!);
        }

        byte[] body;
        if (request.ContentLength is 0 || (request.ContentLength is null && !request.Headers.ContainsKey("Transfer-Encoding")))
        {
            body = Array.Empty<byte>();
        }
        else
        {
            using var buffer = request.ContentLength is { } length and <= int.MaxValue ? new MemoryStream((int)length) : new MemoryStream();
            await request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        // Virtual-hosted addressing works against the configured host name and against the address Kestrel listens on.
        var effectiveServerHost = serverHost;
        var colon = host.LastIndexOf(':');
        var hostName = colon > 0 ? host[..colon] : host;
        if (!hostName.EndsWith("." + serverHost, StringComparison.OrdinalIgnoreCase) && hostName.Contains('.'))
        {
            var listening = hostName[(hostName.IndexOf('.') + 1)..];
            if (listening.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                effectiveServerHost = colon > 0 ? listening + host[colon..] : listening;
            }
        }

        return S3Request.Parse(request.Method, host, rawPath, rawQuery, headers, body, effectiveServerHost);
    }

    internal static async Task WriteAsync(HttpContext context, S3Request s3Request, S3Response s3Response)
    {
        var response = context.Response;
        response.StatusCode = s3Response.StatusCode;
        foreach (var (name, value) in s3Response.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            response.Headers[name] = value;
        }

        var bodyAllowed = s3Response.StatusCode is not (204 or 304) && !s3Request.Method.Equals("HEAD", StringComparison.Ordinal);
        if (s3Request.Method.Equals("HEAD", StringComparison.Ordinal) || bodyAllowed)
        {
            response.ContentLength = s3Response.DeclaredContentLength ?? s3Response.Body.Length;
        }

        if (bodyAllowed && s3Response.Body.Length > 0)
        {
            await response.Body.WriteAsync(s3Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}

/// <summary>Mounts an <see cref="InMemoryBucketServer"/> on an ASP.NET Core pipeline.</summary>
public static class InMemoryBucketApplicationBuilderExtensions
{
    /// <summary>Answers every request on this pipeline from the in-memory S3 server. Terminal: nothing after it runs.</summary>
    public static IApplicationBuilder UseInMemoryBucket(this IApplicationBuilder app, InMemoryBucketServer server)
        => app.UseMiddleware<InMemoryBucketMiddleware>(server);
}
