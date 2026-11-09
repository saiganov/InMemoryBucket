using BenchmarkDotNet.Running;
using InMemoryBucket.Benchmarks;

if (args is ["startup", ..])
{
    await StartupComparison.RunAsync(args[1..]);
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
