using System.Globalization;
using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

/// <summary>Generic keyspace and expiry commands, following valkey/src/db.c and expire.c (tag 9.1.2).</summary>
public static class KeyCommands
{
    [Flags]
    private enum ExpireFlags
    {
        None = 0,
        NX = 1,
        XX = 2,
        GT = 4,
        LT = 8,
    }

    internal static void Register(Action<CommandSpec> add)
    {
        add(new("del", -2, CommandTraits.Write, Del));
        add(new("unlink", -2, CommandTraits.Write, Del));
        add(new("exists", -2, CommandTraits.None, Exists));
        add(new("touch", -2, CommandTraits.None, Exists));
        add(new("type", 2, CommandTraits.None, Type));
        add(new("rename", 3, CommandTraits.Write, c => Rename(c, nx: false)));
        add(new("renamenx", 3, CommandTraits.Write, c => Rename(c, nx: true)));
        add(new("copy", -3, CommandTraits.Write, Copy));
        add(new("move", 3, CommandTraits.Write, Move));
        add(new("keys", 2, CommandTraits.None, Keys));
        add(new("scan", -2, CommandTraits.None, Scan));
        add(new("randomkey", 1, CommandTraits.None, RandomKey));
        add(new("dbsize", 1, CommandTraits.None, c => c.Integer(c.Db.Count)));
        add(new("expire", -3, CommandTraits.Write, c => Expire(c, seconds: true, relative: true)));
        add(new("pexpire", -3, CommandTraits.Write, c => Expire(c, seconds: false, relative: true)));
        add(new("expireat", -3, CommandTraits.Write, c => Expire(c, seconds: true, relative: false)));
        add(new("pexpireat", -3, CommandTraits.Write, c => Expire(c, seconds: false, relative: false)));
        add(new("ttl", 2, CommandTraits.None, c => Ttl(c, ms: false, absolute: false)));
        add(new("pttl", 2, CommandTraits.None, c => Ttl(c, ms: true, absolute: false)));
        add(new("expiretime", 2, CommandTraits.None, c => Ttl(c, ms: false, absolute: true)));
        add(new("pexpiretime", 2, CommandTraits.None, c => Ttl(c, ms: true, absolute: true)));
        add(new("persist", 2, CommandTraits.Write, Persist));
        add(new("object", -2, CommandTraits.None, ObjectCommand));
    }

    private static void Del(CommandContext c)
    {
        int deleted = 0;
        for (int i = 1; i < c.ArgCount; i++)
        {
            if (c.Db.Delete(c[i], c.Now))
            {
                deleted++;
            }
        }

        c.Integer(deleted);
    }

    private static void Exists(CommandContext c)
    {
        int found = 0;
        for (int i = 1; i < c.ArgCount; i++)
        {
            if (c.Db.Lookup(c[i], c.Now) is not null)
            {
                found++;
            }
        }

        c.Integer(found);
    }

    private static void Type(CommandContext c) => RespWriter.Simple(c.Out, TypeName(c.Db.Lookup(c[1], c.Now)?.Value));

    internal static ReadOnlySpan<byte> TypeName(object? value) => value switch
    {
        null => "none"u8,
        RString => "string"u8,
        RList => "list"u8,
        RSet => "set"u8,
        RHash => "hash"u8,
        RZSet => "zset"u8,
        _ => "unknown"u8,
    };

    private static void Rename(CommandContext c, bool nx)
    {
        Entry? source = c.Db.Lookup(c[1], c.Now);
        if (source is null)
        {
            c.Error(Errors.NoSuchKey);
            return;
        }

        if (c[1].SequenceEqual(c[2]))
        {
            if (nx)
            {
                c.Integer(0);
            }
            else
            {
                c.Ok();
            }

            return;
        }

        if (c.Db.Lookup(c[2], c.Now) is not null)
        {
            if (nx)
            {
                c.Integer(0);
                return;
            }

            c.Db.Delete(c[2], c.Now);
        }

        Entry moved = c.Db.Detach(c[1], out _)!;
        c.Db.Add(c[2].ToArray(), moved);
        if (nx)
        {
            c.Integer(1);
        }
        else
        {
            c.Ok();
        }
    }

