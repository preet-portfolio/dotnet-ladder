using System.Buffers;
using System.Text.Unicode;

namespace Ladder.Json;

public static class JsonString
{
    /// <summary>
    /// Decodes a raw JSON string body (already validated by <see cref="JsonTokenizer"/>) into UTF-16.
    /// Unpaired surrogate escapes such as <c>"\uD800"</c> are rejected with <see cref="JsonErrorReason.InvalidSurrogate"/>.
    /// </summary>
    public static OperationStatus Unescape(ReadOnlySpan<byte> raw, Span<char> destination, out int charsWritten, out JsonErrorReason error)
    {
        charsWritten = 0;
        error = JsonErrorReason.None;
        while (true)
        {
            int backslash = raw.IndexOf((byte)'\\');
            ReadOnlySpan<byte> literal = backslash < 0 ? raw : raw[..backslash];
            OperationStatus status = Utf8.ToUtf16(literal, destination[charsWritten..], out _, out int written, replaceInvalidSequences: false);
            charsWritten += written;
            if (status != OperationStatus.Done)
            {
                error = status == OperationStatus.InvalidData ? JsonErrorReason.InvalidUtf8 : JsonErrorReason.None;
                return status;
            }

            if (backslash < 0)
            {
                return OperationStatus.Done;
            }

            raw = raw[backslash..];
            if (charsWritten >= destination.Length)
            {
                return OperationStatus.DestinationTooSmall;
            }

            char c;
            switch (raw[1])
            {
                case (byte)'b': c = '\b'; break;
                case (byte)'f': c = '\f'; break;
                case (byte)'n': c = '\n'; break;
                case (byte)'r': c = '\r'; break;
                case (byte)'t': c = '\t'; break;
                case (byte)'u':
                    c = (char)ParseHex4(raw.Slice(2, 4));
                    if (char.IsHighSurrogate(c))
                    {
                        if (raw.Length < 12 || raw[6] != (byte)'\\' || raw[7] != (byte)'u'
                            || !char.IsLowSurrogate((char)ParseHex4(raw.Slice(8, 4))))
                        {
                            error = JsonErrorReason.InvalidSurrogate;
                            return OperationStatus.InvalidData;
                        }

                        if (charsWritten + 2 > destination.Length)
                        {
                            return OperationStatus.DestinationTooSmall;
                        }

                        destination[charsWritten++] = c;
                        destination[charsWritten++] = (char)ParseHex4(raw.Slice(8, 4));
                        raw = raw[12..];
                        continue;
                    }

                    if (char.IsLowSurrogate(c))
                    {
                        error = JsonErrorReason.InvalidSurrogate;
                        return OperationStatus.InvalidData;
                    }

                    destination[charsWritten++] = c;
                    raw = raw[6..];
                    continue;
                default:
                    // '"', '\\' and '/' stand for themselves.
                    c = (char)raw[1];
                    break;
            }

            destination[charsWritten++] = c;
            raw = raw[2..];
        }
    }

    private static int ParseHex4(ReadOnlySpan<byte> hex)
    {
        int value = 0;
        foreach (byte b in hex)
        {
            value = (value << 4) | HexValue(b);
        }

        return value;
    }

    private static int HexValue(byte b) => b switch
    {
        <= (byte)'9' => b - '0',
        <= (byte)'F' => b - 'A' + 10,
        _ => b - 'a' + 10,
    };
}
