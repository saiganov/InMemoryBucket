using System.Security.Cryptography;

namespace InMemoryBucket.Core;

/// <summary>
/// ETag computation as S3 does it: quoted MD5 hex for single PUTs, MD5-of-MD5s with a <c>-N</c> suffix for multipart.
/// </summary>
internal static class ETags
{
    public static string ForObject(ReadOnlySpan<byte> data)
        => Quote(Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant());

    public static byte[] RawMd5(ReadOnlySpan<byte> data) => MD5.HashData(data);

    public static string ForMultipart(IReadOnlyList<byte[]> partMd5s)
    {
        var concatenated = new byte[partMd5s.Count * 16];
        for (var i = 0; i < partMd5s.Count; i++)
        {
            partMd5s[i].CopyTo(concatenated, i * 16);
        }

        var hex = Convert.ToHexString(MD5.HashData(concatenated)).ToLowerInvariant();
        return Quote(hex + "-" + partMd5s.Count);
    }

    public static string Quote(string value) => value.StartsWith('"') ? value : "\"" + value + "\"";

    public static string Unquote(string value) => value.Trim('"');

    public static bool AreEqual(string a, string b) => string.Equals(Unquote(a), Unquote(b), StringComparison.OrdinalIgnoreCase);
}
