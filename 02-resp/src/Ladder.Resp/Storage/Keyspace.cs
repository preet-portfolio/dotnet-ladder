namespace Ladder.Resp.Storage;

/// <summary>
/// All databases plus the one lock every command runs under. Commands execute one at a time, as in Valkey's
/// single-threaded model; connections parse and write in parallel outside the lock.
/// </summary>
public sealed class Keyspace
{
    public const int DatabaseCount = 16;

    private readonly Database[] _databases = new Database[DatabaseCount];

    public Keyspace()
    {
        for (int i = 0; i < _databases.Length; i++)
        {
            _databases[i] = new Database();
        }
    }

    public Lock Sync { get; } = new();

    public Random Random { get; } = new();

    public Database this[int index] => _databases[index];

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long ExpiredKeys => _databases.Sum(db => db.ExpiredKeys);

    public void ResetStats()
    {
        foreach (Database db in _databases)
        {
            db.ExpiredKeys = 0;
        }
    }

    public void Swap(int a, int b) => (_databases[a], _databases[b]) = (_databases[b], _databases[a]);

    public void ClearAll()
    {
        foreach (Database db in _databases)
        {
            db.Clear();
        }
    }

    /// <summary>One active-expiry pass over every database with a 25 ms budget (Valkey's ACTIVE_EXPIRE_CYCLE_SLOW_TIME_PERC of a 100 ms tick).</summary>
    public int ActiveExpireCycle()
    {
        lock (Sync)
        {
            long now = NowMs();
            long deadline = Environment.TickCount64 + 25;
            int deleted = 0;
            foreach (Database db in _databases)
            {
                deleted += db.ActiveExpireCycle(now, Random, deadline);
            }

            return deleted;
        }
    }
}
