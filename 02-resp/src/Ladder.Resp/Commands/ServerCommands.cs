using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

/// <summary>Server-wide state: configuration, connected clients and counters reported by INFO.</summary>
public sealed class ServerState
{
    private long _nextClientId;

    public ServerState(int port)
    {
        Config["port"] = port.ToString(CultureInfo.InvariantCulture);
    }

    public long StartedAt { get; } = Environment.TickCount64;

    public ConcurrentDictionary<long, Session> Clients { get; } = new();

    /// <summary>
    /// CONFIG GET/SET values. Only a few change behaviour here; the rest are stored and echoed back so clients
    /// and test harnesses that save and restore settings keep working.
    /// </summary>
    public Dictionary<string, string> Config { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appendonly"] = "no",
        ["save"] = string.Empty,
        ["databases"] = "16",
        ["maxmemory"] = "0",
        ["maxmemory-policy"] = "noeviction",
        ["proto-max-bulk-len"] = "536870912",
        ["lazyfree-lazy-user-flush"] = "no",
        ["lazyfree-lazy-user-del"] = "no",
        ["lazyfree-lazy-expire"] = "no",
        ["lazyfree-lazy-server-del"] = "no",
        ["hz"] = "10",
        ["active-expire-effort"] = "1",
        ["list-max-listpack-size"] = "-2",
        ["set-max-intset-entries"] = "512",
        ["set-max-listpack-entries"] = "128",
        ["hash-max-listpack-entries"] = "128",
        ["zset-max-listpack-entries"] = "128",
        ["enable-debug-command"] = "no",
    };

    public long NewClientId() => Interlocked.Increment(ref _nextClientId);

    /// <summary>Valkey still accepts the pre-7.0 "ziplist" names as aliases of the "listpack" settings.</summary>
    public static string CanonicalConfigName(string name) => name.Replace("-ziplist-", "-listpack-", StringComparison.OrdinalIgnoreCase);

    public int ConfigInt(string name, int fallback) =>
        Config.TryGetValue(name, out string? text) && int.TryParse(text, CultureInfo.InvariantCulture, out int value) ? value : fallback;
}

public static class ServerCommands
{
    internal static void Register(Action<CommandSpec> add)
    {
        add(new("ping", -1, CommandTraits.None, Ping));
        add(new("echo", 2, CommandTraits.None, c => c.Bulk(c[1])));
        add(new("select", 2, CommandTraits.None, Select));
        add(new("flushdb", -1, CommandTraits.Write, FlushDb));
        add(new("flushall", -1, CommandTraits.Write, FlushAll));
        add(new("swapdb", 3, CommandTraits.Write, SwapDb));
        add(new("time", 1, CommandTraits.None, Time));
        add(new("info", -1, CommandTraits.None, Info));
        add(new("config", -2, CommandTraits.None, ConfigCommand));
        add(new("client", -2, CommandTraits.NoTransaction, ClientCommand));
        add(new("hello", -1, CommandTraits.NoTransaction, Hello));
        add(new("quit", -1, CommandTraits.NoTransaction, Quit));
        add(new("reset", 1, CommandTraits.NoTransaction, Reset));
        add(new("command", -1, CommandTraits.None, Command));
        add(new("function", -2, CommandTraits.None, Function));
        add(new("memory", -2, CommandTraits.None, Memory));
        add(new("debug", -2, CommandTraits.None, c => c.Error("ERR DEBUG command not allowed. If the enable-debug-command option is set to \"local\", you can run it from a local connection, otherwise you need to set this option in the configuration file, and then restart the server.")));
        add(new("multi", 1, CommandTraits.NoTransaction, Multi));
        add(new("exec", 1, CommandTraits.NoTransaction, Exec));
        add(new("discard", 1, CommandTraits.NoTransaction, Discard));
    }

    private static void Ping(CommandContext c)
    {
        if (c.ArgCount > 2)
        {
            c.Error($"ERR wrong number of arguments for '{c.Command.Name}' command");
        }
        else if (c.ArgCount == 2)
        {
            c.Bulk(c[1]);
        }
        else
        {
            RespWriter.Simple(c.Out, "PONG"u8);
        }
    }

    private static void Select(CommandContext c)
    {
        if (KeyCommands.TryDbIndex(c, 1, out int index))
        {
            c.Session.Db = index;
            c.Ok();
        }
    }

    private static void FlushDb(CommandContext c)
    {
        if (!TryFlushMode(c))
        {
            return;
        }

        c.Db.Clear();
        c.Ok();
    }

    private static void FlushAll(CommandContext c)
    {
        if (!TryFlushMode(c))
        {
            return;
        }

        c.Keyspace.ClearAll();
        c.Ok();
    }