    private static void Copy(CommandContext c)
    {
        Database target = c.Db;
        int targetIndex = c.Session.Db;
        bool replace = false;
        for (int j = 3; j < c.ArgCount; j++)
        {
            if (c.ArgIs(j, "replace"))
            {
                replace = true;
            }
            else if (c.ArgIs(j, "db") && j + 1 < c.ArgCount)
            {
                if (!TryDbIndex(c, ++j, out targetIndex))
                {
                    return;
                }

                target = c.Keyspace[targetIndex];
            }
            else
            {
                c.Error(Errors.Syntax);
                return;
            }
        }

        if (targetIndex == c.Session.Db && c[1].SequenceEqual(c[2]))
        {
            c.Error("ERR source and destination objects are the same");
            return;
        }

        Entry? source = c.Db.Lookup(c[1], c.Now);
        if (source is null)
        {
            c.Integer(0);
            return;
        }

        if (target.Lookup(c[2], c.Now) is not null)
        {
            if (!replace)
            {
                c.Integer(0);
                return;
            }

            target.Delete(c[2], c.Now);
        }

        target.Add(c[2].ToArray(), new Entry(CloneValue(source.Value)) { ExpiresAt = source.ExpiresAt });
        c.Integer(1);
    }

    private static object CloneValue(object value) => value switch
    {
        RString s => s.Encoding == StringEncoding.Int && s.TryGetLong(out long n) ? RString.FromInt(n) : CloneString(s),
        RList l => l.Clone(),
        RSet s => s.Clone(),
        RHash h => h.Clone(),
        RZSet z => z.Clone(),
        _ => value,
    };

    private static RString CloneString(RString s)
    {
        byte[] bytes = s.ToArray();
        return s.Encoding == StringEncoding.Raw ? RString.FromRaw(bytes) : RString.FromComputed(bytes);
    }

    private static void Move(CommandContext c)
    {
        if (!RString.TryParseStrict(c[2], out long index) || index < int.MinValue || index > int.MaxValue)
        {
            c.Error(Errors.NotInteger);
            return;
        }

        if (index < 0 || index >= Keyspace.DatabaseCount)
        {
            c.Error(Errors.DbIndex);
            return;
        }

        if (index == c.Session.Db)
        {
            c.Error("ERR source and destination objects are the same");
            return;
        }

        Database target = c.Keyspace[(int)index];
        if (c.Db.Lookup(c[1], c.Now) is null || target.Lookup(c[1], c.Now) is not null)
        {
            c.Integer(0);
            return;
        }

        Entry entry = c.Db.Detach(c[1], out byte[]? key)!;
        target.Add(key!, entry);
        c.Integer(1);
    }

    private static void Keys(CommandContext c)
    {
        var matches = new List<byte[]>();
        bool all = c[1].SequenceEqual("*"u8);
        foreach (byte[] key in c.Db.Keys.ToList())
        {
            if ((all || GlobMatcher.IsMatch(c[1], key)) && c.Db.Lookup(key, c.Now) is not null)
            {
                matches.Add(key);
            }
        }

        c.ArrayHeader(matches.Count);
        foreach (byte[] key in matches)
        {
            c.Bulk(key);
        }
    }

    private static void Scan(CommandContext c)
    {
        if (!ulong.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong cursor))
        {
            c.Error("ERR invalid cursor");
            return;
        }

        int count = 10;
        int matchIdx = -1;
        int typeIdx = -1;
        for (int j = 2; j < c.ArgCount; j++)
        {
            bool hasNext = j + 1 < c.ArgCount;
            if (c.ArgIs(j, "count") && hasNext)
            {
                if (!c.TryLong(++j, out long n))
                {
                    return;
                }

                if (n < 1)
                {
                    c.Error(Errors.Syntax);
                    return;
                }

                count = (int)Math.Min(n, int.MaxValue);
            }
            else if (c.ArgIs(j, "match") && hasNext)
            {
                matchIdx = ++j;
            }
            else if (c.ArgIs(j, "type") && hasNext)
            {
                typeIdx = ++j;
            }
            else
            {
                c.Error(Errors.Syntax);
                return;
            }
        }

