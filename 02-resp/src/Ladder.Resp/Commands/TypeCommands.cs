using System.Globalization;
using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

/// <summary>
/// The smallest useful slice of the list, set, hash and sorted-set commands. They exist so that type checks,
/// TYPE, COPY and WRONGTYPE behaviour can be exercised; they are not a full implementation of those types.
/// </summary>
public static class TypeCommands
{
    internal static void Register(Action<CommandSpec> add)
    {
        add(new("lpush", -3, CommandTraits.Write, c => Push(c, left: true)));
        add(new("rpush", -3, CommandTraits.Write, c => Push(c, left: false)));
        add(new("lpop", 2, CommandTraits.Write, c => Pop(c, left: true)));
        add(new("rpop", 2, CommandTraits.Write, c => Pop(c, left: false)));
        add(new("llen", 2, CommandTraits.None, c => { if (c.TryGet(c[1], out RList? l)) { c.Integer(l?.Items.Count ?? 0); } }));
        add(new("lrange", 4, CommandTraits.None, LRange));
        add(new("sadd", -3, CommandTraits.Write, SAdd));
        add(new("smembers", 2, CommandTraits.None, SMembers));
        add(new("scard", 2, CommandTraits.None, c => { if (c.TryGet(c[1], out RSet? s)) { c.Integer(s?.Members.Count ?? 0); } }));
        add(new("sismember", 3, CommandTraits.None, c => { if (c.TryGet(c[1], out RSet? s)) { c.Integer(s is not null && s.Members.Contains(c[2].ToArray()) ? 1 : 0); } }));
        add(new("hset", -4, CommandTraits.Write, HSet));
        add(new("hget", 3, CommandTraits.None, HGet));
        add(new("hgetall", 2, CommandTraits.None, HGetAll));
        add(new("hlen", 2, CommandTraits.None, c => { if (c.TryGet(c[1], out RHash? h)) { c.Integer(h?.Fields.Count ?? 0); } }));
        add(new("zadd", -4, CommandTraits.Write, ZAdd));
        add(new("zrange", -4, CommandTraits.None, ZRange));
        add(new("zcard", 2, CommandTraits.None, c => { if (c.TryGet(c[1], out RZSet? z)) { c.Integer(z?.Scores.Count ?? 0); } }));
        add(new("zscore", 3, CommandTraits.None, ZScore));
    }

    private static T? GetOrCreate<T>(CommandContext c, Func<T> create)
        where T : class
    {
        if (!c.TryGet(c[1], out T? value))
        {
            return null;
        }

        if (value is null)
        {
            value = create();
            c.Db.Upsert(c[1], value, c.Now);
        }

        return value;
    }

    private static void Push(CommandContext c, bool left)
    {
        RList? list = GetOrCreate(c, () => new RList());
        if (list is null)
        {
            return;
        }

        for (int i = 2; i < c.ArgCount; i++)
        {
            if (left)
            {
                list.Items.Insert(0, c[i].ToArray());
            }
            else
            {
                list.Items.Add(c[i].ToArray());
            }
        }

        c.Integer(list.Items.Count);
    }

    private static void Pop(CommandContext c, bool left)
    {
        if (!c.TryGet(c[1], out RList? list))
        {
            return;
        }

        if (list is null || list.Items.Count == 0)
        {
            c.NullBulk();
            return;
        }

        int index = left ? 0 : list.Items.Count - 1;
        byte[] item = list.Items[index];
        list.Items.RemoveAt(index);
        if (list.Items.Count == 0)
        {
            c.Db.Delete(c[1], c.Now);
        }

        c.Bulk(item);
    }

    private static void LRange(CommandContext c)
    {
        if (!c.TryLong(2, out long start) || !c.TryLong(3, out long stop) || !c.TryGet(c[1], out RList? list))
        {
            return;
        }

        int n = list?.Items.Count ?? 0;
        start = start < 0 ? Math.Max(n + start, 0) : start;
        stop = stop < 0 ? n + stop : Math.Min(stop, n - 1);
        if (list is null || start > stop || start >= n)
        {
            c.ArrayHeader(0);
            return;
        }

        c.ArrayHeader(stop - start + 1);
        for (long i = start; i <= stop; i++)
        {
            c.Bulk(list.Items[(int)i]);
        }
    }