    private static bool TryFlushMode(CommandContext c)
    {
        if (c.ArgCount == 1 || (c.ArgCount == 2 && (c.ArgIs(1, "sync") || c.ArgIs(1, "async"))))
        {
            return true;
        }

        c.Error(Errors.Syntax);
        return false;
    }

    private static void SwapDb(CommandContext c)
    {
        if (!RString.TryParseStrict(c[1], out long a) || a < int.MinValue || a > int.MaxValue)
        {
            c.Error("ERR invalid first DB index");
            return;
        }

        if (!RString.TryParseStrict(c[2], out long b) || b < int.MinValue || b > int.MaxValue)
        {
            c.Error("ERR invalid second DB index");
            return;
        }

        if (a < 0 || a >= Keyspace.DatabaseCount || b < 0 || b >= Keyspace.DatabaseCount)
        {
            c.Error(Errors.DbIndex);
            return;
        }

        c.Keyspace.Swap((int)a, (int)b);
        c.Ok();
    }

    private static void Time(CommandContext c)
    {
        long micros = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        c.ArrayHeader(2);
        c.Bulk(Encoding.ASCII.GetBytes((micros / 1_000_000).ToString(CultureInfo.InvariantCulture)));
        c.Bulk(Encoding.ASCII.GetBytes((micros % 1_000_000).ToString(CultureInfo.InvariantCulture)));
    }

