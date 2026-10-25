namespace Ladder.Json;

/// <summary>A minimal tree, built from <see cref="JsonTokenizer"/> tokens. It exists to compare against other parsers, not for speed.</summary>
public abstract class JsonValue
{
    private protected JsonValue()
    {
    }

    public abstract JsonTokenKind Kind { get; }
}

public sealed class JsonLiteral : JsonValue
{
    public static readonly JsonLiteral True = new(JsonTokenKind.True);
    public static readonly JsonLiteral False = new(JsonTokenKind.False);
    public static readonly JsonLiteral Null = new(JsonTokenKind.Null);

    private JsonLiteral(JsonTokenKind kind) => Kind = kind;

    public override JsonTokenKind Kind { get; }
}

/// <summary>Keeps the number's source text so no precision is lost before comparison.</summary>
public sealed class JsonNumber(string raw) : JsonValue
{
    public string Raw { get; } = raw;

    public override JsonTokenKind Kind => JsonTokenKind.Number;
}

public sealed class JsonStringValue(string value) : JsonValue
{
    public string Value { get; } = value;

    public override JsonTokenKind Kind => JsonTokenKind.String;
}

public sealed class JsonArray : JsonValue
{
    public List<JsonValue> Items { get; } = [];

    public override JsonTokenKind Kind => JsonTokenKind.StartArray;
}

/// <summary>Members in document order; duplicate names are kept, since RFC 8259 §4 leaves them to the reader.</summary>
public sealed class JsonObject : JsonValue
{
    public List<KeyValuePair<string, JsonValue>> Members { get; } = [];

    public override JsonTokenKind Kind => JsonTokenKind.StartObject;
}
