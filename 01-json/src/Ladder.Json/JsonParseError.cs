namespace Ladder.Json;

public enum JsonErrorReason : byte
{
    None,
    UnexpectedEndOfInput,
    UnexpectedByte,
    ExpectedPropertyName,
    ExpectedColon,
    InvalidLiteral,
    InvalidNumber,
    InvalidEscape,
    ControlCharacterInString,
    InvalidUtf8,
    InvalidSurrogate,
    MaxDepthExceeded,
}

/// <summary>Where and why parsing stopped. <see cref="Offset"/> is a byte offset into the input.</summary>
public readonly record struct JsonParseError(int Offset, JsonErrorReason Reason)
{
    public bool IsError => Reason != JsonErrorReason.None;

    public override string ToString() => IsError ? $"{Reason} at byte {Offset}" : "None";
}
