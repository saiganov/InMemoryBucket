# Known differences

InMemoryBucket follows the AWS S3 documentation. Where MinIO (the reference server the contract suite runs against nightly) or
the fake deliberately behave differently, the difference is listed here and the contract test branches on a flag of
`S3Backend` instead of being skipped.

| Behaviour | AWS S3 / InMemoryBucket | MinIO | Contract flag |
|---|---|---|---|
| Repeated `CreateBucket` on a bucket you own | 200 in `us-east-1`, 409 `BucketAlreadyOwnedByYou` elsewhere. The fake follows `InMemoryBucketOptions.Region`. | Always 409 | `RepeatedCreateBucketConflicts` |
| `ListMultipartUploads` with a prefix or delimiter | Lists every in-progress upload under the prefix and rolls keys up by delimiter, so `TransferUtility.AbortMultipartUploadsAsync` works. | Answers only when the prefix is an exact key; a bucket-wide listing is empty. Documented MinIO simplification. | `ListsMultipartUploadsByPrefix` |
| Default encryption | Every object is reported as SSE-S3: `x-amz-server-side-encryption: AES256` on writes and reads. KMS settings (`aws:kms`, key id, bucket key) are stored and echoed; nothing is encrypted in memory. | Without a KMS configured, no encryption header is sent. | `ReportsDefaultEncryption` |
| `ListObjectsV2` when the key count is a multiple of `max-keys` | The last full page has `IsTruncated=false`. | One more, empty page is returned before `IsTruncated=false`. | `MayReturnTrailingEmptyPage` |

## Fake-only behaviour

- Unknown or unsupported operations (bucket `?tagging`, `?versioning`, UploadPartCopy) answer `501 NotImplemented` naming the operation, so a test fails loudly instead of passing by accident. The same goes for headers asking for features the fake cannot honour: SSE-C (`x-amz-server-side-encryption-customer-*`), Object Lock (`x-amz-object-lock-*`), `x-amz-website-redirect-location`, `x-amz-write-offset-bytes`, and reading one part with `?partNumber`.
- Any other `x-amz-*` request header the fake does not use (an ACL grant, for example) is ignored and reported once per header name as a warning through `Options.Logger`.
- Signatures are parsed but not verified unless `ValidateSignatures` is on; by default any credentials work. In strict mode the region in the credential scope must equal `Options.Region` (S3 answers `AuthorizationHeaderMalformed` too), SigV2 is refused with `InvalidRequest` as in new AWS regions, and chunk signatures inside `aws-chunked` bodies are not checked.
- Presigned URL expiry is checked before the signature, so an expired URL is `AccessDenied: Request has expired` even when tampered. The clock is `Options.TimeProvider`, not the wall clock.
- Over Kestrel (`InMemoryBucket.Hosting`) `bucket.localhost:port` is treated as virtual-hosted addressing; S3 and MinIO need a DNS name that points at them.
- The 2 KB user-metadata limit counts the UTF-8 bytes of each name without `x-amz-meta-` plus its value, as the AWS documentation describes. MinIO counts the prefix as well, so the contract test stays clear of the exact boundary; `CoreTests` pins it.
- `CRC64NVME` checksums are accepted and stored but not verified.
- Keys returned with `encoding-type=url` are encoded the way S3 does it (space is `+`, `/` is kept, everything else percent-encoded); the AWS SDK for .NET does not decode them, so use `WebUtility.UrlDecode`.
