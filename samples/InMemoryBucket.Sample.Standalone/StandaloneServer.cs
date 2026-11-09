using InMemoryBucket.Hosting;
using Microsoft.Extensions.Logging;

namespace InMemoryBucket.Sample.Standalone;

public sealed record StandaloneOptions(
    int Port = 9000,
    IReadOnlyList<string>? Buckets = null,
    string AccessKey = "inmemorybucket",
    string SecretKey = "inmemorybucket",
    string Region = "us-east-1",
    bool Strict = false,
    bool Verbose = false)
{
    public static StandaloneOptions Parse(string[] args)
    {
        var options = new StandaloneOptions();
        var buckets = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--port": options = options with { Port = int.Parse(Next()) }; break;
                case "--bucket": buckets.Add(Next()); break;
                case "--access-key": options = options with { AccessKey = Next() }; break;
                case "--secret-key": options = options with { SecretKey = Next() }; break;
                case "--region": options = options with { Region = Next() }; break;
                case "--strict": options = options with { Strict = true }; break;
                case "--verbose": options = options with { Verbose = true }; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}.");
            }
        }

        return options with { Buckets = buckets };
    }
}

public static class StandaloneServer
{
    /// <summary>Starts the fake on <see cref="StandaloneOptions.Port"/> and prints how to point the AWS CLI at it.</summary>
    public static async Task<InMemoryBucketHost> StartAsync(StandaloneOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        using var loggers = LoggerFactory.Create(logging => logging
            .AddSimpleConsole(c => c.SingleLine = true)
            .SetMinimumLevel(options.Verbose ? LogLevel.Trace : LogLevel.Debug));

        var server = new InMemoryBucketServer(o =>
        {
            o.Region = options.Region;
            o.CreateBuckets((options.Buckets ?? []).ToArray());
            o.UseCredentials(options.AccessKey, options.SecretKey);
            o.ValidateSignatures = options.Strict;
            o.Logger = loggers.CreateLogger("InMemoryBucket");
        });

        var host = await InMemoryBucketHost.StartAsync(server, options.Port, cancellationToken: cancellationToken);

        output.WriteLine($"InMemoryBucket listening on {host.BaseAddress}");
        output.WriteLine($"Buckets: {(options.Buckets is { Count: > 0 } b ? string.Join(", ", b) : "(none, create them with the client)")}");
        output.WriteLine($"Signatures: {(options.Strict ? "verified" : "accepted without verification")}");
        output.WriteLine();
        output.WriteLine("AWS CLI:");
        output.WriteLine($"  export AWS_ACCESS_KEY_ID={options.AccessKey} AWS_SECRET_ACCESS_KEY={options.SecretKey} AWS_DEFAULT_REGION={options.Region}");
        output.WriteLine($"  aws --endpoint-url {host.BaseAddress.ToString().TrimEnd('/')} s3 ls");
        output.WriteLine($"  aws --endpoint-url {host.BaseAddress.ToString().TrimEnd('/')} s3 cp ./file.txt s3://{(options.Buckets is { Count: > 0 } f ? f[0] : "my-bucket")}/");
        output.WriteLine();
        output.WriteLine("Press Ctrl+C to stop.");
        return host;
    }
}