    private static void SAdd(CommandContext c)
    {
        RSet? set = GetOrCreate(c, () => new RSet());
        if (set is null)
        {
            return;
        }

        int added = 0;
        for (int i = 2; i < c.ArgCount; i++)
        {
            if (set.Members.Add(c[i].ToArray()))
            {
                added++;
            }
        }

        c.Integer(added);
    }

    private static void SMembers(CommandContext c)
    {
        if (!c.TryGet(c[1], out RSet? set))
        {
            return;
        }

        c.ArrayHeader(set?.Members.Count ?? 0);
        foreach (byte[] m in set?.Members ?? [])
        {
            c.Bulk(m);
        }
    }

    private static void HSet(CommandContext c)
    {
        if (c.ArgCount % 2 != 0)
        {
            c.Error($"ERR wrong number of arguments for '{c.Command.Name}' command");
            return;
        }

        RHash? hash = GetOrCreate(c, () => new RHash());
        if (hash is null)
        {
            return;
        }

        int added = 0;
        for (int i = 2; i < c.ArgCount; i += 2)
        {
            if (hash.Fields.TryAdd(c[i].ToArray(), c[i + 1].ToArray()))
            {
                added++;
            }
            else
            {
                hash.Fields[c[i].ToArray()] = c[i + 1].ToArray();
            }
        }

        c.Integer(added);
    }

    private static void HGet(CommandContext c)
    {
        if (!c.TryGet(c[1], out RHash? hash))
        {
            return;
        }

        if (hash is not null && hash.Fields.TryGetValue(c[2].ToArray(), out byte[]? value))
        {
            c.Bulk(value);
        }
        else
        {
            c.NullBulk();
        }
    }

    private static void HGetAll(CommandContext c)
    {
        if (!c.TryGet(c[1], out RHash? hash))
        {
            return;
        }

        c.ArrayHeader((hash?.Fields.Count ?? 0) * 2);
        foreach (var kv in hash?.Fields ?? [])
        {
            c.Bulk(kv.Key);
            c.Bulk(kv.Value);
        }
    }

    private static void ZAdd(CommandContext c)
    {
        if (c.ArgCount % 2 != 0)
        {
            c.Error(Errors.Syntax);
            return;
        }

        var scores = new double[(c.ArgCount - 2) / 2];
        for (int i = 2; i < c.ArgCount; i += 2)
        {
            if (!StringCommands.TryParseFloat(c[i], out scores[(i - 2) / 2]))
            {
                c.Error(Errors.NotFloat);
                return;
            }
        }

        RZSet? zset = GetOrCreate(c, () => new RZSet());
        if (zset is null)
        {
            return;
        }

        int added = 0;
        for (int i = 2; i < c.ArgCount; i += 2)
        {
            byte[] member = c[i + 1].ToArray();
            if (!zset.Scores.ContainsKey(member))
            {
                added++;
            }

            zset.Scores[member] = scores[(i - 2) / 2];
        }

        c.Integer(added);
    }

    private static void ZRange(CommandContext c)
    {
        if (!c.TryLong(2, out long start) || !c.TryLong(3, out long stop) || !c.TryGet(c[1], out RZSet? zset))
        {
            return;
        }

        bool withScores = c.ArgCount == 5 && c.ArgIs(4, "withscores");
        var ordered = zset?.Ordered().ToList() ?? [];
        int n = ordered.Count;
        start = start < 0 ? Math.Max(n + start, 0) : start;
        stop = stop < 0 ? n + stop : Math.Min(stop, n - 1);
        if (start > stop || start >= n)
        {
            c.ArrayHeader(0);
            return;
        }

        c.ArrayHeader((stop - start + 1) * (withScores ? 2 : 1));
        for (long i = start; i <= stop; i++)
        {
            c.Bulk(ordered[(int)i].Key);
            if (withScores)
            {
                c.Bulk(Encoding.ASCII.GetBytes(ordered[(int)i].Value.ToString("R", CultureInfo.InvariantCulture)));
            }
        }
    }

    private static void ZScore(CommandContext c)
    {
        if (!c.TryGet(c[1], out RZSet? zset))
        {
            return;
        }

        if (zset is not null && zset.Scores.TryGetValue(c[2].ToArray(), out double score))
        {
            c.Bulk(Encoding.ASCII.GetBytes(score.ToString("R", CultureInfo.InvariantCulture)));
        }
        else
        {
            c.NullBulk();
        }
    }
}
