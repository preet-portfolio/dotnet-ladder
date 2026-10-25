using System.Buffers;
using System.Text;

namespace Ladder.Json.Tests;

public class TokenizerTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-0.0e-0")]
    [InlineData("1E+2")]
    [InlineData("123.456e789")]
    [InlineData("\"\"")]
    [InlineData("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\\u00e9\"")]
    [InlineData(" \t\r\n[ ] ")]
    [InlineData("{\"a\":[true,false,null],\"a\":{}}")]
    [InlineData("\"é😀\"")]
    public void Accepts_valid_documents(string json)
    {
        Assert.Equal(default, Tokenize(json, out _));
    }

    [Theory]
    [InlineData("", 0, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("   ", 3, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("01", 1, JsonErrorReason.UnexpectedByte)]
    [InlineData("-", 1, JsonErrorReason.InvalidNumber)]
    [InlineData("-a", 1, JsonErrorReason.InvalidNumber)]
    [InlineData("1.", 2, JsonErrorReason.InvalidNumber)]
    [InlineData("1.e3", 2, JsonErrorReason.InvalidNumber)]
    [InlineData("1e", 2, JsonErrorReason.InvalidNumber)]
    [InlineData("1e+", 3, JsonErrorReason.InvalidNumber)]
    [InlineData(".5", 0, JsonErrorReason.UnexpectedByte)]
    [InlineData("+1", 0, JsonErrorReason.UnexpectedByte)]
    [InlineData("tru", 0, JsonErrorReason.InvalidLiteral)]
    [InlineData("nul", 0, JsonErrorReason.InvalidLiteral)]
    [InlineData("fals", 0, JsonErrorReason.InvalidLiteral)]
    [InlineData("truex", 4, JsonErrorReason.UnexpectedByte)]
    [InlineData("[1,]", 3, JsonErrorReason.UnexpectedByte)]
    [InlineData("[1 2]", 3, JsonErrorReason.UnexpectedByte)]
    [InlineData("[1}", 2, JsonErrorReason.UnexpectedByte)]
    [InlineData("{]", 1, JsonErrorReason.ExpectedPropertyName)]
    [InlineData("{\"a\":1]", 6, JsonErrorReason.UnexpectedByte)]
    [InlineData("{\"a\":1,}", 7, JsonErrorReason.ExpectedPropertyName)]
    [InlineData("{\"a\" 1}", 5, JsonErrorReason.ExpectedColon)]
    [InlineData("{\"a\"", 4, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("{1:2}", 1, JsonErrorReason.ExpectedPropertyName)]
    [InlineData("[", 1, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("[1,", 3, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("\"abc", 4, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("\"a\tb\"", 2, JsonErrorReason.ControlCharacterInString)]
    [InlineData("\"\\x\"", 1, JsonErrorReason.InvalidEscape)]
    [InlineData("\"\\u12G4\"", 1, JsonErrorReason.InvalidEscape)]
    [InlineData("\"\\u12", 5, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("\"\\", 2, JsonErrorReason.UnexpectedEndOfInput)]
    [InlineData("1 2", 2, JsonErrorReason.UnexpectedByte)]
    [InlineData("[\"\\q\"]", 2, JsonErrorReason.InvalidEscape)]
    [InlineData("{\"\\q\":1}", 2, JsonErrorReason.InvalidEscape)]
    [InlineData("{\"a\":\"\\q\"}", 6, JsonErrorReason.InvalidEscape)]
    [InlineData("[]]", 2, JsonErrorReason.UnexpectedByte)]
    [InlineData("\u00a0[]", 0, JsonErrorReason.UnexpectedByte)]
    public void Rejects_with_reason_and_offset(string json, int offset, JsonErrorReason reason)
    {
        Assert.Equal(new JsonParseError(offset, reason), Tokenize(json, out _));
    }

    [Fact]
    public void Rejects_invalid_utf8_inside_strings()
    {
        byte[] json = [(byte)'"', (byte)'a', 0xC3, (byte)'"'];
        Assert.Equal(new JsonParseError(1, JsonErrorReason.InvalidUtf8), Tokenize(json, out _));
    }

    [Fact]
    public void Rejects_utf8_byte_order_mark()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, (byte)'[', (byte)']'];
        Assert.Equal(new JsonParseError(0, JsonErrorReason.UnexpectedByte), Tokenize(json, out _));
    }

    [Fact]
    public void Emits_tokens_in_document_order()
    {
        Tokenize("{\"k\":[1,\"s\",true,false,null],\"e\":{}}", out List<(JsonTokenKind Kind, string Value, int Offset)> tokens);
        Assert.Equal(
            [
                (JsonTokenKind.StartObject, "", 0),
                (JsonTokenKind.PropertyName, "k", 1),
                (JsonTokenKind.StartArray, "", 5),
                (JsonTokenKind.Number, "1", 6),
                (JsonTokenKind.String, "s", 8),
                (JsonTokenKind.True, "", 12),
                (JsonTokenKind.False, "", 17),
                (JsonTokenKind.Null, "", 23),
                (JsonTokenKind.EndArray, "", 27),
                (JsonTokenKind.PropertyName, "e", 29),
                (JsonTokenKind.StartObject, "", 33),
                (JsonTokenKind.EndObject, "", 34),
                (JsonTokenKind.EndObject, "", 35),
            ],
            tokens);
    }

    [Fact]
    public void Read_after_end_or_error_keeps_returning_false()
    {
        var ok = new JsonTokenizer("1"u8);
        Assert.True(ok.Read());
        Assert.False(ok.Read());
        Assert.False(ok.Read());
        Assert.Equal(JsonTokenKind.None, ok.Kind);

        var bad = new JsonTokenizer("[x"u8);
        Assert.True(bad.Read());
        Assert.False(bad.Read());
        Assert.False(bad.Read());
        Assert.Equal(new JsonParseError(1, JsonErrorReason.UnexpectedByte), bad.Error);
    }

    [Fact]
    public void Depth_limit_is_exact()
    {
        Assert.Equal(default, Tokenize("[[{\"a\":[]}]]", out _, maxDepth: 4));
        Assert.Equal(new JsonParseError(7, JsonErrorReason.MaxDepthExceeded), Tokenize("[[{\"a\":[]}]]", out _, maxDepth: 3));
        Assert.Equal(new JsonParseError(5, JsonErrorReason.MaxDepthExceeded), Tokenize("{\"a\":{}}", out _, maxDepth: 1));
    }

    [Fact]
    public void Default_depth_limit_accepts_512_and_rejects_513()
    {
        Assert.Equal(default, Tokenize(new string('[', 512) + new string(']', 512), out _));
        Assert.Equal(new JsonParseError(512, JsonErrorReason.MaxDepthExceeded), Tokenize(new string('[', 513) + new string(']', 513), out _));
    }

    [Fact]
    public void Nesting_past_the_limit_fails_without_stack_overflow()
    {
        Assert.Equal(JsonErrorReason.MaxDepthExceeded, Tokenize(new string('[', 100_000), out _).Reason);
    }

    [Fact]
    public void Depth_bits_track_objects_and_arrays_across_word_boundaries()
    {
        // Alternate object/array nesting past 64 levels so the close for each level is checked against the right bit.
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            sb.Append(i % 2 == 0 ? "{\"k\":" : "[");
        }

        sb.Append('0');
        for (int i = 199; i >= 0; i--)
        {
            sb.Append(i % 2 == 0 ? '}' : ']');
        }

        Assert.Equal(default, Tokenize(sb.ToString(), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(JsonTokenizer.MaxSupportedDepth + 1)]
    public void Rejects_unsupported_max_depth(int maxDepth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JsonTokenizer("1"u8, maxDepth));
    }

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("-12.5e1", -125d)]
    [InlineData("1e400", double.PositiveInfinity)]
    public void TryGetDouble_parses_number_tokens(string json, double expected)
    {
        var t = new JsonTokenizer(Encoding.UTF8.GetBytes(json));
        Assert.True(t.Read());
        Assert.True(t.TryGetDouble(out double value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Typed_getters_refuse_other_token_kinds()
    {
        var t = new JsonTokenizer("\"1\""u8);
        Assert.True(t.Read());
        Assert.False(t.TryGetDouble(out _));
        Assert.False(t.TryGetInt64(out _));

        var n = new JsonTokenizer("1"u8);
        Assert.True(n.Read());
        Assert.Equal(OperationStatus.InvalidData, n.TryGetString(new char[8], out _, out _));
    }

    [Theory]
    [InlineData("9223372036854775807", true, long.MaxValue)]
    [InlineData("-9223372036854775808", true, long.MinValue)]
    [InlineData("9223372036854775808", false, 0L)]
    [InlineData("1.0", false, 0L)]
    public void TryGetInt64_accepts_only_in_range_integers(string json, bool ok, long expected)
    {
        var t = new JsonTokenizer(Encoding.UTF8.GetBytes(json));
        Assert.True(t.Read());
        Assert.Equal(ok, t.TryGetInt64(out long value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Exposes_raw_value_and_escape_flag()
    {
        var t = new JsonTokenizer("[\"a\\nb\",\"plain\"]"u8);
        Assert.True(t.Read());
        Assert.True(t.Read());
        Assert.True(t.ValueHasEscapes);
        Assert.Equal("a\\nb", Encoding.UTF8.GetString(t.ValueSpan));
        Assert.True(t.Read());
        Assert.False(t.ValueHasEscapes);
        Assert.Equal(1, t.CurrentDepth);
    }

    [Fact]
    public void Tokenizing_allocates_nothing()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"a\":[1,2.5e3,\"x\\u00e9\\n\",true,false,null],\"b\":{\"c\":\"é😀\"}}");
        CountTokens(json);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = CountTokens(json);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(16, count);
        Assert.Equal(0, after - before);
    }

    private static int CountTokens(byte[] json)
    {
        var t = new JsonTokenizer(json);
        int count = 0;
        while (t.Read())
        {
            count++;
        }

        return t.Error.IsError ? -1 : count;
    }

    internal static JsonParseError Tokenize(string json, out List<(JsonTokenKind Kind, string Value, int Offset)> tokens, int maxDepth = JsonTokenizer.MaxSupportedDepth)
        => Tokenize(Encoding.UTF8.GetBytes(json), out tokens, maxDepth);

    internal static JsonParseError Tokenize(byte[] json, out List<(JsonTokenKind Kind, string Value, int Offset)> tokens, int maxDepth = JsonTokenizer.MaxSupportedDepth)
    {
        tokens = [];
        var t = new JsonTokenizer(json, maxDepth);
        while (t.Read())
        {
            // Read returns false on the call that fails, so a true result always carries a real token.
            Assert.NotEqual(JsonTokenKind.None, t.Kind);
            Assert.Equal(t.Kind is JsonTokenKind.String or JsonTokenKind.PropertyName && t.ValueSpan.Contains((byte)'\\'), t.ValueHasEscapes);
            tokens.Add((t.Kind, Encoding.UTF8.GetString(t.ValueSpan), t.TokenOffset));
        }

        return t.Error;
    }
}
