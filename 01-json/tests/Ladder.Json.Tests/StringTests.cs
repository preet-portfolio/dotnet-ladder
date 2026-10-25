using System.Buffers;
using System.Text;

namespace Ladder.Json.Tests;

public class StringTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("\\\"\\\\\\/\\b\\f\\n\\r\\t", "\"\\/\b\f\n\r\t")]
    [InlineData("\\u0041\\u00e9\\u00E9", "Aéé")]
    [InlineData("\\uD83D\\uDE00", "😀")]
    [InlineData("é\\n😀x", "é\n😀x")]
    [InlineData("\\uFFFF", "\uFFFF")]
    public void Unescapes(string raw, string expected)
    {
        Assert.Equal(expected, Decode(raw, out _));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uD800x")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800\\u0041")]
    [InlineData("\\uD800\\\\u0041")]
    [InlineData("\\uD800\\uD800")]
    [InlineData("\\uD800\\n")]
    public void Rejects_unpaired_surrogates(string raw)
    {
        Assert.Null(Decode(raw, out JsonErrorReason reason));
        Assert.Equal(JsonErrorReason.InvalidSurrogate, reason);
    }

    [Fact]
    public void Rejects_invalid_utf8()
    {
        OperationStatus status = JsonString.Unescape([(byte)'a', 0xFF], new char[4], out _, out JsonErrorReason reason);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(JsonErrorReason.InvalidUtf8, reason);
    }

    [Theory]
    [InlineData("abc", 2)]
    [InlineData("a\\n", 1)]
    [InlineData("\\uD83D\\uDE00", 1)]
    [InlineData("\\u0041", 0)]
    public void Reports_destination_too_small(string raw, int size)
    {
        Assert.Equal(OperationStatus.DestinationTooSmall, JsonString.Unescape(Encoding.UTF8.GetBytes(raw), new char[size], out _, out JsonErrorReason reason));
        Assert.Equal(JsonErrorReason.None, reason);
    }

    [Fact]
    public void Surrogate_pair_fits_a_buffer_of_exactly_two()
    {
        Assert.Equal(OperationStatus.Done, JsonString.Unescape("\\uD83D\\uDE00"u8, new char[2], out int written, out _));
        Assert.Equal(2, written);
    }

    [Fact]
    public void Raw_length_is_always_a_large_enough_buffer()
    {
        // Each escape is at least as long in bytes as its UTF-16 output, and each UTF-8 byte yields at most one char.
        byte[] raw = Encoding.UTF8.GetBytes("\\uD83D\\uDE00é😀\\n");
        var buffer = new char[raw.Length];
        Assert.Equal(OperationStatus.Done, JsonString.Unescape(raw, buffer, out int written, out _));
        Assert.Equal("😀é😀\n", new string(buffer, 0, written));
    }

    private static string? Decode(string raw, out JsonErrorReason reason)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        var buffer = new char[bytes.Length];
        OperationStatus status = JsonString.Unescape(bytes, buffer, out int written, out reason);
        return status == OperationStatus.Done ? new string(buffer, 0, written) : null;
    }
}
