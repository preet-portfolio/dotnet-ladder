namespace Ladder.Json;

// Same member names as System.Text.Json.JsonTokenType, so the two read side by side.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Mirrors JsonTokenType.")]
public enum JsonTokenKind : byte
{
    None,
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    PropertyName,
    String,
    Number,
    True,
    False,
    Null,
}
