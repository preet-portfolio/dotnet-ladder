using System.Buffers;
using System.Text;

namespace Ladder.Resp;

/// <summary>Writes RESP2 replies straight into the connection's output pipe.</summary>
public static class RespWriter
{
    public static void Ok(IBufferWriter<byte> w) => Raw(w, "+OK\r\n"u8);

    public static void NullBulk(IBufferWriter<byte> w) => Raw(w, "$-1\r\n"u8);

    public static void NullArray(IBufferWriter<byte> w) => Raw(w, "*-1\r\n"u8);

    public static void Simple(IBufferWriter<byte> w, ReadOnlySpan<byte> text)
    {
        Raw(w, "+"u8);
        Raw(w, text);
        Raw(w, "\r\n"u8);
    }

    /// <summary>Writes a pre-encoded error line such as <c>"-ERR syntax error\r\n"u8</c>.</summary>
    public static void Error(IBufferWriter<byte> w, ReadOnlySpan<byte> line) => Raw(w, line);

    /// <summary>Writes a computed error message; <paramref name="message"/> must not contain \r or \n.</summary>
    public static void Error(IBufferWriter<byte> w, string message)
    {
        Raw(w, "-"u8);
        Span<byte> dest = w.GetSpan(Encoding.UTF8.GetMaxByteCount(message.Length));
        int n = Encoding.UTF8.GetBytes(message, dest);
        w.Advance(n);
        Raw(w, "\r\n"u8);
    }

    public static void Integer(IBufferWriter<byte> w, long value)
    {
        Span<byte> span = w.GetSpan(24);
        span[0] = (byte)':';
        value.TryFormat(span[1..], out int n, provider: System.Globalization.CultureInfo.InvariantCulture);
        span[n + 1] = (byte)'\r';
        span[n + 2] = (byte)'\n';
        w.Advance(n + 3);
    }

    public static void ArrayHeader(IBufferWriter<byte> w, long count) => Header(w, (byte)'*', count);

    public static void Bulk(IBufferWriter<byte> w, ReadOnlySpan<byte> value)
    {
        Header(w, (byte)'$', value.Length);
        Raw(w, value);
        Raw(w, "\r\n"u8);
    }

    public static void Bulk(IBufferWriter<byte> w, string value) => Bulk(w, Encoding.UTF8.GetBytes(value));

    /// <summary>Writes an integer as a bulk string (how int-encoded strings are returned by GET).</summary>
    public static void BulkInteger(IBufferWriter<byte> w, long value)
    {
        Span<byte> digits = stackalloc byte[20];
        value.TryFormat(digits, out int n, provider: System.Globalization.CultureInfo.InvariantCulture);
        Bulk(w, digits[..n]);
    }

    private static void Header(IBufferWriter<byte> w, byte marker, long count)
    {
        Span<byte> span = w.GetSpan(24);
        span[0] = marker;
        count.TryFormat(span[1..], out int n, provider: System.Globalization.CultureInfo.InvariantCulture);
        span[n + 1] = (byte)'\r';
        span[n + 2] = (byte)'\n';
        w.Advance(n + 3);
    }

    public static void Raw(IBufferWriter<byte> w, ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            Span<byte> span = w.GetSpan(Math.Min(bytes.Length, 64 * 1024));
            int n = Math.Min(span.Length, bytes.Length);
            bytes[..n].CopyTo(span);
            w.Advance(n);
            bytes = bytes[n..];
        }
    }
}
