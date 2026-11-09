# InMemoryBucket.AwsSdk

Connects the AWS SDK for .NET (AWSSDK.S3 4.0.13.1 or later 4.x) to [InMemoryBucket](https://github.com/saiganov/InMemoryBucket), an in-process S3 fake for tests. You get a real `AmazonS3Client` whose requests are answered from memory.

```csharp
using Amazon.S3;
using InMemoryBucket;
using InMemoryBucket.AwsSdk;

// Without DI
using var s3 = new InMemoryBucketServer(o => o.CreateBuckets("uploads"));
IAmazonS3 client = s3.CreateClient();

// WebApplicationFactory: replaces every IAmazonS3 registration
builder.ConfigureTestServices(services =>
    services.AddInMemoryBucket(o => o.CreateBuckets("uploads")));
```

`TransferUtility`, presigned URLs and the SDK's checksums all work as against S3. Documentation and samples: [https://github.com/saiganov/InMemoryBucket](https://github.com/saiganov/InMemoryBucket).
