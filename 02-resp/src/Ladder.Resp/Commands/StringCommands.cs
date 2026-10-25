using System.Globalization;
using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

/// <summary>String commands, following valkey/src/t_string.c (tag 9.1.2) for options, reply shapes and error text.</summary>
public static class StringCommands
{
    private const long MaxStringLength = 512L * 1024 * 1024;

    [Flags]
    private enum Opt
    {
        None = 0,
        NX = 1,
        XX = 2,
        IfEq = 4,
        Get = 8,
        KeepTtl = 16,
        Persist = 32,
        EX = 64,
        PX = 128,
        EXAT = 256,
        PXAT = 512,
    }

    private enum OptKind
    {
        Set,
        GetEx,
        MSet,
    }

    internal static void Register(Action<CommandSpec> add)
    {
        add(new("get", 2, CommandTraits.None, Get));
        add(new("set", -3, CommandTraits.Write, Set));
        add(new("setnx", 3, CommandTraits.Write, SetNx));
        add(new("setex", 4, CommandTraits.Write, c => SetEx(c, Opt.EX)));
        add(new("psetex", 4, CommandTraits.Write, c => SetEx(c, Opt.PX)));
        add(new("getset", 3, CommandTraits.Write, GetSet));
        add(new("getdel", 2, CommandTraits.Write, GetDel));
        add(new("getex", -2, CommandTraits.Write, GetEx));
        add(new("mget", -2, CommandTraits.None, MGet));
        add(new("mset", -3, CommandTraits.Write, MSet));
        add(new("msetnx", -3, CommandTraits.Write, MSetNx));
        add(new("msetex", -4, CommandTraits.Write, MSetEx));
        add(new("append", 3, CommandTraits.Write, Append));
        add(new("strlen", 2, CommandTraits.None, StrLen));
        add(new("setrange", 4, CommandTraits.Write, SetRange));
        add(new("getrange", 4, CommandTraits.None, GetRange));
        add(new("substr", 4, CommandTraits.None, GetRange));
        add(new("incr", 2, CommandTraits.Write, c => IncrBy(c, 1)));
        add(new("decr", 2, CommandTraits.Write, c => IncrBy(c, -1)));
        add(new("incrby", 3, CommandTraits.Write, IncrByCommand));
        add(new("decrby", 3, CommandTraits.Write, DecrByCommand));
        add(new("incrbyfloat", 3, CommandTraits.Write, IncrByFloat));
        add(new("setbit", 4, CommandTraits.Write, SetBit));
        add(new("getbit", 3, CommandTraits.None, GetBit));
        add(new("delifeq", 3, CommandTraits.Write, DelIfEq));
        add(new("lcs", -3, CommandTraits.None, Lcs));
    }

