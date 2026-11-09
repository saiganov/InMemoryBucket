using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace InMemoryBucket.Core;

/// <summary>
/// A bucket. Writes take <c>_gate</c> and swap an immutable sorted map; reads and listings work on whatever snapshot they grab and never block.
/// </summary>
public sealed class Bucket
{
    private const int MinPartSize = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Func<string> _uploadIds;
    private readonly StorageBudget _budget;
    private readonly ConcurrentDictionary<string, MultipartUpload> _uploads = new(StringComparer.Ordinal);
    private bool _deleted;
    private ImmutableSortedDictionary<string, S3Object> _objects =
        ImmutableSortedDictionary.Create<string, S3Object>(StringComparer.Ordinal);

    internal Bucket(string name, DateTimeOffset creationDate, TimeProvider clock, Func<string> uploadIds, StorageBudget budget)
    {
        _budget = budget;
        Name = name;
        CreationDate = creationDate;
        _clock = clock;
        _uploadIds = uploadIds;
    }

    /// <summary>The bucket name.</summary>
    public string Name { get; }

    /// <summary>When the bucket was created, by the server's <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset CreationDate { get; }

    /// <summary>The number of objects in the bucket now.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>A consistent snapshot of every object, in S3 key order.</summary>
    public IReadOnlyCollection<S3Object> Objects => _objects.Values.ToArray();

    /// <summary>A snapshot of the multipart uploads in progress, in no particular order.</summary>
    public IReadOnlyCollection<MultipartUpload> Uploads => _uploads.Values.ToArray();

    internal S3Object Put(string key, ReadOnlyMemory<byte> data, ObjectAttributes attributes, ChecksumSet checksums, string? checksumType = null, WriteCondition condition = default, DateTimeOffset? lastModified = null)
    {
        ObjectLimits.Check(key, attributes);
        condition.Validate();
        var obj = new S3Object(
            key,
            data,
            ETags.ForObject(data.Span),
            lastModified ?? _clock.GetUtcNow(),
            attributes,
            Checksums.Complete(checksums, data.Span),
            checksumType ?? (checksums.IsEmpty ? null : "FULL_OBJECT"));

        lock (_gate)
        {
            EnsureLive();
            var previous = _objects.GetValueOrDefault(key);
            condition.Check(Name, key, previous);
            _budget.Reserve(obj.Size - (previous?.Size ?? 0));
            _objects = _objects.SetItem(key, obj);
        }

        return obj;
    }

    /// <summary>Replaces an object's tag set in place, keeping its ETag and LastModified as PutObjectTagging does on S3.</summary>
    internal S3Object SetTags(string key, ImmutableDictionary<string, string> tags)
    {
        lock (_gate)
        {
            EnsureLive();
            var current = Get(key);
            var updated = current with { Attributes = current.Attributes with { Tags = tags } };
            _objects = _objects.SetItem(key, updated);
            return updated;
        }
    }

    /// <summary>
    /// Marks the bucket deleted if it holds no objects, under the write lock, so a write racing with DeleteBucket either lands
    /// first (and the delete fails with BucketNotEmpty) or fails with NoSuchBucket. It is never accepted and then lost.
    /// </summary>
    internal bool TryMarkDeleted()
    {
        lock (_gate)
        {
            if (!_objects.IsEmpty)
            {
                return false;
            }

            _deleted = true;
            return true;
        }
    }

    /// <summary>Call under <c>_gate</c>: a bucket removed from its store takes no more writes.</summary>
    private void EnsureLive()
    {
        if (_deleted)
        {
            throw new S3Exception(S3ErrorCode.NoSuchBucket, resource: "/" + Name);
        }
    }

    /// <summary>Looks up an object by key.</summary>
    /// <param name="key">The object key.</param>
    /// <param name="obj">The object when found; otherwise null.</param>
    /// <returns>True when the object exists.</returns>
    public bool TryGet(string key, out S3Object obj) => _objects.TryGetValue(key, out obj!);

