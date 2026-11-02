using System.Buffers;

namespace Ladder.Resp;

public enum ParseStatus : byte
{
    Complete,
    Incomplete,
    Empty,
    ProtocolError,
}

/// <summary>
/// Reads one client request: a RESP array of bulk strings (<c>*2\r\n$3\r\nGET\r\n$1\r\nk\r\n</c>) or an inline
/// command (<c>PING\r\n</c>). Requests may span any number of buffer segments.
/// </summary>
public static class RespParser
{
    public const long MaxBulkLength = 512L * 1024 * 1024;
    public const int MaxInlineLength = 64 * 1024;
    private const int MaxArgs = 1024 * 1024;

    /// <summary>
    /// Parses one request starting at <paramref name="reader"/>. On <see cref="ParseStatus.Incomplete"/> the reader
    /// position is meaningless and the caller must wait for more data and retry from the request's start.
    /// </summary>
    public static ParseStatus TryRead(ref SequenceReader<byte> reader, CommandArgs args, out string? error)
    {
        error = null;
        args.Reset();
        if (!reader.TryPeek(out byte first))
        {
            return ParseStatus.Incomplete;
        }

        return first == (byte)'*' ? TryReadMultiBulk(ref reader, args, out error) : TryReadInline(ref reader, args, out error);
    }

    // Follows processMultibulkBuffer in Valkey's networking.c (9.1.2), including the order of its checks, because
    // that order decides which error a malformed request gets.
    private static ParseStatus TryReadMultiBulk(ref SequenceReader<byte> reader, CommandArgs args, out string? error)
    {
        error = null;
        switch (TryReadLengthLine(ref reader, (byte)'*', out long count))
        {
            case LineStatus.Incomplete:
                return ParseStatus.Incomplete;
            case LineStatus.TooBig:
                error = "Protocol error: too big mbulk count string";
                return ParseStatus.ProtocolError;
            case LineStatus.InvalidCrlf:
                error = "Protocol error: invalid CRLF in request";
                return ParseStatus.ProtocolError;
            case LineStatus.Invalid:
                error = "Protocol error: invalid multibulk length";
                return ParseStatus.ProtocolError;
        }

        // Valkey accepts up to INT_MAX; we refuse above 1M arguments rather than preallocate for them.
        if (count > MaxArgs)
        {
            error = "Protocol error: invalid multibulk length";
            return ParseStatus.ProtocolError;
        }

        if (count <= 0)
        {
            return ParseStatus.Empty;
        }

        for (long i = 0; i < count; i++)
        {
            switch (TryReadLengthLine(ref reader, (byte)'$', out long length))
            {
                case LineStatus.Incomplete:
                    return ParseStatus.Incomplete;
                case LineStatus.TooBig:
                    error = "Protocol error: too big bulk count string";
                    return ParseStatus.ProtocolError;
                case LineStatus.WrongMarker:
                    reader.TryPeek(out byte got);
                    error = $"Protocol error: expected '$', got '{(char)got}'";
                    return ParseStatus.ProtocolError;
                case LineStatus.InvalidCrlf:
                    error = "Protocol error: invalid CRLF in request";
                    return ParseStatus.ProtocolError;
                case LineStatus.Invalid:
                    error = "Protocol error: invalid bulk length";
                    return ParseStatus.ProtocolError;
            }

            if (length < 0 || length > MaxBulkLength)
            {
                error = "Protocol error: invalid bulk length";
                return ParseStatus.ProtocolError;
            }

            if (reader.Remaining < length + 2)
            {
                return ParseStatus.Incomplete;
            }

            args.Add(reader.Sequence.Slice(reader.Position, length));
            reader.Advance(length);
            if (!reader.IsNext("\r\n"u8, advancePast: true))
            {
                error = "Protocol error: invalid CRLF in request";
                return ParseStatus.ProtocolError;
            }
        }

        return ParseStatus.Complete;
    }

