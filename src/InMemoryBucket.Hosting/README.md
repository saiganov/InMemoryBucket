# InMemoryBucket.Hosting

Serves an [InMemoryBucket](https://github.com/saiganov/InMemoryBucket) S3 fake over real HTTP on Kestrel. Use it for clients that cannot take an `HttpMessageHandler`: other processes, containers, the AWS CLI, or any SDK pointed at an endpoint URL.

```csharp
using InMemoryBucket.Hosting;

await using var host = await InMemoryBucketHost.StartAsync(o => o.CreateBuckets("uploads"));
// host.BaseAddress is http://127.0.0.1:<port>/. Use it as the endpoint, with path-style addressing.
// host.Server is the same InMemoryBucketServer: seed, inspect, inject faults.
```

`app.UseInMemoryBucket(server)` mounts the fake on an existing ASP.NET Core pipeline. Documentation and samples: [https://github.com/saiganov/InMemoryBucket](https://github.com/saiganov/InMemoryBucket).