    /// <summary>Gets an object by key.</summary>
    /// <param name="key">The object key.</param>
    /// <returns>The object.</returns>
    /// <exception cref="S3Exception">The key does not exist (<see cref="S3ErrorCode.NoSuchKey"/>).</exception>
    public S3Object Get(string key)
        => TryGet(key, out var obj) ? obj : throw new S3Exception(S3ErrorCode.NoSuchKey, resource: "/" + Name + "/" + key);

    internal bool Delete(string key)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out var removed))
            {
                return false;
            }

            _objects = _objects.Remove(key);
            _budget.Release(removed.Size);
            return true;
        }
    }

    internal ListObjectsV2Result ListObjectsV2(ListObjectsV2Query query)
    {
        var snapshot = _objects;
        var maxKeys = Math.Clamp(query.MaxKeys, 0, 1000);

        // Keys strictly greater than this one are candidates.
        string? after = query.ContinuationToken is not null
            ? ContinuationTokens.Decode(query.ContinuationToken)
            : query.StartAfter;

        var contents = new List<S3Object>();
        var commonPrefixes = new List<string>();
        string? lastEmitted = null;
        var truncated = false;

        foreach (var (key, obj) in snapshot)
        {
            if (!key.StartsWith(query.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (after is not null && string.CompareOrdinal(key, after) <= 0)
            {
                continue;
            }

            string? commonPrefix = null;
            if (!string.IsNullOrEmpty(query.Delimiter))
            {
                var idx = key.IndexOf(query.Delimiter, query.Prefix.Length, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    commonPrefix = key[..(idx + query.Delimiter.Length)];
                    if (commonPrefix == after || (commonPrefixes.Count > 0 && commonPrefixes[^1] == commonPrefix))
                    {
                        continue; // Already rolled up, on this page or as the previous page's marker.
                    }
                }
            }

            if (contents.Count + commonPrefixes.Count >= maxKeys)
            {
                truncated = true;
                break;
            }

            if (commonPrefix is not null)
            {
                commonPrefixes.Add(commonPrefix);
                lastEmitted = commonPrefix;
            }
            else
            {
                contents.Add(obj);
                lastEmitted = key;
            }
        }

        return new ListObjectsV2Result(
            contents,
            commonPrefixes,
            truncated,
            truncated && lastEmitted is not null ? ContinuationTokens.Encode(lastEmitted) : null,
            contents.Count + commonPrefixes.Count);
    }

    internal MultipartUpload CreateMultipartUpload(string key, ObjectAttributes attributes, ChecksumAlgorithm? checksumAlgorithm, string? checksumType)
    {
        ObjectLimits.Check(key, attributes);
        var uploadId = _uploadIds();
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new InvalidOperationException("UploadIdGenerator returned a null or empty upload id.");
        }

        var upload = new MultipartUpload(uploadId, key, attributes, _clock.GetUtcNow(), checksumAlgorithm, checksumType);
        lock (_gate)
        {
            EnsureLive();
            if (!_uploads.TryAdd(uploadId, upload))
            {
                throw new InvalidOperationException($"UploadIdGenerator returned '{uploadId}', which an in-progress upload in bucket '{Name}' already uses. Upload ids must be unique.");
            }
        }

        return upload;
    }

    internal MultipartUpload GetUpload(string uploadId, string key)
    {
        if (!_uploads.TryGetValue(uploadId, out var upload) || upload.Key != key)
        {
            throw new S3Exception(S3ErrorCode.NoSuchUpload, resource: "/" + Name + "/" + key);
        }

        return upload;
    }

    internal UploadedPart UploadPart(MultipartUpload upload, int partNumber, byte[] data, ChecksumSet checksums)
    {
        if (partNumber is < 1 or > 10_000)
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000, inclusive");
        }

        var md5 = ETags.RawMd5(data);
        var part = new UploadedPart(
            partNumber,
            data,
            md5,
            ETags.Quote(Convert.ToHexString(md5).ToLowerInvariant()),
            upload.ChecksumAlgorithm is { } algorithm && checksums[algorithm] is null && Checksums.CanCompute(algorithm)
                ? checksums.With(algorithm, Checksums.Compute(algorithm, data))
                : checksums,
            _clock.GetUtcNow());
        _budget.Reserve(data.Length);
        UploadedPart? replaced = null;
        upload.Parts.AddOrUpdate(
            partNumber,
            _ =>
            {
                replaced = null; // the add path can run after a failed update attempt: forget what that attempt saw
                return part;
            },
            (_, old) =>
            {
                replaced = old;
                return part;
            });
        if (replaced is not null)
        {
            _budget.Release(replaced.Data.Length);
        }

        if (!_uploads.ContainsKey(upload.UploadId) && upload.Parts.TryRemove(KeyValuePair.Create(partNumber, part)))
        {
            _budget.Release(data.Length); // the upload was aborted or completed meanwhile: this part is garbage
        }

        return part;
    }

    internal bool AbortUpload(string uploadId)
    {
        if (!_uploads.TryRemove(uploadId, out var upload))
        {
            return false;
        }

        ReleaseParts(upload);
        return true;
    }

    /// <summary>Drops every in-progress upload, as S3 does when the bucket is deleted.</summary>
    internal void AbortAllUploads()
    {
        foreach (var uploadId in _uploads.Keys)
        {
            AbortUpload(uploadId);
        }
    }

    /// <summary>Gives back each part's bytes exactly once, even if UploadPart races with Abort or Complete.</summary>
    private void ReleaseParts(MultipartUpload upload)
    {
        foreach (var entry in upload.Parts)
        {
            if (upload.Parts.TryRemove(entry))
            {
                _budget.Release(entry.Value.Data.Length);
            }
        }
    }

    /// <summary>
    /// In-progress uploads ordered by key, then upload id, as S3 lists them. Markers follow the same order,
    /// so a client can page through with the NextKeyMarker/NextUploadIdMarker pair.
    /// </summary>
    internal ListMultipartUploadsResult ListMultipartUploads(ListMultipartUploadsQuery query)
    {
        var maxUploads = Math.Clamp(query.MaxUploads, 0, 1000);
        var ordered = _uploads.Values
            .Where(u => u.Key.StartsWith(query.Prefix, StringComparison.Ordinal))
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .ThenBy(u => u.UploadId, StringComparer.Ordinal);

        var uploads = new List<MultipartUpload>();
        var commonPrefixes = new List<string>();
        var truncated = false;
        string? nextKey = null;
        string? nextUploadId = null;

        foreach (var upload in ordered)
        {
            if (query.KeyMarker is { Length: > 0 } keyMarker)
            {
                var cmp = string.CompareOrdinal(upload.Key, keyMarker);
                if (cmp < 0 || (cmp == 0 && (string.IsNullOrEmpty(query.UploadIdMarker) || string.CompareOrdinal(upload.UploadId, query.UploadIdMarker) <= 0)))
                {
                    continue;
                }
            }

            string? commonPrefix = null;
            if (!string.IsNullOrEmpty(query.Delimiter))
            {
                var idx = upload.Key.IndexOf(query.Delimiter, query.Prefix.Length, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    commonPrefix = upload.Key[..(idx + query.Delimiter.Length)];
                    if (commonPrefix == query.KeyMarker || (commonPrefixes.Count > 0 && commonPrefixes[^1] == commonPrefix))
                    {
                        continue; // Already rolled up, on this page or as the previous page's marker.
                    }
                }
            }

            if (uploads.Count + commonPrefixes.Count >= maxUploads)
            {
                truncated = true;
                break;
            }

            if (commonPrefix is not null)
            {
                commonPrefixes.Add(commonPrefix);
                nextKey = commonPrefix;
                nextUploadId = null;
            }
            else
            {
                uploads.Add(upload);
                nextKey = upload.Key;
                nextUploadId = upload.UploadId;
            }
        }

        return new ListMultipartUploadsResult(uploads, commonPrefixes, truncated, truncated ? nextKey : null, truncated ? nextUploadId : null);
    }

    /// <summary>
    /// Validates the client's part list against what was uploaded, assembles the object and publishes it atomically.
    /// </summary>
    internal S3Object CompleteMultipartUpload(MultipartUpload upload, IReadOnlyList<CompletedPart> parts, ChecksumSet declaredObjectChecksums, string? declaredChecksumType, WriteCondition condition = default)
    {
        condition.Validate();
        if (parts.Count == 0)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML, "The XML you provided was not well-formed or did not validate against our published schema");
        }

        var uploaded = new List<UploadedPart>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var requested = parts[i];
            if (i > 0 && requested.PartNumber <= parts[i - 1].PartNumber)
            {
                throw new S3Exception(S3ErrorCode.InvalidPartOrder);
            }

            if (!upload.Parts.TryGetValue(requested.PartNumber, out var part) || !ETags.AreEqual(part.ETag, requested.ETag))
            {
                throw new S3Exception(S3ErrorCode.InvalidPart);
            }

            foreach (var (algorithm, value) in requested.Checksums.Entries())
            {
                if (part.Checksums[algorithm] is { } stored && !string.Equals(stored, value, StringComparison.Ordinal))
                {
                    throw new S3Exception(S3ErrorCode.InvalidPart, $"The {Checksums.AwsName(algorithm)} you specified for part {requested.PartNumber} did not match the part's checksum.");
                }
            }

            if (i < parts.Count - 1 && part.Data.Length < MinPartSize)
            {
                throw new S3Exception(S3ErrorCode.EntityTooSmall);
            }

            uploaded.Add(part);
        }

        var total = uploaded.Sum(p => (long)p.Data.Length);
        var data = new byte[total];
        var offset = 0;
        foreach (var part in uploaded)
        {
            part.Data.CopyTo(data, offset);
            offset += part.Data.Length;
        }

        var etag = ETags.ForMultipart(uploaded.Select(p => p.Md5).ToList());
        var (checksums, checksumType) = MultipartChecksums(upload, uploaded, data, declaredObjectChecksums, declaredChecksumType);

        var obj = new S3Object(upload.Key, data, etag, _clock.GetUtcNow(), upload.Attributes, checksums, checksumType);
        lock (_gate)
        {
            EnsureLive();
            var previous = _objects.GetValueOrDefault(upload.Key);
            condition.Check(Name, upload.Key, previous);
            _budget.Reserve(obj.Size - (previous?.Size ?? 0)); // parts still count until released below: memory really holds both
            _objects = _objects.SetItem(upload.Key, obj);
        }

        if (_uploads.TryRemove(upload.UploadId, out _))
        {
            ReleaseParts(upload);
        }

        return obj;
    }

    private static (ChecksumSet Checksums, string? Type) MultipartChecksums(
        MultipartUpload upload,
        List<UploadedPart> parts,
        byte[] data,
        ChecksumSet declared,
        string? declaredType)
    {
        var algorithm = upload.ChecksumAlgorithm
            ?? declared.Entries().Select(e => (ChecksumAlgorithm?)e.Algorithm).FirstOrDefault()
            ?? parts[0].Checksums.Entries().Select(e => (ChecksumAlgorithm?)e.Algorithm).FirstOrDefault();

        if (algorithm is null || !Checksums.CanCompute(algorithm.Value))
        {
            // Nothing we can verify: keep a CRC32 of the full object so GetObject can still report one.
            return (new ChecksumSet(Crc32: Checksums.Compute(ChecksumAlgorithm.Crc32, data)), "FULL_OBJECT");
        }

        var type = declaredType ?? upload.ChecksumType
            ?? (declared[algorithm.Value] is { } d && d.Contains('-') ? "COMPOSITE" : null)
            ?? (parts.All(p => p.Checksums[algorithm.Value] is not null) ? "COMPOSITE" : "FULL_OBJECT");

        string computed;
        if (type == "COMPOSITE")
        {
            var partChecksums = parts.Select(p => p.Checksums[algorithm.Value]).ToList();
            if (partChecksums.Any(c => c is null))
            {
                throw new S3Exception(S3ErrorCode.InvalidPart, "Composite checksums require every part to carry a checksum.");
            }

            computed = Checksums.ComputeComposite(algorithm.Value, partChecksums!);
        }
        else
        {
            computed = Checksums.Compute(algorithm.Value, data);
        }

        if (declared[algorithm.Value] is { } expected && !string.Equals(expected, computed, StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.BadDigest, $"The {Checksums.AwsName(algorithm.Value)} you specified did not match the calculated checksum.");
        }

        return (ChecksumSet.Empty.With(algorithm.Value, computed), type);
    }
}
