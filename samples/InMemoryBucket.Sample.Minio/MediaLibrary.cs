using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace InMemoryBucket.Sample.Minio;

public sealed record MediaItem(string Path, long Size, DateTime? LastModified);

/// <summary>
/// Albums of media files on an S3-compatible server, written against the MinIO .NET SDK: streams in, enumerates listings,
/// hands out share links. Nothing here knows about InMemoryBucket.
/// </summary>
public sealed class MediaLibrary(IMinioClient minio, string bucket)
{
    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
        }
    }

    public Task AddAsync(string album, string fileName, Stream content, long size, string contentType, CancellationToken ct)
        => minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(Key(album, fileName))
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType)
            .WithHeaders(new Dictionary<string, string> { ["x-amz-meta-album"] = album }), ct);

    public async Task<IReadOnlyList<MediaItem>> ListAlbumAsync(string album, CancellationToken ct)
    {
        var items = new List<MediaItem>();
        await foreach (var item in minio.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithPrefix(album + "/").WithRecursive(true), ct))
        {
            items.Add(new MediaItem(item.Key, (long)item.Size, item.LastModifiedDateTime));
        }

        return items;
    }

    public async Task<byte[]?> ReadAsync(string album, string fileName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        try
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(bucket)
                .WithObject(Key(album, fileName))
                .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(buffer, token)), ct);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }

        return buffer.ToArray();
    }

    /// <summary>A link anyone can open for <paramref name="validFor"/>, no credentials needed.</summary>
    public Task<string> ShareAsync(string album, string fileName, TimeSpan validFor)
        => minio.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(bucket)
            .WithObject(Key(album, fileName))
            .WithExpiry((int)validFor.TotalSeconds));

    public async Task<int> DeleteAlbumAsync(string album, CancellationToken ct)
    {
        var keys = (await ListAlbumAsync(album, ct)).Select(i => i.Path).ToList();
        if (keys.Count > 0)
        {
            var errors = await minio.RemoveObjectsAsync(new RemoveObjectsArgs().WithBucket(bucket).WithObjects(keys), ct);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"{errors.Count} objects could not be deleted: {string.Join(", ", errors.Select(e => e.Key))}");
            }
        }

        return keys.Count;
    }

    private static string Key(string album, string fileName) => $"{album}/{fileName}";
}