    private static void Info(CommandContext c)
    {
        var sb = new StringBuilder();
        sb.Append("# Server\r\nredis_version:7.2.4\r\nvalkey_version:9.1.2\r\nserver_name:ladder-resp\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"process_id:{Environment.ProcessId}\r\ntcp_port:{c.Server.Config["port"]}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"uptime_in_seconds:{(Environment.TickCount64 - c.Server.StartedAt) / 1000}\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"# Clients\r\nconnected_clients:{c.Server.Clients.Count}\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"# Memory\r\nused_memory:{GC.GetTotalMemory(false)}\r\nmaxmemory:0\r\n\r\n");
        sb.Append("# Persistence\r\nloading:0\r\nrdb_bgsave_in_progress:0\r\naof_rewrite_in_progress:0\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"# Stats\r\nexpired_keys:{c.Keyspace.ExpiredKeys}\r\n\r\n");
        sb.Append("# Keyspace\r\n");
        for (int i = 0; i < Keyspace.DatabaseCount; i++)
        {
            Database db = c.Keyspace[i];
            if (db.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"db{i}:keys={db.Count},expires={db.VolatileCount},avg_ttl=0,keys_with_volatile_items=0\r\n");
            }
        }

        c.Bulk(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void ConfigCommand(CommandContext c)
    {
        if (c.ArgIs(1, "get") && c.ArgCount >= 3)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            for (int i = 2; i < c.ArgCount; i++)
            {
                byte[] pattern = Encoding.ASCII.GetBytes(ServerState.CanonicalConfigName(c.ArgString(i)));
                Ascii.ToLowerInPlace(pattern, out _);
                foreach (var kv in c.Server.Config)
                {
                    if (GlobMatcher.IsMatch(pattern, Encoding.ASCII.GetBytes(kv.Key)) && !pairs.Contains(kv))
                    {
                        pairs.Add(kv);
                    }
                }
            }

            c.ArrayHeader(pairs.Count * 2);
            foreach (var kv in pairs)
            {
                c.Bulk(Encoding.UTF8.GetBytes(kv.Key));
                c.Bulk(Encoding.UTF8.GetBytes(kv.Value));
            }
        }
        else if (c.ArgIs(1, "set") && c.ArgCount >= 4 && c.ArgCount % 2 == 0)
        {
            for (int i = 2; i < c.ArgCount; i += 2)
            {
                c.Server.Config[ServerState.CanonicalConfigName(c.ArgString(i))] = c.ArgString(i + 1);
            }

            c.Ok();
        }
        else if (c.ArgIs(1, "resetstat"))
        {
            c.Keyspace.ResetStats();
            c.Ok();
        }
        else if (c.ArgIs(1, "rewrite"))
        {
            c.Ok();
        }
        else
        {
            c.Error($"ERR unknown subcommand or wrong number of arguments for '{c.ArgString(1)}'. Try CONFIG HELP.");
        }
    }

    private static void ClientCommand(CommandContext c)
    {
        if (c.ArgIs(1, "id"))
        {
            c.Integer(c.Session.Id);
        }
        else if (c.ArgIs(1, "setname") && c.ArgCount == 3)
        {
            c.Session.Name = c[2].IsEmpty ? null : c[2].ToArray();
            c.Ok();
        }
        else if (c.ArgIs(1, "getname"))
        {
            if (c.Session.Name is null)
            {
                c.NullBulk();
            }
            else
            {
                c.Bulk(c.Session.Name);
            }
        }
        else if (c.ArgIs(1, "info"))
        {
            c.Bulk(Encoding.UTF8.GetBytes(ClientLine(c.Session) + "\n"));
        }
        else if (c.ArgIs(1, "list"))
        {
            var sb = new StringBuilder();
            foreach (Session s in c.Server.Clients.Values.OrderBy(s => s.Id))
            {
                sb.Append(ClientLine(s)).Append('\n');
            }

            c.Bulk(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        else if (c.ArgIs(1, "setinfo") || c.ArgIs(1, "no-evict") || c.ArgIs(1, "no-touch") || c.ArgIs(1, "reply"))
        {
            c.Ok();
        }
        else
        {
            c.Error($"ERR unknown subcommand or wrong number of arguments for '{c.ArgString(1)}'. Try CLIENT HELP.");
        }
    }

    private static string ClientLine(Session s) =>
        string.Create(CultureInfo.InvariantCulture, $"id={s.Id} name={(s.Name is null ? string.Empty : Encoding.UTF8.GetString(s.Name))} db={s.Db} cmd=client|info");

    private static void Hello(CommandContext c)
    {
        if (c.ArgCount >= 2 && !c.ArgIs(1, "2"))
        {
            c.Error(c.ArgIs(1, "3") ? "NOPROTO this server does not support RESP3" : "NOPROTO unsupported protocol version");
            return;
        }

        c.ArrayHeader(14);
        c.Bulk("server"u8);
        c.Bulk("valkey"u8);
        c.Bulk("version"u8);
        c.Bulk("9.1.2"u8);
        c.Bulk("proto"u8);
        c.Integer(2);
        c.Bulk("id"u8);
        c.Integer(c.Session.Id);
        c.Bulk("mode"u8);
        c.Bulk("standalone"u8);
        c.Bulk("role"u8);
        c.Bulk("master"u8);
        c.Bulk("modules"u8);
        c.ArrayHeader(0);
    }

    private static void Quit(CommandContext c)
    {
        c.Session.CloseAfterReply = true;
        c.Ok();
    }

    private static void Reset(CommandContext c)
    {
        c.Session.Db = 0;
        c.Session.Name = null;
        c.Session.Queued = null;
        c.Session.TransactionFailed = false;
        RespWriter.Simple(c.Out, "RESET"u8);
    }

    private static void Command(CommandContext c)
    {
        if (c.ArgCount == 2 && c.ArgIs(1, "count"))
        {
            c.Integer(CommandTable.Count);
            return;
        }

        // Clients such as valkey-cli call COMMAND DOCS on start-up for hints; an empty reply is valid.
        c.ArrayHeader(0);
    }

    private static void Function(CommandContext c)
    {
        if (c.ArgIs(1, "flush"))
        {
            c.Ok();
        }
        else
        {
            c.Error("ERR FUNCTION is not supported by this server");
        }
    }

    private static void Memory(CommandContext c)
    {
        if (!c.ArgIs(1, "usage") || c.ArgCount < 3)
        {
            c.Error($"ERR unknown subcommand or wrong number of arguments for '{c.ArgString(1)}'. Try MEMORY HELP.");
            return;
        }

        Entry? entry = c.Db.Lookup(c[2], c.Now);
        if (entry is null)
        {
            c.NullBulk();
        }
        else
        {
            c.Integer(c[2].Length + (entry.Value is RString s ? s.Length : 64) + 56);
        }
    }

    private static void Multi(CommandContext c)
    {
        if (c.Session.Queued is not null)
        {
            c.Error("ERR MULTI calls can not be nested");
            return;
        }

        c.Session.Queued = [];
        c.Session.TransactionFailed = false;
        c.Ok();
    }

    private static void Discard(CommandContext c)
    {
        if (c.Session.Queued is null)
        {
            c.Error("ERR DISCARD without MULTI");
            return;
        }

        c.Session.Queued = null;
        c.Ok();
    }

    /// <summary>
    /// Runs the queued commands back to back. The keyspace lock is re-entrant, so holding it for the whole
    /// EXEC keeps other clients out between the queued commands, which is what makes the transaction atomic.
    /// </summary>
    private static void Exec(CommandContext c)
    {
        if (c.Session.Queued is not { } queued)
        {
            c.Error("ERR EXEC without MULTI");
            return;
        }

        c.Session.Queued = null;
        if (c.Session.TransactionFailed)
        {
            c.Session.TransactionFailed = false;
            c.Error("EXECABORT Transaction discarded because of previous errors.");
            return;
        }

        c.ArrayHeader(queued.Count);
        CommandArgs saved = c.Args;
        var args = new CommandArgs();
        c.Args = args;
        foreach (byte[][] command in queued)
        {
            args.Reset();
            foreach (byte[] arg in command)
            {
                args.Add(arg);
            }

            CommandTable.Execute(c);
        }

        c.Args = saved;
    }
}