        var batch = new List<byte[]>();
        ulong next = c.Db.Scan(cursor, count, batch);
        var keys = new List<byte[]>(batch.Count);
        foreach (byte[] key in batch)
        {
            Entry? entry = c.Db.Lookup(key, c.Now);
            if (entry is null)
            {
                continue;
            }

            if (matchIdx >= 0 && !GlobMatcher.IsMatch(c[matchIdx], key))
            {
                continue;
            }

            if (typeIdx >= 0 && !Ascii.EqualsIgnoreCase(TypeName(entry.Value), c[typeIdx]))
            {
                continue;
            }

            keys.Add(key);
        }

        c.ArrayHeader(2);
        c.Bulk(Encoding.ASCII.GetBytes(next.ToString(CultureInfo.InvariantCulture)));
        c.ArrayHeader(keys.Count);
        foreach (byte[] key in keys)
        {
            c.Bulk(key);
        }
    }

    private static void RandomKey(CommandContext c)
    {
        byte[]? key = c.Db.RandomKey(c.Keyspace.Random, c.Now);
        if (key is null)
        {
            c.NullBulk();
        }
        else
        {
            c.Bulk(key);
        }
    }

    /// <summary>expireGenericCommand from valkey/src/expire.c.</summary>
    private static void Expire(CommandContext c, bool seconds, bool relative)
    {
        ExpireFlags flags = ExpireFlags.None;
        for (int j = 3; j < c.ArgCount; j++)
        {
            if (c.ArgIs(j, "nx"))
            {
                flags |= ExpireFlags.NX;
            }
            else if (c.ArgIs(j, "xx"))
            {
                flags |= ExpireFlags.XX;
            }
            else if (c.ArgIs(j, "gt"))
            {
                flags |= ExpireFlags.GT;
            }
            else if (c.ArgIs(j, "lt"))
            {
                flags |= ExpireFlags.LT;
            }
            else
            {
                c.Error($"ERR Unsupported option {c.ArgString(j)}");
                return;
            }
        }

        if ((flags & ExpireFlags.NX) != 0 && (flags & (ExpireFlags.XX | ExpireFlags.GT | ExpireFlags.LT)) != 0)
        {
            c.Error("ERR NX and XX, GT or LT options at the same time are not compatible");
            return;
        }

        if ((flags & ExpireFlags.GT) != 0 && (flags & ExpireFlags.LT) != 0)
        {
            c.Error("ERR GT and LT options at the same time are not compatible");
            return;
        }

        if (!c.TryLong(2, out long when))
        {
            return;
        }

        if (seconds)
        {
            if (when > long.MaxValue / 1000 || when < long.MinValue / 1000)
            {
                c.Error($"ERR invalid expire time in '{c.Command.Name}' command");
                return;
            }

            when *= 1000;
        }

        if (relative)
        {
            if (when > long.MaxValue - c.Now)
            {
                c.Error($"ERR invalid expire time in '{c.Command.Name}' command");
                return;
            }

            when += c.Now;
        }

        Entry? entry = c.Db.Lookup(c[1], c.Now);
        if (entry is null)
        {
            c.Integer(0);
            return;
        }

        long current = entry.ExpiresAt;
        if (((flags & ExpireFlags.NX) != 0 && current != -1)
            || ((flags & ExpireFlags.XX) != 0 && current == -1)
            || ((flags & ExpireFlags.GT) != 0 && (current == -1 || when <= current))
            || ((flags & ExpireFlags.LT) != 0 && current != -1 && when >= current))
        {
            c.Integer(0);
            return;
        }

        if (when <= c.Now)
        {
            c.Db.ExpireNow(c[1]);
        }
        else
        {
            c.Db.SetExpire(c[1], c.Now, when);
        }

        c.Integer(1);
    }

    private static void Ttl(CommandContext c, bool ms, bool absolute)
    {
        Entry? entry = c.Db.Lookup(c[1], c.Now);
        if (entry is null)
        {
            c.Integer(-2);
            return;
        }

        if (entry.ExpiresAt < 0)
        {
            c.Integer(-1);
            return;
        }

        long ttl = Math.Max(absolute ? entry.ExpiresAt : entry.ExpiresAt - c.Now, 0);
        c.Integer(ms ? ttl : (ttl + 500) / 1000);
    }

    private static void Persist(CommandContext c)
    {
        Entry? entry = c.Db.Lookup(c[1], c.Now);
        if (entry is null || entry.ExpiresAt < 0)
        {
            c.Integer(0);
            return;
        }

        c.Db.SetExpire(c[1], c.Now, -1);
        c.Integer(1);
    }

    private static void ObjectCommand(CommandContext c)
    {
        if (c.ArgIs(1, "help"))
        {
            c.ArrayHeader(1);
            c.Bulk("OBJECT <subcommand> [<arg> [value] [opt] ...]. Subcommands are: ENCODING <key>, FREQ <key>, IDLETIME <key>, REFCOUNT <key>"u8);
            return;
        }

        if (c.ArgCount != 3)
        {
            c.Error($"ERR unknown subcommand or wrong number of arguments for '{c.ArgString(1)}'. Try OBJECT HELP.");
            return;
        }

        Entry? entry = c.Db.Lookup(c[2], c.Now);
        if (entry is null)
        {
            c.NullBulk();
            return;
        }

        if (c.ArgIs(1, "encoding"))
        {
            c.Bulk(Encoding.ASCII.GetBytes(EncodingName(entry.Value, c.Server)));
        }
        else if (c.ArgIs(1, "refcount"))
        {
            c.Integer(1);
        }
        else if (c.ArgIs(1, "idletime") || c.ArgIs(1, "freq"))
        {
            c.Integer(0);
        }
        else
        {
            c.Error($"ERR unknown subcommand or wrong number of arguments for '{c.ArgString(1)}'. Try OBJECT HELP.");
        }
    }

    /// <summary>Approximates Valkey's encoding choice from the configured listpack/intset limits (64-byte values).</summary>
    internal static string EncodingName(object value, ServerState server)
    {
        int setInts = server.ConfigInt("set-max-intset-entries", 512);
        int setEntries = server.ConfigInt("set-max-listpack-entries", 128);
        int hashEntries = server.ConfigInt("hash-max-listpack-entries", 128);
        int zsetEntries = server.ConfigInt("zset-max-listpack-entries", 128);
        return value switch
        {
            RString s => s.Encoding switch
            {
                StringEncoding.Int => "int",
                StringEncoding.Embstr => "embstr",
                _ => "raw",
            },
            RList l => l.Items.Sum(i => i.Length + 11) <= 8192 ? "listpack" : "quicklist",
            RSet s when s.Members.Count <= setInts && s.Members.All(m => RString.TryParseStrict(m, out _)) => "intset",
            RSet s => s.Members.Count <= setEntries && s.Members.All(m => m.Length <= 64) ? "listpack" : "hashtable",
            RHash h => h.Fields.Count <= hashEntries && h.Fields.All(f => f.Key.Length <= 64 && f.Value.Length <= 64) ? "listpack" : "hashtable",
            RZSet z => z.Scores.Count <= zsetEntries && z.Scores.Keys.All(k => k.Length <= 64) ? "listpack" : "skiplist",
            _ => "unknown",
        };
    }

    internal static bool TryDbIndex(CommandContext c, int argIndex, out int index)
    {
        index = 0;
        if (!RString.TryParseStrict(c[argIndex], out long n) || n < int.MinValue || n > int.MaxValue)
        {
            c.Error(Errors.NotInteger);
            return false;
        }

        if (n < 0 || n >= Keyspace.DatabaseCount)
        {
            c.Error(Errors.DbIndex);
            return false;
        }

        index = (int)n;
        return true;
    }
}
