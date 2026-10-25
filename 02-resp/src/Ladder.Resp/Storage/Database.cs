namespace Ladder.Resp.Storage;

public sealed class Entry(object value)
{
    public object Value { get; set; } = value;

    /// <summary>Absolute expiry in Unix milliseconds, or -1 when the key does not expire.</summary>
    public long ExpiresAt { get; set; } = -1;
}

/// <summary>
/// One numbered database. Keys expire lazily (when touched) and actively (a sampling cycle, like Valkey's
/// activeExpireCycle). Every method must be called with <see cref="Keyspace.Sync"/> held.
/// </summary>
public sealed class Database
{
    private readonly Dictionary<byte[], Entry> _map = new(ByteKeyComparer.Instance);
    private readonly Dictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> _lookup;

    // Keys with a TTL, indexable so the active cycle can sample at random, with O(1) removal.
    private readonly List<byte[]> _volatile = [];
    private readonly Dictionary<byte[], int> _volatileIndex = new(ByteKeyComparer.Instance);

    // SCAN works from a snapshot sorted by a stable per-key hash; any write invalidates it.
    private (ulong Hash, byte[] Key)[]? _scanOrder;

    public Database() => _lookup = _map.GetAlternateLookup<ReadOnlySpan<byte>>();

    public int Count => _map.Count;

    public int VolatileCount => _volatile.Count;

    /// <summary>Keys removed because their TTL passed (INFO stats expired_keys, summed over databases).</summary>
    public long ExpiredKeys { get; set; }

    public IEnumerable<byte[]> Keys => _map.Keys;

    /// <summary>Finds a live key; an expired key is deleted on the way and reported as missing.</summary>
    public Entry? Lookup(ReadOnlySpan<byte> key, long now)
    {
        if (!_lookup.TryGetValue(key, out byte[]? actual, out Entry? entry))
        {
            return null;
        }

        if (entry.ExpiresAt >= 0 && entry.ExpiresAt <= now)
        {
            Remove(actual);
            ExpiredKeys++;
            return null;
        }

        return entry;
    }

    public Entry? Lookup(ReadOnlySpan<byte> key, long now, out byte[]? actualKey)
    {
        Entry? entry = Lookup(key, now);
        actualKey = null;
        if (entry is not null)
        {
            _lookup.TryGetValue(key, out actualKey, out _);
        }

        return entry;
    }

    /// <summary>
    /// Stores a value. The TTL is replaced by <paramref name="expireAt"/> when it is set (&gt;= 0), kept when
    /// <paramref name="keepTtl"/> is true, and cleared otherwise (plain SET semantics).
    /// </summary>
    public Entry Upsert(ReadOnlySpan<byte> key, object value, long now, bool keepTtl = false, long expireAt = -1)
    {
        if (_lookup.TryGetValue(key, out byte[]? actual, out Entry? entry))
        {
            if (entry.ExpiresAt >= 0 && entry.ExpiresAt <= now)
            {
                // An expired key is a missing key: it does not keep its old TTL.
                entry.ExpiresAt = -1;
                UntrackVolatile(actual);
            }

            entry.Value = value;
            if (expireAt >= 0)
            {
                SetExpire(actual, entry, expireAt);
            }
            else if (!keepTtl && entry.ExpiresAt >= 0)
            {
                SetExpire(actual, entry, -1);
            }

            return entry;
        }

        actual = key.ToArray();
        entry = new Entry(value);
        _map.Add(actual, entry);
        _scanOrder = null;
        if (expireAt >= 0)
        {
            SetExpire(actual, entry, expireAt);
        }

        return entry;
    }

    /// <summary>Sets (at &gt;= 0) or removes (at &lt; 0) the TTL of a live key; false when the key does not exist.</summary>
    public bool SetExpire(ReadOnlySpan<byte> key, long now, long at)
    {
        if (Lookup(key, now) is null || !_lookup.TryGetValue(key, out byte[]? actual, out Entry? entry))
        {
            return false;
        }

        SetExpire(actual, entry, at);
        return true;
    }

    public void Add(byte[] key, Entry entry)
    {
        _map.Add(key, entry);
        if (entry.ExpiresAt >= 0)
        {
            TrackVolatile(key);
        }

        _scanOrder = null;
    }

    public bool Delete(ReadOnlySpan<byte> key, long now)
    {
        if (!_lookup.TryGetValue(key, out byte[]? actual, out Entry? entry))
        {
            return false;
        }

        bool live = entry.ExpiresAt < 0 || entry.ExpiresAt > now;
        Remove(actual);
        if (!live)
        {
            ExpiredKeys++;
        }

        return live;
    }

