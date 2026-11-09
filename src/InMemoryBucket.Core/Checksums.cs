using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace InMemoryBucket.Core;

/// <summary>The checksum algorithms S3 supports for <c>x-amz-checksum-*</c> headers.</summary>
public enum ChecksumAlgorithm
{
    /// <summary>CRC-32 (<c>x-amz-checksum-crc32</c>).</summary>
    Crc32,

    /// <summary>CRC-32C, Castagnoli (<c>x-amz-checksum-crc32c</c>).</summary>
    Crc32C,

    /// <summary>SHA-1 (<c>x-amz-checksum-sha1</c>).</summary>
    Sha1,

    /// <summary>SHA-256 (<c>x-amz-checksum-sha256</c>).</summary>
    Sha256,

    /// <summary>CRC-64/NVME (<c>x-amz-checksum-crc64nvme</c>).</summary>
    Crc64Nvme,
}

/// <summary>
/// The set of base64-encoded checksums S3 tracks for an object. Any member may be absent.
/// </summary>
/// <param name="Crc32">The base64 CRC-32, or null.</param>
/// <param name="Crc32C">The base64 CRC-32C, or null.</param>
/// <param name="Sha1">The base64 SHA-1, or null.</param>
/// <param name="Sha256">The base64 SHA-256, or null.</param>
/// <param name="Crc64Nvme">The base64 CRC-64/NVME, or null.</param>
public sealed record ChecksumSet(
    string? Crc32 = null,
    string? Crc32C = null,
    string? Sha1 = null,
    string? Sha256 = null,
    string? Crc64Nvme = null)
{
    /// <summary>A set with no checksums.</summary>
    public static readonly ChecksumSet Empty = new();

    /// <summary>True when no checksum is set.</summary>
    public bool IsEmpty => Crc32 is null && Crc32C is null && Sha1 is null && Sha256 is null && Crc64Nvme is null;

    /// <summary>The checksum for <paramref name="algorithm"/>, or null when it is absent.</summary>
    /// <param name="algorithm">The algorithm.</param>
    public string? this[ChecksumAlgorithm algorithm] => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => Crc32,
        ChecksumAlgorithm.Crc32C => Crc32C,
        ChecksumAlgorithm.Sha1 => Sha1,
        ChecksumAlgorithm.Sha256 => Sha256,
        ChecksumAlgorithm.Crc64Nvme => Crc64Nvme,
        _ => null,
    };

    /// <summary>A copy of this set with the checksum for <paramref name="algorithm"/> set to <paramref name="value"/>.</summary>
    /// <param name="algorithm">The algorithm.</param>
    /// <param name="value">The base64-encoded checksum.</param>
    /// <returns>The new set; this one is unchanged.</returns>
    public ChecksumSet With(ChecksumAlgorithm algorithm, string value) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => this with { Crc32 = value },
        ChecksumAlgorithm.Crc32C => this with { Crc32C = value },
        ChecksumAlgorithm.Sha1 => this with { Sha1 = value },
        ChecksumAlgorithm.Sha256 => this with { Sha256 = value },
        ChecksumAlgorithm.Crc64Nvme => this with { Crc64Nvme = value },
        _ => this,
    };

    /// <summary>The checksums that are set, in the order of <see cref="ChecksumAlgorithm"/>.</summary>
    /// <returns>Pairs of algorithm and base64 value.</returns>
    public IEnumerable<(ChecksumAlgorithm Algorithm, string Value)> Entries()
    {
        if (Crc32 is not null) yield return (ChecksumAlgorithm.Crc32, Crc32);
        if (Crc32C is not null) yield return (ChecksumAlgorithm.Crc32C, Crc32C);
        if (Sha1 is not null) yield return (ChecksumAlgorithm.Sha1, Sha1);
        if (Sha256 is not null) yield return (ChecksumAlgorithm.Sha256, Sha256);
        if (Crc64Nvme is not null) yield return (ChecksumAlgorithm.Crc64Nvme, Crc64Nvme);
    }
}

