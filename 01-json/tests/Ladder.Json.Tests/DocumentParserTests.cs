using System.Text;

namespace Ladder.Json.Tests;

public class DocumentParserTests
{
    [Fact]
    public void Builds_tree_with_duplicate_keys_in_order()
    {
        Assert.True(JsonDocumentParser.TryParse("{\"a\":1,\"b\":[\"x\",null],\"a\":true}"u8, out JsonValue? value, out _));
        var obj = Assert.IsType<JsonObject>(value);
        Assert.Equal(["a", "b", "a"], obj.Members.Select(m => m.Key));
        Assert.Equal("1", Assert.IsType<JsonNumber>(obj.Members[0].Value).Raw);
        var arr = Assert.IsType<JsonArray>(obj.Members[1].Value);
        Assert.Equal("x", Assert.IsType<JsonStringValue>(arr.Items[0]).Value);
        Assert.Same(JsonLiteral.Null, arr.Items[1]);
        Assert.Same(JsonLiteral.True, obj.Members[2].Value);
    }

    [Theory]
    [InlineData("1.50", JsonTokenKind.Number)]
    [InlineData("\"s\"", JsonTokenKind.String)]
    [InlineData("false", JsonTokenKind.False)]
    [InlineData("[]", JsonTokenKind.StartArray)]
    [InlineData("{}", JsonTokenKind.StartObject)]
    public void Scalar_and_empty_roots(string json, JsonTokenKind kind)
    {
        Assert.True(JsonDocumentParser.TryParse(Encoding.UTF8.GetBytes(json), out JsonValue? value, out _));
        Assert.Equal(kind, value!.Kind);
    }

    [Theory]
    [InlineData("[\"\\uD800\"]", 1)]
    [InlineData("{\"\\uDC00\":1}", 1)]
    public void Rejects_lone_surrogates_at_the_string_token(string json, int offset)
    {
        Assert.False(JsonDocumentParser.TryParse(Encoding.UTF8.GetBytes(json), out JsonValue? value, out JsonParseError error));
        Assert.Null(value);
        Assert.Equal(new JsonParseError(offset, JsonErrorReason.InvalidSurrogate), error);
    }

    [Fact]
    public void Reports_tokenizer_errors_and_returns_no_tree()
    {
        Assert.False(JsonDocumentParser.TryParse("[1,2"u8, out JsonValue? value, out JsonParseError error));
        Assert.Null(value);
        Assert.Equal(new JsonParseError(4, JsonErrorReason.UnexpectedEndOfInput), error);
    }

    [Fact]
    public void Decodes_long_strings_after_short_ones()
    {
        string longText = new('é', 500);
        Assert.True(JsonDocumentParser.TryParse(Encoding.UTF8.GetBytes($"[\"a\",\"{longText}\",\"b\"]"), out JsonValue? value, out _));
        Assert.Equal(["a", longText, "b"], Assert.IsType<JsonArray>(value).Items.Select(i => ((JsonStringValue)i).Value));
    }

    [Fact]
    public void Error_ToString_names_reason_and_offset()
    {
        Assert.Equal("InvalidNumber at byte 3", new JsonParseError(3, JsonErrorReason.InvalidNumber).ToString());
        Assert.Equal("None", default(JsonParseError).ToString());
    }
}