    /// <summary>Deletes a key because an explicit expiry time is already in the past; counts as an expired key.</summary>
    public void ExpireNow(ReadOnlySpan<byte> key)
    {
        if (_lookup.TryGetValue(key, out byte[]? actual, out _))
        {
            Remove(actual);
            ExpiredKeys++;
        }
    }

    public void SetExpire(byte[] key, Entry entry, long at)
    {
        entry.ExpiresAt = at;
        if (at >= 0)
        {
            TrackVolatile(key);
        }
        else
        {
            UntrackVolatile(key);
        }
    }

    public void Clear()
    {
        _map.Clear();
        _volatile.Clear();
        _volatileIndex.Clear();
        _scanOrder = null;
    }

    /// <summary>Removes and returns the entry so it can be moved to another database.</summary>
    public Entry? Detach(ReadOnlySpan<byte> key, out byte[]? actualKey)
    {
        if (!_lookup.TryGetValue(key, out actualKey, out Entry? entry))
        {
            return null;
        }

        Remove(actualKey);
        return entry;
    }

    /// <summary>
    /// Valkey's active expiry: sample up to 20 volatile keys, delete the expired ones, and repeat while more than
    /// a quarter of a sample was expired and the time budget lasts. Returns the number of keys deleted.
    /// </summary>
    public int ActiveExpireCycle(long now, Random random, long deadlineTicks)
    {
        int deleted = 0;
        while (_volatile.Count > 0)
        {
            int sample = Math.Min(20, _volatile.Count);
            int expired = 0;
            for (int i = 0; i < sample && _volatile.Count > 0; i++)
            {
                byte[] key = _volatile[random.Next(_volatile.Count)];
                Entry entry = _map[key];
                if (entry.ExpiresAt <= now)
                {
                    Remove(key);
                    expired++;
                    ExpiredKeys++;
                }
            }

            deleted += expired;
            if (expired * 4 <= sample || Environment.TickCount64 >= deadlineTicks)
            {
                break;
            }
        }

        return deleted;
    }

    /// <summary>
    /// One SCAN step. The cursor is a position in stable-hash order, so a key present for the whole iteration is
    /// returned at least once even if other keys are added or removed between calls (the guarantee SCAN gives).
    /// </summary>
    public ulong Scan(ulong cursor, int count, List<byte[]> output)
    {
        _scanOrder ??= BuildScanOrder();
        int i = LowerBound(_scanOrder, cursor);
        int taken = 0;
        while (i < _scanOrder.Length && (taken < count || (i > 0 && _scanOrder[i].Hash == _scanOrder[i - 1].Hash)))
        {
            output.Add(_scanOrder[i].Key);
            taken++;
            i++;
        }

        return i >= _scanOrder.Length ? 0 : _scanOrder[i].Hash;
    }

    public byte[]? RandomKey(Random random, long now)
    {
        // Retries skip keys that turn out to be expired, as Valkey does.
        for (int attempt = 0; attempt < 100 && _map.Count > 0; attempt++)
        {
            byte[] key = _map.Keys.ElementAt(random.Next(_map.Count));
            if (Lookup(key, now) is not null)
            {
                return key;
            }
        }

        return null;
    }

    private (ulong, byte[])[] BuildScanOrder()
    {
        var order = new (ulong Hash, byte[] Key)[_map.Count];
        int i = 0;
        foreach (byte[] key in _map.Keys)
        {
            order[i++] = (StableHash(key), key);
        }

        Array.Sort(order, (a, b) => a.Hash != b.Hash ? a.Hash.CompareTo(b.Hash) : a.Key.AsSpan().SequenceCompareTo(b.Key));
        return order;
    }

    private static int LowerBound((ulong Hash, byte[] Key)[] order, ulong cursor)
    {
        int lo = 0, hi = order.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (order[mid].Hash < cursor)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>FNV-1a, folded into [1, 2^62] so that 0 stays free to mean "iteration finished".</summary>
    private static ulong StableHash(ReadOnlySpan<byte> key)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in key)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        return (h >> 2) + 1;
    }

    private void Remove(byte[] key)
    {
        _map.Remove(key);
        UntrackVolatile(key);
        _scanOrder = null;
    }

    private void TrackVolatile(byte[] key)
    {
        if (!_volatileIndex.ContainsKey(key))
        {
            _volatileIndex[key] = _volatile.Count;
            _volatile.Add(key);
        }
    }

    private void UntrackVolatile(byte[] key)
    {
        if (!_volatileIndex.Remove(key, out int index))
        {
            return;
        }

        int last = _volatile.Count - 1;
        if (index != last)
        {
            byte[] moved = _volatile[last];
            _volatile[index] = moved;
            _volatileIndex[moved] = index;
        }

        _volatile.RemoveAt(last);
    }
}