internal static class Checksums
{
    /// <summary>S3 header suffix for an algorithm: <c>x-amz-checksum-{suffix}</c>.</summary>
    public static string HeaderSuffix(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => "crc32",
        ChecksumAlgorithm.Crc32C => "crc32c",
        ChecksumAlgorithm.Sha1 => "sha1",
        ChecksumAlgorithm.Sha256 => "sha256",
        ChecksumAlgorithm.Crc64Nvme => "crc64nvme",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>Algorithm name as S3 spells it in XML and in <c>x-amz-checksum-algorithm</c>: CRC32, CRC32C, SHA1, SHA256, CRC64NVME.</summary>
    public static string AwsName(ChecksumAlgorithm algorithm) => HeaderSuffix(algorithm).ToUpperInvariant();

    public static bool TryParse(string name, out ChecksumAlgorithm algorithm)
    {
        switch (name.ToLowerInvariant())
        {
            case "crc32": algorithm = ChecksumAlgorithm.Crc32; return true;
            case "crc32c": algorithm = ChecksumAlgorithm.Crc32C; return true;
            case "sha1": algorithm = ChecksumAlgorithm.Sha1; return true;
            case "sha256": algorithm = ChecksumAlgorithm.Sha256; return true;
            case "crc64nvme": algorithm = ChecksumAlgorithm.Crc64Nvme; return true;
            default: algorithm = default; return false;
        }
    }

    /// <summary>
    /// Whether this build can compute the algorithm. CRC64NVME is accepted from clients but not verified yet.
    /// </summary>
    public static bool CanCompute(ChecksumAlgorithm algorithm) => algorithm is not ChecksumAlgorithm.Crc64Nvme;

    /// <summary>Raw checksum bytes, big-endian for CRCs as S3 encodes them.</summary>
    public static byte[] ComputeRaw(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> data)
    {
        switch (algorithm)
        {
            case ChecksumAlgorithm.Crc32:
            {
                var raw = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(raw, System.IO.Hashing.Crc32.HashToUInt32(data));
                return raw;
            }
            case ChecksumAlgorithm.Crc32C:
            {
                var raw = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(raw, Crc32C.Compute(data));
                return raw;
            }
            case ChecksumAlgorithm.Sha1:
                return SHA1.HashData(data);
            case ChecksumAlgorithm.Sha256:
                return SHA256.HashData(data);
            default:
                throw new NotSupportedException($"Checksum algorithm {algorithm} is not implemented.");
        }
    }

    public static string Compute(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> data)
        => Convert.ToBase64String(ComputeRaw(algorithm, data));

    /// <summary>
    /// Composite checksum as S3 computes it for multipart uploads: the checksum of the concatenated raw part checksums, suffixed with <c>-N</c>.
    /// </summary>
    public static string ComputeComposite(ChecksumAlgorithm algorithm, IReadOnlyList<string> partChecksumsBase64)
    {
        var concatenated = new List<byte>();
        foreach (var part in partChecksumsBase64)
        {
            concatenated.AddRange(Convert.FromBase64String(part));
        }

        return Compute(algorithm, concatenated.ToArray()) + "-" + partChecksumsBase64.Count;
    }

    /// <summary>
    /// Verifies every declared checksum this build can compute. Throws <see cref="S3Exception"/> with <c>BadDigest</c> on a mismatch.
    /// </summary>
    public static void Verify(ChecksumSet declared, ReadOnlySpan<byte> data)
    {
        foreach (var (algorithm, value) in declared.Entries())
        {
            if (!CanCompute(algorithm))
            {
                continue;
            }

            var actual = Compute(algorithm, data);
            if (!string.Equals(actual, value, StringComparison.Ordinal))
            {
                throw new S3Exception(
                    S3ErrorCode.BadDigest,
                    $"The {AwsName(algorithm)} you specified did not match the calculated checksum.");
            }
        }
    }

    /// <summary>
    /// Fills in every computable algorithm that is absent in <paramref name="declared"/>, so stored objects always carry a CRC32.
    /// </summary>
    public static ChecksumSet Complete(ChecksumSet declared, ReadOnlySpan<byte> data)
    {
        var result = declared;
        if (result.Crc32 is null)
        {
            result = result with { Crc32 = Compute(ChecksumAlgorithm.Crc32, data) };
        }

        return result;
    }
}

/// <summary>CRC-32C (Castagnoli), the variant S3 calls CRC32C. Table-driven, reflected, polynomial 0x82F63B78.</summary>
internal static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
