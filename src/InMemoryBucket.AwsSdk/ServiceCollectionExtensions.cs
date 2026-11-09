using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace InMemoryBucket.AwsSdk;

/// <summary>Registers the in-memory server and an <see cref="IAmazonS3"/> bound to it in an <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="InMemoryBucketServer"/> singleton and replaces every <see cref="IAmazonS3"/> registration with a client bound to it.
    /// Call it after the production registrations, for example from <c>WebApplicationFactory.ConfigureTestServices</c>.
    /// If an <see cref="InMemoryBucketServer"/> is already registered (by an earlier call or by <c>AddInMemoryBucketHandler()</c>), the clients use that one.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Options for the server. Only the call that registers the server may pass them, so options are never silently dropped.
    /// </param>
    /// <exception cref="InvalidOperationException">An <see cref="InMemoryBucketServer"/> is already registered and <paramref name="configure"/> is not null.</exception>
    public static IServiceCollection AddInMemoryBucket(this IServiceCollection services, Action<InMemoryBucketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null && services.Any(d => d.ServiceType == typeof(InMemoryBucketServer) && !d.IsKeyedService))
        {
            throw new InvalidOperationException(
                "An InMemoryBucketServer is already registered, so these options would be ignored. " +
                "Pass the options to the first AddInMemoryBucket() or AddInMemoryBucketHandler() call and call the others without options.");
        }

        services.TryAddSingleton(sp => new InMemoryBucketServer(o =>
        {
            configure?.Invoke(o);
            // Traces land in the host's logging (Debug: one line per request, Trace: headers and XML) unless the caller set a logger.
            o.Logger ??= sp.GetService<ILoggerFactory>()?.CreateLogger("InMemoryBucket");
        }));
        services.RemoveAll<IAmazonS3>();
        services.AddSingleton<IAmazonS3>(sp => sp.GetRequiredService<InMemoryBucketServer>().CreateClient());
        return services;
    }
}
