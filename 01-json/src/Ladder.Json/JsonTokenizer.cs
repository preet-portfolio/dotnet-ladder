using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Ladder.Json;

/// <summary>
/// Forward-only RFC 8259 tokenizer over a complete UTF-8 document. It never allocates and never
/// throws on malformed input: <see cref="Read"/> returns false and <see cref="Error"/> says why.
/// </summary>
public ref struct JsonTokenizer
{
    public const int MaxSupportedDepth = DepthBits.Capacity;

    // A string ends at the closing quote; a backslash or a raw control character (RFC 8259 §7) needs attention.
    private static readonly SearchValues<byte> s_stringStops = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"u8);

    private readonly ReadOnlySpan<byte> _json;
    private readonly int _maxDepth;
    private int _pos;
    private int _depth;
    private DepthBits _isObject;
    private Expect _expect;

    public JsonTokenizer(ReadOnlySpan<byte> utf8Json, int maxDepth = MaxSupportedDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, MaxSupportedDepth);
        _json = utf8Json;
        _maxDepth = maxDepth;
        _isObject = default;
        _expect = Expect.Value;
    }

    public JsonTokenKind Kind { get; private set; }

    /// <summary>Byte offset of the current token's first byte (the opening quote for strings).</summary>
    public int TokenOffset { get; private set; }

    /// <summary>Raw bytes of a string (without quotes, still escaped) or number token.</summary>
    public ReadOnlySpan<byte> ValueSpan { get; private set; }

    public bool ValueHasEscapes { get; private set; }

    public JsonParseError Error { get; private set; }

    public readonly int CurrentDepth => _depth;

    /// <summary>Advances to the next token. False means either a clean end of input or an error.</summary>
    public bool Read()
    {
        if (_expect == Expect.Done)
        {
            return false;
        }

        ValueSpan = default;
        ValueHasEscapes = false;
        SkipWhitespace();
        if (_pos >= _json.Length)
        {
            if (_expect == Expect.EndOfInput)
            {
                _expect = Expect.Done;
                Kind = JsonTokenKind.None;
                return false;
            }

            return Fail(JsonErrorReason.UnexpectedEndOfInput, _pos);
        }

        byte b = _json[_pos];
        switch (_expect)
        {
            case Expect.Value:
                return ReadValue(b);
            case Expect.FirstArrayItem:
                return b == (byte)']' ? EndContainer(JsonTokenKind.EndArray) : ReadValue(b);
            case Expect.FirstObjectMember:
                return b == (byte)'}' ? EndContainer(JsonTokenKind.EndObject) : ReadPropertyName(b);
            case Expect.PropertyName:
                return ReadPropertyName(b);
            case Expect.CommaOrClose:
                return ReadCommaOrClose(b);
            default:
                // Expect.EndOfInput with bytes left over.
                return Fail(JsonErrorReason.UnexpectedByte, _pos);
        }
    }

    public readonly bool TryGetDouble(out double value)
    {
        if (Kind != JsonTokenKind.Number)
        {
            value = 0;
            return false;
        }

        return double.TryParse(ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public readonly bool TryGetInt64(out long value)
    {
        if (Kind != JsonTokenKind.Number)
        {
            value = 0;
            return false;
        }

        return long.TryParse(ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Unescapes the current string or property name into <paramref name="destination"/>.
    /// A buffer of <c>ValueSpan.Length</c> chars is always large enough.
    /// </summary>
    public readonly OperationStatus TryGetString(Span<char> destination, out int charsWritten, out JsonErrorReason error)
    {
        if (Kind is not (JsonTokenKind.String or JsonTokenKind.PropertyName))
        {
            charsWritten = 0;
            error = JsonErrorReason.None;
            return OperationStatus.InvalidData;
        }

        return JsonString.Unescape(ValueSpan, destination, out charsWritten, out error);
    }

    private bool ReadValue(byte b)
    {
        TokenOffset = _pos;
        switch (b)
        {
            case (byte)'{':
                if (!Push(isObject: true))
                {
                    return false;
                }

                _pos++;
                Kind = JsonTokenKind.StartObject;
                _expect = Expect.FirstObjectMember;
                return true;
            case (byte)'[':
                if (!Push(isObject: false))
                {
                    return false;
                }

                _pos++;
                Kind = JsonTokenKind.StartArray;
                _expect = Expect.FirstArrayItem;
                return true;
            case (byte)'"':
                if (!ReadString())
                {
                    return false;
                }

                Kind = JsonTokenKind.String;
                return ValueCompleted();
            case (byte)'t':
                return ReadLiteral("true"u8, JsonTokenKind.True);
            case (byte)'f':
                return ReadLiteral("false"u8, JsonTokenKind.False);
            case (byte)'n':
                return ReadLiteral("null"u8, JsonTokenKind.Null);
            default:
                if (b == (byte)'-' || IsDigit(b))
                {
                    return ReadNumber();
                }

                return Fail(JsonErrorReason.UnexpectedByte, _pos);
        }
    }

    private bool ReadPropertyName(byte b)
    {
        if (b != (byte)'"')
        {
            return Fail(JsonErrorReason.ExpectedPropertyName, _pos);
        }

        TokenOffset = _pos;
        if (!ReadString())
        {
            return false;
        }

        SkipWhitespace();
        if (_pos >= _json.Length)
        {
            return Fail(JsonErrorReason.UnexpectedEndOfInput, _pos);
        }

        if (_json[_pos] != (byte)':')
        {
            return Fail(JsonErrorReason.ExpectedColon, _pos);
        }

        _pos++;
        Kind = JsonTokenKind.PropertyName;
        _expect = Expect.Value;
        return true;
    }

    private bool ReadCommaOrClose(byte b)
    {
        bool inObject = _isObject[_depth - 1];
        if (b == (byte)',')
        {
            _pos++;
            SkipWhitespace();
            if (_pos >= _json.Length)
            {
                return Fail(JsonErrorReason.UnexpectedEndOfInput, _pos);
            }

            b = _json[_pos];
            return inObject ? ReadPropertyName(b) : ReadValue(b);
        }

        if (inObject && b == (byte)'}')
        {
            return EndContainer(JsonTokenKind.EndObject);
        }

        if (!inObject && b == (byte)']')
        {
            return EndContainer(JsonTokenKind.EndArray);
        }

        return Fail(JsonErrorReason.UnexpectedByte, _pos);
    }

    /// <summary>Scans a string starting at the opening quote. Sets <see cref="ValueSpan"/> and advances past the closing quote.</summary>
    private bool ReadString()
    {
        int contentStart = _pos + 1;
        int scan = contentStart;
        bool escaped = false;
        while (true)
        {
            int idx = _json.Slice(scan).IndexOfAny(s_stringStops);
            if (idx < 0)
            {
                return Fail(JsonErrorReason.UnexpectedEndOfInput, _json.Length);
            }

            int at = scan + idx;
            byte c = _json[at];
            if (c == (byte)'"')
            {
                ReadOnlySpan<byte> content = _json[contentStart..at];
                // Escapes are ASCII, so validating the raw bytes validates every literal segment between them.
                if (!Utf8.IsValid(content))
                {
                    return Fail(JsonErrorReason.InvalidUtf8, contentStart);
                }

                ValueSpan = content;
                ValueHasEscapes = escaped;
                _pos = at + 1;
                return true;
            }

            if (c != (byte)'\\')
            {
                return Fail(JsonErrorReason.ControlCharacterInString, at);
            }

            escaped = true;
            int escapeLength = EscapeLength(at);
            if (escapeLength <= 0)
            {
                return false;
            }

            scan = at + escapeLength;
        }
    }

    /// <summary>Length of the escape sequence at <paramref name="at"/> (the backslash), or 0 after recording an error.</summary>
    private int EscapeLength(int at)
    {
        if (at + 1 >= _json.Length)
        {
            Fail(JsonErrorReason.UnexpectedEndOfInput, _json.Length);
            return 0;
        }

        switch (_json[at + 1])
        {
            case (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t':
                return 2;
            case (byte)'u':
                for (int i = at + 2; i < at + 6; i++)
                {
                    if (i >= _json.Length)
                    {
                        Fail(JsonErrorReason.UnexpectedEndOfInput, _json.Length);
                        return 0;
                    }

                    if (!char.IsAsciiHexDigit((char)_json[i]))
                    {
                        Fail(JsonErrorReason.InvalidEscape, at);
                        return 0;
                    }
                }

                return 6;
            default:
                Fail(JsonErrorReason.InvalidEscape, at);
                return 0;
        }
    }

    // number = [ minus ] int [ frac ] [ exp ]   (RFC 8259 §6)
    private bool ReadNumber()
    {
        ReadOnlySpan<byte> json = _json;
        int start = _pos;
        int i = _pos;
        if (json[i] == (byte)'-')
        {
            i++;
        }

        if (i >= json.Length)
        {
            return Fail(JsonErrorReason.InvalidNumber, i);
        }

        if (json[i] == (byte)'0')
        {
            i++;
        }
        else if (IsDigit(json[i]))
        {
            i = SkipDigits(json, i + 1);
        }
        else
        {
            return Fail(JsonErrorReason.InvalidNumber, i);
        }

        if (i < json.Length && json[i] == (byte)'.')
        {
            i++;
            if (i >= json.Length || !IsDigit(json[i]))
            {
                return Fail(JsonErrorReason.InvalidNumber, i);
            }

            i = SkipDigits(json, i + 1);
        }

        if (i < json.Length && (json[i] | 0x20) == (byte)'e')
        {
            i++;
            if (i < json.Length && (json[i] == (byte)'+' || json[i] == (byte)'-'))
            {
                i++;
            }

            if (i >= json.Length || !IsDigit(json[i]))
            {
                return Fail(JsonErrorReason.InvalidNumber, i);
            }

            i = SkipDigits(json, i + 1);
        }

        // Whatever follows ("01", "1x", "1.") is caught by the next Read as an unexpected byte.
        ValueSpan = json[start..i];
        _pos = i;
        Kind = JsonTokenKind.Number;
        return ValueCompleted();
    }

    private bool ReadLiteral(ReadOnlySpan<byte> literal, JsonTokenKind kind)
    {
        if (!_json.Slice(_pos).StartsWith(literal))
        {
            return Fail(JsonErrorReason.InvalidLiteral, _pos);
        }

        _pos += literal.Length;
        Kind = kind;
        return ValueCompleted();
    }

    private bool Push(bool isObject)
    {
        if (_depth >= _maxDepth)
        {
            return Fail(JsonErrorReason.MaxDepthExceeded, _pos);
        }

        _isObject[_depth] = isObject;
        _depth++;
        return true;
    }

    private bool EndContainer(JsonTokenKind kind)
    {
        TokenOffset = _pos;
        _pos++;
        _depth--;
        Kind = kind;
        return ValueCompleted();
    }

    private bool ValueCompleted()
    {
        _expect = _depth == 0 ? Expect.EndOfInput : Expect.CommaOrClose;
        return true;
    }

    private bool Fail(JsonErrorReason reason, int offset)
    {
        Error = new JsonParseError(offset, reason);
        Kind = JsonTokenKind.None;
        ValueSpan = default;
        _expect = Expect.Done;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        ReadOnlySpan<byte> json = _json;
        int i = _pos;
        while (i < json.Length)
        {
            byte b = json[i];
            if (b != (byte)' ' && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t')
            {
                break;
            }

            i++;
        }

        _pos = i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    private static int SkipDigits(ReadOnlySpan<byte> json, int i)
    {
        while (i < json.Length && IsDigit(json[i]))
        {
            i++;
        }

        return i;
    }

    private enum Expect : byte
    {
        Value,
        FirstArrayItem,
        FirstObjectMember,
        PropertyName,
        CommaOrClose,
        EndOfInput,
        Done,
    }
}
