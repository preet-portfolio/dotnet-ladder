namespace Ladder.Resp.Storage;

/// <summary>Minimal list, set, hash and sorted-set values: enough for type checks, COPY and the commands that create them.</summary>
public sealed class RList
{
    public List<byte[]> Items { get; } = [];

    public RList Clone()
    {
        var c = new RList();
        c.Items.AddRange(Items);
        return c;
    }
}

public sealed class RSet
{
    public HashSet<byte[]> Members { get; } = new(ByteKeyComparer.Instance);

    public RSet Clone()
    {
        var c = new RSet();
        c.Members.UnionWith(Members);
        return c;
    }
}

public sealed class RHash
{
    public Dictionary<byte[], byte[]> Fields { get; } = new(ByteKeyComparer.Instance);

    public RHash Clone()
    {
        var c = new RHash();
        foreach (var kv in Fields)
        {
            c.Fields[kv.Key] = kv.Value;
        }

        return c;
    }
}

public sealed class RZSet
{
    public Dictionary<byte[], double> Scores { get; } = new(ByteKeyComparer.Instance);

    public IEnumerable<KeyValuePair<byte[], double>> Ordered() =>
        Scores.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, ByteOrder.Instance);

    public RZSet Clone()
    {
        var c = new RZSet();
        foreach (var kv in Scores)
        {
            c.Scores[kv.Key] = kv.Value;
        }

        return c;
    }
}

/// <summary>Lexicographic byte order (memcmp), the order Valkey uses for equal scores and sorted replies.</summary>
public sealed class ByteOrder : IComparer<byte[]>
{
    public static readonly ByteOrder Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
