using System.Globalization;
using System.Text;
using InMemoryBucket.Core;

namespace InMemoryBucket.Protocol;

/// <summary>
/// Decodes the <c>aws-chunked</c> transfer format the AWS SDKs use for uploads with trailing checksums:
/// <code>
/// hex-size[;chunk-signature=...]\r\n
/// &lt;data&gt;\r\n
/// ...
/// 0[;chunk-signature=...]\r\n
/// x-amz-checksum-crc32:base64\r\n
/// [x-amz-trailer-signature:...\r\n]
/// \r\n
/// </code>
/// Signatures are parsed and ignored.
/// </summary>
internal static class AwsChunkedDecoder
{
    public static bool IsChunked(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Content-Encoding", out var encoding)
            && encoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return headers.TryGetValue("x-amz-content-sha256", out var sha)
            && sha.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase);
    }

    public static (byte[] Payload, Dictionary<string, string> Trailers) Decode(ReadOnlySpan<byte> encoded, long? decodedLengthHint)
    {
        var payload = new MemoryStream(decodedLengthHint is > 0 and < int.MaxValue ? (int)decodedLengthHint.Value : 0);
        var trailers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;

        while (pos < encoded.Length)
        {
            var header = ReadLine(encoded, ref pos);
            if (header.Length == 0)
            {
                continue; // Tolerate a stray blank line between chunks.
            }

            var semicolon = header.IndexOf(';');
            var sizeText = semicolon >= 0 ? header[..semicolon] : header;
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, $"Malformed aws-chunked body: bad chunk size '{sizeText}'.");
            }

            if (size == 0)
            {
                ReadTrailers(encoded, ref pos, trailers);
                break;
            }

            if (pos + size > encoded.Length)
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, "Malformed aws-chunked body: chunk runs past the end of the payload.");
            }

            payload.Write(encoded.Slice(pos, size));
            pos += size;
            SkipCrlf(encoded, ref pos);
        }

        return (payload.ToArray(), trailers);
    }

    private static void ReadTrailers(ReadOnlySpan<byte> data, ref int pos, Dictionary<string, string> trailers)
    {
        while (pos < data.Length)
        {
            var line = ReadLine(data, ref pos);
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            trailers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
    }

    private static string ReadLine(ReadOnlySpan<byte> data, ref int pos)
    {
        var rest = data[pos..];
        var end = rest.IndexOf((byte)'\n');
        if (end < 0)
        {
            pos = data.Length;
            return Encoding.ASCII.GetString(rest).TrimEnd('\r');
        }

        var line = rest[..end];
        pos += end + 1;
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return Encoding.ASCII.GetString(line);
    }

    private static void SkipCrlf(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos < data.Length && data[pos] == (byte)'\r') pos++;
        if (pos < data.Length && data[pos] == (byte)'\n') pos++;
    }
}
