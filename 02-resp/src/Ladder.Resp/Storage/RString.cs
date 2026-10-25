using System.Buffers;
using System.Globalization;

namespace Ladder.Resp.Storage;

public enum StringEncoding : byte
{
    Int,
    Embstr,
    Raw,
}

/// <summary>
/// A string value. Integers are kept as <see cref="long"/> (Valkey's "int" encoding) so INCR can update in place
/// without allocating; other values keep their bytes. The encoding names follow Valkey's OBJECT ENCODING output.
/// </summary>
public sealed class RString
{
    // Valkey's OBJ_ENCODING_EMBSTR_SIZE_LIMIT.
    private const int EmbstrLimit = 44;

    private long _int;
    private byte[] _bytes = [];
    private int _length;

    private RString()
    {
    }

    public StringEncoding Encoding { get; private set; }

    public int Length => Encoding == StringEncoding.Int ? DigitCount(_int) : _length;

    /// <summary>Stores a value the way SET does: integers as int, short strings as embstr, others raw.</summary>
    public static RString FromValue(ReadOnlySpan<byte> value)
    {
        if (value.Length <= 20 && TryParseStrict(value, out long n))
        {
            return FromInt(n);
        }

        var s = new RString { _bytes = value.ToArray(), _length = value.Length };
        s.Encoding = value.Length <= EmbstrLimit ? StringEncoding.Embstr : StringEncoding.Raw;
        return s;
    }

    /// <summary>Stores bytes without trying the int encoding (APPEND, SETRANGE and friends produce raw strings).</summary>
    public static RString FromRaw(ReadOnlySpan<byte> value) => new() { _bytes = value.ToArray(), _length = value.Length, Encoding = StringEncoding.Raw };

    public static RString FromInt(long value) => new() { _int = value, Encoding = StringEncoding.Int };

    /// <summary>Creates a string the way Valkey's createStringObject does: embstr when short, raw otherwise.</summary>
    public static RString FromComputed(ReadOnlySpan<byte> value)
    {
        var s = new RString { _bytes = value.ToArray(), _length = value.Length };
        s.Encoding = value.Length <= EmbstrLimit ? StringEncoding.Embstr : StringEncoding.Raw;
        return s;
    }

    public bool TryGetLong(out long value)
    {
        if (Encoding == StringEncoding.Int)
        {
            value = _int;
            return true;
        }

        return TryParseStrict(_bytes.AsSpan(0, _length), out value);
    }

    /// <summary>Replaces the value with an integer in place; returns false when the caller must create a new object.</summary>
    public bool TrySetIntInPlace(long value)
    {
        if (Encoding != StringEncoding.Int)
        {
            return false;
        }

        _int = value;
        return true;
    }

    /// <summary>The value's bytes. Int-encoded values are formatted into <paramref name="scratch"/> (at least 20 bytes).</summary>
    public ReadOnlySpan<byte> GetBytes(Span<byte> scratch)
    {
        if (Encoding == StringEncoding.Int)
        {
            _int.TryFormat(scratch, out int n, provider: CultureInfo.InvariantCulture);
            return scratch[..n];
        }

        return _bytes.AsSpan(0, _length);
    }

    public byte[] ToArray() => GetBytes(stackalloc byte[20]).ToArray();

    public void WriteBulk(IBufferWriter<byte> w)
    {
        if (Encoding == StringEncoding.Int)
        {
            RespWriter.BulkInteger(w, _int);
        }
        else
        {
            RespWriter.Bulk(w, _bytes.AsSpan(0, _length));
        }
    }

    /// <summary>Returns a writable raw buffer of at least <paramref name="length"/> bytes, zero-filling any growth (APPEND, SETRANGE, SETBIT).</summary>
    public Span<byte> MakeRawWritable(int length)
    {
        if (Encoding == StringEncoding.Int)
        {
            Span<byte> digits = stackalloc byte[20];
            _int.TryFormat(digits, out int n, provider: CultureInfo.InvariantCulture);
            _bytes = digits[..n].ToArray();
            _length = n;
        }

        Encoding = StringEncoding.Raw;
        if (length > _bytes.Length)
        {
            var grown = new byte[Math.Max(length, Math.Min(_bytes.Length * 2, length + (1024 * 1024)))];
            _bytes.AsSpan(0, _length).CopyTo(grown);
            _bytes = grown;
        }

        if (length > _length)
        {
            _bytes.AsSpan(_length, length - _length).Clear();
            _length = length;
        }

        return _bytes.AsSpan(0, _length);
    }

    /// <summary>Valkey's string2ll: optional '-', no leading zeros, no '+', no spaces, fits in a long.</summary>
    public static bool TryParseStrict(ReadOnlySpan<byte> s, out long value)
    {
        value = 0;
        if (s.IsEmpty || s.Length > 20)
        {
            return false;
        }

        if (s.Length == 1 && s[0] == (byte)'0')
        {
            return true;
        }

        bool negative = s[0] == (byte)'-';
        ReadOnlySpan<byte> digits = negative ? s[1..] : s;
        if (digits.IsEmpty || digits[0] is < (byte)'1' or > (byte)'9')
        {
            return false;
        }

        ulong v = 0;
        foreach (byte b in digits)
        {
            if ((uint)(b - '0') > 9)
            {
                return false;
            }

            if (v > (ulong.MaxValue - 9) / 10)
            {
                return false;
            }

            v = (v * 10) + (ulong)(b - '0');
        }

        if (negative)
        {
            if (v > (ulong)long.MaxValue + 1)
            {
                return false;
            }

            value = (long)(0 - v);
            return true;
        }

        if (v > long.MaxValue)
        {
            return false;
        }

        value = (long)v;
        return true;
    }

    private static int DigitCount(long v)
    {
        Span<byte> digits = stackalloc byte[20];
        v.TryFormat(digits, out int n, provider: CultureInfo.InvariantCulture);
        return n;
    }
}
