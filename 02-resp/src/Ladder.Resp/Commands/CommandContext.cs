using System.Buffers;
using System.Text;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Commands;

/// <summary>Per-connection state that outlives a single command.</summary>
public sealed class Session(long id)
{
    public long Id { get; } = id;

    public int Db { get; set; }

    public byte[]? Name { get; set; }

    public bool CloseAfterReply { get; set; }

    public List<byte[][]>? Queued { get; set; }

    public bool TransactionFailed { get; set; }
}

/// <summary>Everything a command handler needs, reused for every command on a connection.</summary>
public sealed class CommandContext(Keyspace keyspace, ServerState server, Session session, CommandArgs args, IBufferWriter<byte> output)
{
    private long _now = -1;

    public Keyspace Keyspace { get; } = keyspace;

    public ServerState Server { get; } = server;

    public Session Session { get; } = session;

    public CommandArgs Args { get; internal set; } = args;

    public IBufferWriter<byte> Out { get; internal set; } = output;

    public CommandSpec Command { get; internal set; } = null!;

    public Database Db => Keyspace[Session.Db];

    /// <summary>One clock reading per command, like Valkey's commandTimeSnapshot, so a command sees a consistent "now".</summary>
    public long Now => _now >= 0 ? _now : (_now = Keyspace.NowMs());

    public int ArgCount => Args.Count;

    public ReadOnlySpan<byte> this[int index] => Args[index];

    internal void BeginCommand() => _now = -1;

    public bool ArgIs(int index, string token) => Ascii.EqualsIgnoreCase(Args[index], token);

    public void Error(ReadOnlySpan<byte> line) => RespWriter.Error(Out, line);

    public void Error(string message) => RespWriter.Error(Out, message);

    public void Ok() => RespWriter.Ok(Out);

    public void Integer(long value) => RespWriter.Integer(Out, value);

    public void Bulk(ReadOnlySpan<byte> value) => RespWriter.Bulk(Out, value);

    public void NullBulk() => RespWriter.NullBulk(Out);

    public void ArrayHeader(long count) => RespWriter.ArrayHeader(Out, count);

    public string ArgString(int index) => Encoding.UTF8.GetString(Args[index]);

    /// <summary>Parses a strict 64-bit integer argument or replies with Valkey's integer error.</summary>
    public bool TryLong(int index, out long value)
    {
        if (RString.TryParseStrict(Args[index], out value))
        {
            return true;
        }

        Error(Errors.NotInteger);
        return false;
    }

    /// <summary>Finds a key of type <typeparamref name="T"/>; replies WRONGTYPE and returns false if it holds another type.</summary>
    public bool TryGet<T>(ReadOnlySpan<byte> key, out T? value)
        where T : class
    {
        Entry? entry = Db.Lookup(key, Now);
        value = null;
        if (entry is null)
        {
            return true;
        }

        if (entry.Value is T typed)
        {
            value = typed;
            return true;
        }

        Error(Errors.WrongType);
        return false;
    }
}

public static class Errors
{
    public static ReadOnlySpan<byte> WrongType => "-WRONGTYPE Operation against a key holding the wrong kind of value\r\n"u8;

    public static ReadOnlySpan<byte> NotInteger => "-ERR value is not an integer or out of range\r\n"u8;

    public static ReadOnlySpan<byte> NotFloat => "-ERR value is not a valid float\r\n"u8;

    public static ReadOnlySpan<byte> Syntax => "-ERR syntax error\r\n"u8;

    public static ReadOnlySpan<byte> Overflow => "-ERR increment or decrement would overflow\r\n"u8;

    public static ReadOnlySpan<byte> NoSuchKey => "-ERR no such key\r\n"u8;

    public static ReadOnlySpan<byte> DbIndex => "-ERR DB index is out of range\r\n"u8;

    public static ReadOnlySpan<byte> TooBig => "-ERR string exceeds maximum allowed size (proto-max-bulk-len)\r\n"u8;
}
