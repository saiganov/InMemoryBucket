using System.Text;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Tests;

public class AwsChunkedDecoderTests
{
    [Fact]
    public void Decodes_unsigned_chunks_with_trailer()
    {
        var encoded = Encoding.ASCII.GetBytes(
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n" +
            "x-amz-checksum-crc32:DUoRhQ==\r\n" +
            "\r\n");

        var (payload, trailers) = AwsChunkedDecoder.Decode(encoded, 11);

        Assert.Equal("hello world", Encoding.ASCII.GetString(payload));
        Assert.Equal("DUoRhQ==", trailers["x-amz-checksum-crc32"]);
    }

    [Fact]
    public void Decodes_signed_chunks_and_trailer_signature()
    {
        var encoded = Encoding.ASCII.GetBytes(
            "3;chunk-signature=abc123\r\nabc\r\n" +
            "0;chunk-signature=def456\r\n" +
            "x-amz-checksum-sha256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=\r\n" +
            "x-amz-trailer-signature:deadbeef\r\n" +
            "\r\n");

        var (payload, trailers) = AwsChunkedDecoder.Decode(encoded, 3);

        Assert.Equal("abc", Encoding.ASCII.GetString(payload));
        Assert.Equal("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", trailers["x-amz-checksum-sha256"]);
        Assert.Equal("deadbeef", trailers["x-amz-trailer-signature"]);
    }

    [Fact]
    public void Handles_binary_payload_containing_crlf()
    {
        var data = new byte[] { 0x0D, 0x0A, 0x30, 0x0D, 0x0A, 0xFF };
        var encoded = Encoding.ASCII.GetBytes("6\r\n").Concat(data).Concat(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n")).ToArray();

        var (payload, _) = AwsChunkedDecoder.Decode(encoded, 6);

        Assert.Equal(data, payload);
    }

    [Theory]
    [InlineData("Content-Encoding", "aws-chunked", true)]
    [InlineData("Content-Encoding", "aws-chunked, gzip", true)]
    [InlineData("x-amz-content-sha256", "STREAMING-UNSIGNED-PAYLOAD-TRAILER", true)]
    [InlineData("x-amz-content-sha256", "STREAMING-AWS4-HMAC-SHA256-PAYLOAD", true)]
    [InlineData("x-amz-content-sha256", "UNSIGNED-PAYLOAD", false)]
    [InlineData("Content-Encoding", "gzip", false)]
    public void Detects_chunked_bodies(string header, string value, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = value };

        Assert.Equal(expected, AwsChunkedDecoder.IsChunked(headers));
    }
}