    private static void Get(CommandContext c)
    {
        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            c.NullBulk();
        }
        else
        {
            value.WriteBulk(c.Out);
        }
    }

    private static void Set(CommandContext c)
    {
        if (!TryParseOptions(c, OptKind.Set, 3, c.ArgCount, out Opt flags, out int expireIdx, out int compareIdx))
        {
            return;
        }

        SetGeneric(c, flags, 1, 2, expireIdx, compareIdx, okReply: null, abortReply: null);
    }

    private static void SetNx(CommandContext c) => SetGeneric(c, Opt.NX, 1, 2, -1, -1, okReply: 1, abortReply: 0);

    private static void SetEx(CommandContext c, Opt unit) => SetGeneric(c, unit, 1, 3, 2, -1, okReply: null, abortReply: null);

    private static void GetSet(CommandContext c) => SetGeneric(c, Opt.Get, 1, 2, -1, -1, okReply: null, abortReply: null);

    /// <summary>
    /// setGenericCommand: SET, SETNX, SETEX, PSETEX and GETSET. A non-null <paramref name="okReply"/> or
    /// <paramref name="abortReply"/> replaces +OK / null with an integer (SETNX).
    /// </summary>
    private static void SetGeneric(CommandContext c, Opt flags, int keyIdx, int valueIdx, int expireIdx, int compareIdx, long? okReply, long? abortReply)
    {
        long expireAt = -1;
        if (expireIdx >= 0 && !TryExpireAt(c, flags, expireIdx, out expireAt))
        {
            return;
        }

        ReadOnlySpan<byte> key = c[keyIdx];
        Entry? existing = c.Db.Lookup(key, c.Now);
        bool get = (flags & Opt.Get) != 0;
        if (get)
        {
            if (existing is not null && existing.Value is not RString)
            {
                c.Error(Errors.WrongType);
                return;
            }

            if (existing is null)
            {
                c.NullBulk();
            }
            else
            {
                ((RString)existing.Value).WriteBulk(c.Out);
            }
        }

        bool found = existing is not null;
        if ((flags & Opt.IfEq) != 0)
        {
            if (!found)
            {
                Abort(c, get, abortReply);
                return;
            }

            if (existing!.Value is not RString current)
            {
                c.Error(Errors.WrongType);
                return;
            }

            if (!current.GetBytes(stackalloc byte[20]).SequenceEqual(c[compareIdx]))
            {
                Abort(c, get, abortReply);
                return;
            }
        }

        if (((flags & Opt.NX) != 0 && found) || ((flags & Opt.XX) != 0 && !found))
        {
            Abort(c, get, abortReply);
            return;
        }

        if (expireAt >= 0 && expireAt <= c.Now)
        {
            if (found)
            {
                c.Db.ExpireNow(key);
            }

            if (!get)
            {
                c.Ok();
            }

            return;
        }

        c.Db.Upsert(key, RString.FromValue(c[valueIdx]), c.Now, keepTtl: (flags & Opt.KeepTtl) != 0, expireAt: expireAt);
        if (!get)
        {
            if (okReply is long n)
            {
                c.Integer(n);
            }
            else
            {
                c.Ok();
            }
        }
    }

    private static void Abort(CommandContext c, bool get, long? abortReply)
    {
        if (get)
        {
            return;
        }

        if (abortReply is long n)
        {
            c.Integer(n);
        }
        else
        {
            c.NullBulk();
        }
    }

    private static void GetDel(CommandContext c)
    {
        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            c.NullBulk();
            return;
        }

        value.WriteBulk(c.Out);
        c.Db.Delete(c[1], c.Now);
    }

    private static void GetEx(CommandContext c)
    {
        if (!TryParseOptions(c, OptKind.GetEx, 2, c.ArgCount, out Opt flags, out int expireIdx, out _))
        {
            return;
        }

        long expireAt = -1;
        if (expireIdx >= 0 && !TryExpireAt(c, flags, expireIdx, out expireAt))
        {
            return;
        }

        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            c.NullBulk();
            return;
        }

        value.WriteBulk(c.Out);
        if (expireIdx >= 0 && expireAt <= c.Now)
        {
            c.Db.ExpireNow(c[1]);
        }
        else if (expireIdx >= 0)
        {
            c.Db.SetExpire(c[1], c.Now, expireAt);
        }
        else if ((flags & Opt.Persist) != 0)
        {
            c.Db.SetExpire(c[1], c.Now, -1);
        }
    }

    private static void MGet(CommandContext c)
    {
        c.ArrayHeader(c.ArgCount - 1);
        for (int i = 1; i < c.ArgCount; i++)
        {
            if (c.Db.Lookup(c[i], c.Now)?.Value is RString s)
            {
                s.WriteBulk(c.Out);
            }
            else
            {
                c.NullBulk();
            }
        }
    }

    private static void MSet(CommandContext c)
    {
        if ((c.ArgCount % 2) == 0)
        {
            c.Error($"ERR wrong number of arguments for '{c.Command.Name}' command");
            return;
        }

        for (int i = 1; i < c.ArgCount; i += 2)
        {
            c.Db.Upsert(c[i], RString.FromValue(c[i + 1]), c.Now);
        }

        c.Ok();
    }

    private static void MSetNx(CommandContext c)
    {
        if ((c.ArgCount % 2) == 0)
        {
            c.Error($"ERR wrong number of arguments for '{c.Command.Name}' command");
            return;
        }

        for (int i = 1; i < c.ArgCount; i += 2)
        {
            if (c.Db.Lookup(c[i], c.Now) is not null)
            {
                c.Integer(0);
                return;
            }
        }

        for (int i = 1; i < c.ArgCount; i += 2)
        {
            c.Db.Upsert(c[i], RString.FromValue(c[i + 1]), c.Now);
        }

        c.Integer(1);
    }

    private static void MSetEx(CommandContext c)
    {
        if (!RString.TryParseStrict(c[1], out long numKeys) || numKeys < 1 || numKeys > int.MaxValue)
        {
            c.Error("ERR invalid numkeys value or out of range");
            return;
        }

        long optionsStart = 2 + (numKeys * 2);
        if (optionsStart > c.ArgCount)
        {
            c.Error(Errors.Syntax);
            return;
        }

        if (!TryParseOptions(c, OptKind.MSet, (int)optionsStart, c.ArgCount, out Opt flags, out int expireIdx, out _))
        {
            return;
        }

        long expireAt = -1;
        if (expireIdx >= 0 && !TryExpireAt(c, flags, expireIdx, out expireAt))
        {
            return;
        }

        if ((flags & (Opt.NX | Opt.XX)) != 0)
        {
            for (int j = 2; j < optionsStart; j += 2)
            {
                bool exists = c.Db.Lookup(c[j], c.Now) is not null;
                if (((flags & Opt.NX) != 0 && exists) || ((flags & Opt.XX) != 0 && !exists))
                {
                    c.Integer(0);
                    return;
                }
            }
        }

        // Unlike SET, an already-past expiry still stores the keys; lazy or active expiry removes them.
        for (int j = 2; j < optionsStart; j += 2)
        {
            c.Db.Upsert(c[j], RString.FromValue(c[j + 1]), c.Now, keepTtl: (flags & Opt.KeepTtl) != 0, expireAt: expireAt);
        }

        c.Integer(1);
    }

    private static void Append(CommandContext c)
    {
        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        ReadOnlySpan<byte> suffix = c[2];
        if (value is null)
        {
            c.Db.Upsert(c[1], RString.FromRaw(suffix), c.Now, keepTtl: true);
            c.Integer(suffix.Length);
            return;
        }

        long total = (long)value.Length + suffix.Length;
        if (total > MaxStringLength)
        {
            c.Error(Errors.TooBig);
            return;
        }

        int oldLength = value.Length;
        Span<byte> bytes = value.MakeRawWritable((int)total);
        suffix.CopyTo(bytes[oldLength..]);
        c.Integer(total);
    }

    private static void StrLen(CommandContext c)
    {
        if (c.TryGet(c[1], out RString? value))
        {
            c.Integer(value?.Length ?? 0);
        }
    }

    private static void SetRange(CommandContext c)
    {
        if (!c.TryLong(2, out long offset))
        {
            return;
        }

        if (offset < 0)
        {
            c.Error("ERR offset is out of range");
            return;
        }

        ReadOnlySpan<byte> patch = c[3];
        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            if (patch.IsEmpty)
            {
                c.Integer(0);
                return;
            }

            if (offset > MaxStringLength - patch.Length)
            {
                c.Error(Errors.TooBig);
                return;
            }

            value = RString.FromRaw([]);
            c.Db.Upsert(c[1], value, c.Now);
        }
        else
        {
            if (patch.IsEmpty)
            {
                c.Integer(value.Length);
                return;
            }

            if (offset > MaxStringLength - patch.Length)
            {
                c.Error(Errors.TooBig);
                return;
            }
        }

        int needed = (int)Math.Max(value.Length, offset + patch.Length);
        Span<byte> bytes = value.MakeRawWritable(needed);
        patch.CopyTo(bytes[(int)offset..]);
        c.Integer(value.Length);
    }

    private static void GetRange(CommandContext c)
    {
        if (!c.TryLong(2, out long start) || !c.TryLong(3, out long end))
        {
            return;
        }

        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            c.Bulk([]);
            return;
        }

        ReadOnlySpan<byte> s = value.GetBytes(stackalloc byte[20]);
        long len = s.Length;
        if (start < 0 && end < 0 && start > end)
        {
            c.Bulk([]);
            return;
        }

        if (start < 0)
        {
            start = len + start;
        }

        if (end < 0)
        {
            end = len + end;
        }

        start = Math.Max(start, 0);
        end = Math.Max(end, 0);
        if (end >= len)
        {
            end = len - 1;
        }

        if (start > end || len == 0)
        {
            c.Bulk([]);
            return;
        }

        c.Bulk(s.Slice((int)start, (int)(end - start + 1)));
    }

    private static void IncrByCommand(CommandContext c)
    {
        if (c.TryLong(2, out long by))
        {
            IncrBy(c, by);
        }
    }

    private static void DecrByCommand(CommandContext c)
    {
        if (!c.TryLong(2, out long by))
        {
            return;
        }

        if (by == long.MinValue)
        {
            c.Error("ERR decrement would overflow");
            return;
        }

        IncrBy(c, -by);
    }

    /// <summary>INCR family. On an int-encoded value this updates in place, so the hot path allocates nothing.</summary>
    private static void IncrBy(CommandContext c, long by)
    {
        Entry? entry = c.Db.Lookup(c[1], c.Now);
        long current = 0;
        if (entry is not null)
        {
            if (entry.Value is not RString s)
            {
                c.Error(Errors.WrongType);
                return;
            }

            if (!s.TryGetLong(out current))
            {
                c.Error(Errors.NotInteger);
                return;
            }
        }

        if ((by < 0 && current < 0 && by < long.MinValue - current) || (by > 0 && current > 0 && by > long.MaxValue - current))
        {
            c.Error(Errors.Overflow);
            return;
        }

        long result = current + by;
        if (entry is not null && ((RString)entry.Value).TrySetIntInPlace(result))
        {
            c.Integer(result);
            return;
        }

        if (entry is not null)
        {
            entry.Value = RString.FromInt(result);
        }
        else
        {
            c.Db.Upsert(c[1], RString.FromInt(result), c.Now);
        }

        c.Integer(result);
    }

    private static void IncrByFloat(CommandContext c)
    {
        Entry? entry = c.Db.Lookup(c[1], c.Now);
        double current = 0;
        if (entry is not null)
        {
            if (entry.Value is not RString s)
            {
                c.Error(Errors.WrongType);
                return;
            }

            if (!TryParseFloat(s.GetBytes(stackalloc byte[20]), out current))
            {
                c.Error(Errors.NotFloat);
                return;
            }
        }

        if (!TryParseFloat(c[2], out double by))
        {
            c.Error(Errors.NotFloat);
            return;
        }

        double result = current + by;
        if (double.IsNaN(result) || double.IsInfinity(result))
        {
            c.Error("ERR increment would produce NaN or Infinity");
            return;
        }

        byte[] text = Encoding.ASCII.GetBytes(FormatHuman(result));
        c.Db.Upsert(c[1], RString.FromComputed(text), c.Now, keepTtl: true);
        c.Bulk(text);
    }

    private static void SetBit(CommandContext c)
    {
        if (!TryBitOffset(c, 2, out long offset))
        {
            return;
        }

        ReadOnlySpan<byte> bitArg = c[3];
        if (bitArg.Length != 1 || (bitArg[0] != (byte)'0' && bitArg[0] != (byte)'1'))
        {
            c.Error("ERR bit is not an integer or out of range");
            return;
        }

        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            value = RString.FromRaw([]);
            c.Db.Upsert(c[1], value, c.Now);
        }

        int byteIndex = (int)(offset >> 3);
        Span<byte> bytes = value.MakeRawWritable(Math.Max(value.Length, byteIndex + 1));
        int mask = 1 << (7 - (int)(offset & 7));
        int old = (bytes[byteIndex] & mask) != 0 ? 1 : 0;
        bytes[byteIndex] = bitArg[0] == (byte)'1' ? (byte)(bytes[byteIndex] | mask) : (byte)(bytes[byteIndex] & ~mask);
        c.Integer(old);
    }

    private static void GetBit(CommandContext c)
    {
        if (!TryBitOffset(c, 2, out long offset) || !c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null)
        {
            c.Integer(0);
            return;
        }

        ReadOnlySpan<byte> bytes = value.GetBytes(stackalloc byte[20]);
        long byteIndex = offset >> 3;
        c.Integer(byteIndex < bytes.Length && (bytes[(int)byteIndex] & (1 << (7 - (int)(offset & 7)))) != 0 ? 1 : 0);
    }

    private static void DelIfEq(CommandContext c)
    {
        if (!c.TryGet(c[1], out RString? value))
        {
            return;
        }

        if (value is null || !value.GetBytes(stackalloc byte[20]).SequenceEqual(c[2]))
        {
            c.Integer(0);
            return;
        }

        c.Db.Delete(c[1], c.Now);
        c.Integer(1);
    }

    /// <summary>LCS, ported from lcsCommand: the dynamic-programming table, then a walk back that emits match ranges.</summary>
    private static void Lcs(CommandContext c)
    {
        Entry? ea = c.Db.Lookup(c[1], c.Now);
        Entry? eb = c.Db.Lookup(c[2], c.Now);
        if ((ea is not null && ea.Value is not RString) || (eb is not null && eb.Value is not RString))
        {
            c.Error("ERR The specified keys must contain string values");
            return;
        }

        byte[] a = (ea?.Value as RString)?.ToArray() ?? [];
        byte[] b = (eb?.Value as RString)?.ToArray() ?? [];
        bool getLen = false, getIdx = false, withMatchLen = false;
        long minMatchLen = 0;
        for (int j = 3; j < c.ArgCount; j++)
        {
            bool moreArgs = j < c.ArgCount - 1;
            if (c.ArgIs(j, "idx"))
            {
                getIdx = true;
            }
            else if (c.ArgIs(j, "len"))
            {
                getLen = true;
            }
            else if (c.ArgIs(j, "withmatchlen"))
            {
                withMatchLen = true;
            }
            else if (c.ArgIs(j, "minmatchlen") && moreArgs)
            {
                if (!c.TryLong(++j, out minMatchLen))
                {
                    return;
                }

                minMatchLen = Math.Max(minMatchLen, 0);
            }
            else
            {
                c.Error(Errors.Syntax);
                return;
            }
        }

        if (getLen && getIdx)
        {
            c.Error("ERR If you want both the length and indexes, please just use IDX.");
            return;
        }

        int alen = a.Length, blen = b.Length;
        if ((long)(alen + 1) * (blen + 1) * sizeof(uint) > MaxStringLength)
        {
            c.Error("ERR Insufficient memory, transient memory for LCS exceeds proto-max-bulk-len");
            return;
        }

        var table = new uint[(alen + 1) * (blen + 1)];
        int w = blen + 1;
        for (int i = 1; i <= alen; i++)
        {
            for (int j = 1; j <= blen; j++)
            {
                table[(i * w) + j] = a[i - 1] == b[j - 1]
                    ? table[((i - 1) * w) + j - 1] + 1
                    : Math.Max(table[((i - 1) * w) + j], table[(i * w) + j - 1]);
            }
        }

        uint length = table[(alen * w) + blen];
        if (getLen)
        {
            c.Integer(length);
            return;
        }

        var result = new byte[length];
        var ranges = new List<(int AStart, int AEnd, int BStart, int BEnd)>();
        uint idx = length;
        int ia = alen, ib = blen;
        int aStart = alen, aEnd = 0, bStart = 0, bEnd = 0;
        while (ia > 0 && ib > 0)
        {
            bool emit = false;
            if (a[ia - 1] == b[ib - 1])
            {
                result[idx - 1] = a[ia - 1];
                if (aStart == alen)
                {
                    aStart = aEnd = ia - 1;
                    bStart = bEnd = ib - 1;
                }
                else if (aStart == ia && bStart == ib)
                {
                    aStart--;
                    bStart--;
                }
                else
                {
                    emit = true;
                }

                if (aStart == 0 || bStart == 0)
                {
                    emit = true;
                }

                idx--;
                ia--;
                ib--;
            }
            else
            {
                if (table[((ia - 1) * w) + ib] > table[(ia * w) + ib - 1])
                {
                    ia--;
                }
                else
                {
                    ib--;
                }

                if (aStart != alen)
                {
                    emit = true;
                }
            }

            if (emit)
            {
                if (minMatchLen == 0 || aEnd - aStart + 1 >= minMatchLen)
                {
                    ranges.Add((aStart, aEnd, bStart, bEnd));
                }

                aStart = alen;
            }
        }

        if (!getIdx)
        {
            c.Bulk(result);
            return;
        }

        c.ArrayHeader(4);
        c.Bulk("matches"u8);
        c.ArrayHeader(ranges.Count);
        foreach (var r in ranges)
        {
            c.ArrayHeader(withMatchLen ? 3 : 2);
            c.ArrayHeader(2);
            c.Integer(r.AStart);
            c.Integer(r.AEnd);
            c.ArrayHeader(2);
            c.Integer(r.BStart);
            c.Integer(r.BEnd);
            if (withMatchLen)
            {
                c.Integer(r.AEnd - r.AStart + 1);
            }
        }

        c.Bulk("len"u8);
        c.Integer(length);
    }

    private static bool TryBitOffset(CommandContext c, int index, out long offset)
    {
        if (!RString.TryParseStrict(c[index], out offset) || offset < 0 || (offset >> 3) >= MaxStringLength)
        {
            c.Error("ERR bit offset is not an integer or out of range");
            return false;
        }

        return true;
    }

    /// <summary>parseExtendedCommandArgumentsOrReply from valkey/src/server.c, for SET, GETEX and MSETEX.</summary>
    private static bool TryParseOptions(CommandContext c, OptKind kind, int start, int end, out Opt flags, out int expireIdx, out int compareIdx)
    {
        flags = Opt.None;
        expireIdx = -1;
        compareIdx = -1;
        const Opt anyExpire = Opt.EX | Opt.PX | Opt.EXAT | Opt.PXAT;
        bool setLike = kind is OptKind.Set or OptKind.MSet;
        for (int j = start; j < end; j++)
        {
            bool hasNext = j < end - 1;
            if (c.ArgIs(j, "nx") && (flags & (Opt.XX | Opt.IfEq)) == 0 && setLike)
            {
                flags |= Opt.NX;
            }
            else if (c.ArgIs(j, "xx") && (flags & (Opt.NX | Opt.IfEq)) == 0 && setLike)
            {
                flags |= Opt.XX;
            }
            else if (c.ArgIs(j, "ifeq") && hasNext && (flags & (Opt.NX | Opt.XX | Opt.IfEq)) == 0 && kind == OptKind.Set)
            {
                flags |= Opt.IfEq;
                compareIdx = ++j;
            }
            else if (c.ArgIs(j, "get") && kind == OptKind.Set)
            {
                flags |= Opt.Get;
            }
            else if (c.ArgIs(j, "keepttl") && (flags & (Opt.Persist | anyExpire)) == 0 && setLike)
            {
                flags |= Opt.KeepTtl;
            }
            else if (c.ArgIs(j, "persist") && kind == OptKind.GetEx && (flags & (anyExpire | Opt.KeepTtl)) == 0)
            {
                flags |= Opt.Persist;
            }
            else if (TryExpireOption(c, j, hasNext, ref flags))
            {
                expireIdx = ++j;
            }
            else
            {
                c.Error(Errors.Syntax);
                return false;
            }
        }

        return true;
    }

    private static bool TryExpireOption(CommandContext c, int j, bool hasNext, ref Opt flags)
    {
        const Opt blockers = Opt.KeepTtl | Opt.Persist | Opt.EX | Opt.PX | Opt.EXAT | Opt.PXAT;
        if (!hasNext || (flags & blockers) != 0)
        {
            return false;
        }

        Opt found = c.ArgIs(j, "ex") ? Opt.EX : c.ArgIs(j, "px") ? Opt.PX : c.ArgIs(j, "exat") ? Opt.EXAT : c.ArgIs(j, "pxat") ? Opt.PXAT : Opt.None;
        flags |= found;
        return found != Opt.None;
    }

    /// <summary>getExpireMillisecondsOrReply: converts EX/PX/EXAT/PXAT to an absolute Unix-ms time.</summary>
    private static bool TryExpireAt(CommandContext c, Opt flags, int index, out long at)
    {
        at = -1;
        if (!c.TryLong(index, out long ms))
        {
            return false;
        }

        bool seconds = (flags & (Opt.EX | Opt.EXAT)) != 0;
        if (ms <= 0 || (seconds && ms > long.MaxValue / 1000))
        {
            c.Error($"ERR invalid expire time in '{c.Command.Name}' command");
            return false;
        }

        if (seconds)
        {
            ms *= 1000;
        }

        if ((flags & (Opt.EX | Opt.PX)) != 0)
        {
            ms = unchecked(ms + c.Now);
        }

        if (ms <= 0)
        {
            c.Error($"ERR invalid expire time in '{c.Command.Name}' command");
            return false;
        }

        at = ms;
        return true;
    }

    /// <summary>
    /// string2ld: a strtold-compatible float with no leading space, no trailing garbage, no NaN, and no
    /// overflow to infinity or underflow to zero from finite input. "inf" and "infinity" are accepted.
    /// </summary>
    internal static bool TryParseFloat(ReadOnlySpan<byte> text, out double value)
    {
        value = 0;
        if (text.IsEmpty || text.Length >= 5 * 1024 || text[0] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\v' or (byte)'\f')
        {
            return false;
        }

        ReadOnlySpan<byte> unsigned = text[0] is (byte)'+' or (byte)'-' ? text[1..] : text;
        if (Ascii.EqualsIgnoreCase(unsigned, "inf"u8) || Ascii.EqualsIgnoreCase(unsigned, "infinity"u8))
        {
            value = text[0] == (byte)'-' ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }

        foreach (byte b in text)
        {
            if (!(char.IsAsciiDigit((char)b) || b is (byte)'.' or (byte)'e' or (byte)'E' or (byte)'+' or (byte)'-'))
            {
                return false;
            }
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        if (value == 0)
        {
            // strtold reports ERANGE when a non-zero mantissa underflows to zero.
            int e = text.IndexOfAny("eE"u8);
            ReadOnlySpan<byte> mantissa = e < 0 ? text : text[..e];
            if (mantissa.IndexOfAnyInRange((byte)'1', (byte)'9') >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ld2string(LD_STR_HUMAN): "%.17f" with trailing zeros and a bare point removed, and "-0" written as "0".</summary>
    internal static string FormatHuman(double value)
    {
        string s = value.ToString("F17", CultureInfo.InvariantCulture);
        if (s.Contains('.', StringComparison.Ordinal))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }

        return s == "-0" ? "0" : s;
    }
}