    // Inline commands are space-separated words ending in \n (optionally \r\n), as typed into telnet.
    private static ParseStatus TryReadInline(ref SequenceReader<byte> reader, CommandArgs args, out string? error)
    {
        error = null;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
        {
            if (reader.Remaining > MaxInlineLength)
            {
                error = "Protocol error: too big inline request";
                return ParseStatus.ProtocolError;
            }

            return ParseStatus.Incomplete;
        }

        var words = new SequenceReader<byte>(line);
        while (!words.End)
        {
            words.AdvancePastAny((byte)' ', (byte)'\t', (byte)'\r');
            if (words.End)
            {
                break;
            }

            SequencePosition start = words.Position;
            if (!words.TryAdvanceToAny(" \t\r"u8, advancePastDelimiter: false))
            {
                words.AdvanceToEnd();
            }

            args.Add(line.Slice(start, words.Position));
        }

        return args.Count == 0 ? ParseStatus.Empty : ParseStatus.Complete;
    }

    private enum LineStatus : byte
    {
        Ok,
        Incomplete,
        TooBig,
        WrongMarker,
        InvalidCrlf,
        Invalid,
    }

    /// <summary>
    /// Reads a "*count\r\n" or "$length\r\n" line, with the reader at the marker. Valkey's checks, in its order:
    /// <list type="number">
    /// <item>no '\r' yet is Incomplete, or TooBig once more than 64 KB are waiting;</item>
    /// <item>the byte after '\r' must have arrived;</item>
    /// <item>the marker;</item>
    /// <item>that byte must be '\n';</item>
    /// <item>the number, with string2ll's rules.</item>
    /// </list>
    /// On WrongMarker the reader is left at the marker. Never allocates, even when the line spans segments.
    /// </summary>
    private static LineStatus TryReadLengthLine(ref SequenceReader<byte> reader, byte marker, out long value)
    {
        value = 0;
        SequenceReader<byte> start = reader;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\r', advancePastDelimiter: true))
        {
            return reader.Remaining > MaxInlineLength ? LineStatus.TooBig : LineStatus.Incomplete;
        }

        if (!reader.TryRead(out byte lf))
        {
            return LineStatus.Incomplete;
        }

        // The line can be empty (the '\r' is where the marker should be), and its first segment can be empty too.
        if (!start.TryPeek(out byte first) || line.IsEmpty || first != marker)
        {
            reader = start;
            return LineStatus.WrongMarker;
        }

        if (lf != (byte)'\n')
        {
            return LineStatus.InvalidCrlf;
        }

        return TryParseLong(line.Slice(1), out value) ? LineStatus.Ok : LineStatus.Invalid;
    }

    /// <summary>
    /// Valkey's string2ll: an optional '-', then "0" alone or a digit 1-9 followed by digits, at most 20 bytes, and in
    /// range for a 64-bit integer. So "", "-", "01", "-0", "+1" and " 1" are all invalid.
    /// </summary>
    private static bool TryParseLong(ReadOnlySequence<byte> text, out long value)
    {
        value = 0;
        if (text.IsEmpty || text.Length >= 21)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[20];
        text.CopyTo(buffer);
        ReadOnlySpan<byte> s = buffer[..(int)text.Length];
        if (s.Length == 1 && s[0] - '0' is >= 0 and <= 9)
        {
            value = s[0] - '0';
            return true;
        }

        bool negative = s[0] == (byte)'-';
        if (negative)
        {
            s = s[1..];
        }

        if (s.IsEmpty || s[0] - '1' is < 0 or > 8)
        {
            return false;
        }

        ulong v = 0;
        foreach (byte b in s)
        {
            uint digit = (uint)(b - '0');
            if (digit > 9 || v > (ulong.MaxValue - digit) / 10)
            {
                return false;
            }

            v = (v * 10) + digit;
        }

        if (negative ? v > (ulong)long.MaxValue + 1 : v > long.MaxValue)
        {
            return false;
        }

        value = negative ? (long)(0 - v) : (long)v;
        return true;
    }
}
