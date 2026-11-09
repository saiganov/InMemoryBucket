using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InMemoryBucket.Http;

/// <summary>Routes <c>IHttpClientFactory</c> clients to the in-memory server.</summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Makes the in-memory server the primary handler of this <see cref="System.Net.Http.HttpClient"/>, so every request it sends is answered by the fake.
    /// Delegating handlers added with <c>AddHttpMessageHandler</c> (retries, logging, auth) still run in front of it.
    /// The server is the <see cref="InMemoryBucketServer"/> singleton in the container, the same one <c>AddInMemoryBucket()</c> from
    /// <c>InMemoryBucket.AwsSdk</c> registers; if there is none yet, this call registers it.
    /// Call it after the production registrations of the client (for example last in <c>ConfigureTestServices</c>):
    /// a primary handler configured later replaces the fake, and production code that casts the primary handler to
    /// <see cref="System.Net.Http.SocketsHttpHandler"/> fails if it runs after this call.
    /// The client's <c>BaseAddress</c> is left as it is. Path-style requests (<c>https://any-host/bucket/key</c>) work with any host;
    /// virtual-hosted requests (<c>https://bucket.host/key</c>) are recognised only under <see cref="InMemoryBucketOptions.Host"/>,
    /// so set it to the production S3 host for such clients.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddHttpClient()</c>.</param>
    /// <param name="configure">
    /// Options for the server. Only the call that registers the server may pass them, so options are never silently dropped:
    /// configure the server in the first registration and call this without options for further clients.
    /// </param>
    /// <exception cref="InvalidOperationException">An <see cref="InMemoryBucketServer"/> is already registered and <paramref name="configure"/> is not null.</exception>
    public static IHttpClientBuilder AddInMemoryBucketHandler(this IHttpClientBuilder builder, Action<InMemoryBucketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterServer(builder.Services, configure);
        builder.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<InMemoryBucketServer>().CreateHandler());
        return builder;
    }

    private static void RegisterServer(IServiceCollection services, Action<InMemoryBucketOptions>? configure)
    {
        if (services.Any(d => d.ServiceType == typeof(InMemoryBucketServer) && !d.IsKeyedService))
        {
            if (configure is not null)
            {
                throw new InvalidOperationException(
                    "An InMemoryBucketServer is already registered, so these options would be ignored. " +
                    "Pass the options to the first AddInMemoryBucket() or AddInMemoryBucketHandler() call and call the others without options.");
            }

            return;
        }

        services.AddSingleton(sp => new InMemoryBucketServer(o =>
        {
            configure?.Invoke(o);
            // Same default as AddInMemoryBucket(): traces land in the host's logging unless the caller set a logger.
            o.Logger ??= sp.GetService<ILoggerFactory>()?.CreateLogger("InMemoryBucket");
        }));
    }
}
