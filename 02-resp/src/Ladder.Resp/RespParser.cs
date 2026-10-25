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

    private static ParseStatus TryReadMultiBulk(ref SequenceReader<byte> reader, CommandArgs args, out string? error)
    {
        error = null;
        reader.Advance(1);
        switch (TryReadLength(ref reader, out long count))
        {
            case LineStatus.Incomplete:
                return ParseStatus.Incomplete;
            case LineStatus.Invalid:
                error = "Protocol error: invalid multibulk length";
                return ParseStatus.ProtocolError;
        }

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
            if (!reader.TryRead(out byte marker))
            {
                return ParseStatus.Incomplete;
            }

            if (marker != (byte)'$')
            {
                error = $"Protocol error: expected '$', got '{(char)marker}'";
                return ParseStatus.ProtocolError;
            }

            switch (TryReadLength(ref reader, out long length))
            {
                case LineStatus.Incomplete:
                    return ParseStatus.Incomplete;
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
                error = "Protocol error: invalid bulk length";
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
        Invalid,
    }

    /// <summary>Reads a decimal length terminated by \r\n without allocating, even when it spans segments.</summary>
    private static LineStatus TryReadLength(ref SequenceReader<byte> reader, out long value)
    {
        value = 0;
        bool negative = false;
        int digits = 0;
        while (true)
        {
            if (!reader.TryRead(out byte b))
            {
                return LineStatus.Incomplete;
            }

            if (b == (byte)'\r')
            {
                if (!reader.TryRead(out byte lf))
                {
                    return LineStatus.Incomplete;
                }

                if (lf != (byte)'\n' || digits == 0)
                {
                    return LineStatus.Invalid;
                }

                value = negative ? -value : value;
                return LineStatus.Ok;
            }

            if (b == (byte)'-' && digits == 0 && !negative)
            {
                negative = true;
                continue;
            }

            if ((uint)(b - '0') > 9 || digits >= 18)
            {
                return LineStatus.Invalid;
            }

            value = (value * 10) + (b - '0');
            digits++;
        }
    }
}
