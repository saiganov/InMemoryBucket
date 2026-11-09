using System.Net;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket;

/// <summary>
/// The HTTP boundary: turns an <see cref="HttpRequestMessage"/> into an <see cref="S3Request"/>, runs it, and builds the response.
/// Thread-safe; the AWS SDK sends multipart parts from several threads at once.
/// </summary>
internal sealed class InMemoryBucketHandler : HttpMessageHandler
{
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow",
    };

    private readonly InMemoryBucketServer _server;

    public InMemoryBucketHandler(InMemoryBucketServer server)
    {
        _server = server;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var s3Request = await ReadAsync(request, _server.Options.Host, cancellationToken).ConfigureAwait(false);
        var s3Response = await _server.HandleAsync(s3Request, cancellationToken).ConfigureAwait(false);
        return ToHttp(s3Response, request);
    }

    internal static async Task<S3Request> ReadAsync(HttpRequestMessage request, string serverHost, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        byte[] body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return S3RequestReader.Read(
            request.Method.Method,
            request.Headers.Host ?? uri.Authority,
            uri.AbsolutePath,
            uri.Query,
            CollectHeaders(request),
            body,
            serverHost);
    }

    private static Dictionary<string, string> CollectHeaders(HttpRequestMessage request)
    {
        // NonValidated keeps each header as one raw string; the parsed view splits User-Agent and friends into tokens,
        // which would break signature verification of signed headers.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.NonValidated)
        {
            headers[header.Key] = header.Value.ToString();
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers.NonValidated)
            {
                headers[header.Key] = header.Value.ToString();
            }

            if (!headers.ContainsKey("Content-Length") && request.Content.Headers.ContentLength is { } length)
            {
                headers["Content-Length"] = length.ToString();
            }
        }

        return headers;
    }

    private static HttpResponseMessage ToHttp(S3Response s3Response, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage((HttpStatusCode)s3Response.StatusCode)
        {
            RequestMessage = request,
            Content = new MemoryContent(s3Response.Body, s3Response.DeclaredContentLength),
        };

        foreach (var (name, value) in s3Response.Headers)
        {
            if (ContentHeaderNames.Contains(name))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    /// <summary>Serves a <see cref="ReadOnlyMemory{T}"/> without copying and can declare a different Content-Length (HEAD).</summary>
    private sealed class MemoryContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly long _declaredLength;

        public MemoryContent(ReadOnlyMemory<byte> data, long? declaredLength)
        {
            _data = data;
            _declaredLength = declaredLength ?? data.Length;
            Headers.ContentLength = _declaredLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => stream.WriteAsync(_data, cancellationToken).AsTask();

        // The MinIO SDK reads responses synchronously; HttpContent throws unless these are overridden.
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => stream.Write(_data.Span);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(CreateContentReadStream(CancellationToken.None));

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
            => System.Runtime.InteropServices.MemoryMarshal.TryGetArray(_data, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(_data.ToArray(), writable: false);

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }
    }
}
