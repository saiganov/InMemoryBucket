using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace InMemoryBucket.Hosting;

/// <summary>
/// An <see cref="InMemoryBucketServer"/> listening on a loopback TCP port. The same pipeline, store, faults and request
/// log as the in-process handler; use it when the client is another process, a container or the AWS CLI.
/// </summary>
public sealed class InMemoryBucketHost : IAsyncDisposable
{
    private readonly IWebHost _host;

    private InMemoryBucketHost(InMemoryBucketServer server, IWebHost host, Uri baseAddress)
    {
        Server = server;
        _host = host;
        BaseAddress = baseAddress;
    }

    /// <summary>The server being served: seed, inspect, inject faults and read the request log through it.</summary>
    public InMemoryBucketServer Server { get; }

    /// <summary>Where the fake listens, for example <c>http://127.0.0.1:54321/</c>. Use it as the SDK's ServiceURL with path-style addressing.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The TCP port Kestrel listens on.</summary>
    public int Port => BaseAddress.Port;

    /// <summary>Starts Kestrel on <paramref name="port"/> (0 picks a free one) bound to <paramref name="address"/> (loopback by default).</summary>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The two overloads differ in their required first parameter type, so calls stay unambiguous.")]
    public static async Task<InMemoryBucketHost> StartAsync(InMemoryBucketServer server, int port = 0, IPAddress? address = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var host = new WebHostBuilder()
            .UseKestrel(kestrel =>
            {
                kestrel.Listen(address ?? IPAddress.Loopback, port);
                kestrel.Limits.MaxRequestBodySize = null; // S3 objects are bigger than Kestrel's 30 MB default
                kestrel.AddServerHeader = false;
            })
            .ConfigureServices(services => services.AddSingleton(server))
            .Configure(app => app.UseInMemoryBucket(server))
            .Build();

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        var listening = host.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                        ?? throw new InvalidOperationException("Kestrel started without reporting an address.");
        var baseAddress = new Uri(listening.TrimEnd('/') + "/");
        if (baseAddress.Host == "0.0.0.0" || baseAddress.Host == "[::]")
        {
            baseAddress = new UriBuilder(baseAddress) { Host = "127.0.0.1" }.Uri;
        }

        return new InMemoryBucketHost(server, host, baseAddress);
    }

    /// <summary>Starts a fresh server with the given options and serves it.</summary>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "See the other StartAsync overload.")]
    public static Task<InMemoryBucketHost> StartAsync(Action<InMemoryBucketOptions>? configure = null, int port = 0, CancellationToken cancellationToken = default)
        => StartAsync(new InMemoryBucketServer(configure), port, address: null, cancellationToken);

    /// <summary>A plain HttpClient bound to <see cref="BaseAddress"/>, for presigned URLs and hand-made requests.</summary>
    public HttpClient CreateHttpClient() => new() { BaseAddress = BaseAddress };

    /// <summary>Stops Kestrel. The <see cref="Server"/> is not disposed; dispose it yourself if you created it.</summary>
    /// <returns>A task that completes when Kestrel has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}
