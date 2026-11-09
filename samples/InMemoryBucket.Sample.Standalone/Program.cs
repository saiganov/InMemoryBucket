using InMemoryBucket.Sample.Standalone;

// dotnet run -- --port 9000 --bucket uploads --bucket avatars --access-key test --secret-key test --strict --verbose
var options = StandaloneOptions.Parse(args);
await using var host = await StandaloneServer.StartAsync(options, Console.Out);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine($"Stopping after {host.Server.Requests.Count} requests.");
}
