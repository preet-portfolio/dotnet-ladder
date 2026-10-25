using System.Buffers;
using System.Text;
using Ladder.Resp.Commands;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Tests;

public class CommandTests
{
    private readonly Harness _h = new();

    [Fact]
    public void Set_and_get_round_trip()
    {
        Assert.Equal("+OK\r\n", _h.Run("SET", "k", "v"));
        Assert.Equal("$1\r\nv\r\n", _h.Run("get", "k"));
        Assert.Equal("$-1\r\n", _h.Run("GET", "missing"));
    }

    [Fact]
    public void Set_options_follow_valkey()
    {
        Assert.Equal("$-1\r\n", _h.Run("SET", "k", "v", "XX"));
        Assert.Equal("+OK\r\n", _h.Run("SET", "k", "v", "NX"));
        Assert.Equal("$-1\r\n", _h.Run("SET", "k", "w", "NX"));
        Assert.Equal("$1\r\nv\r\n", _h.Run("SET", "k", "w", "GET"));
        Assert.Equal("$-1\r\n", _h.Run("SET", "k", "x", "IFEQ", "nope"));
        Assert.Equal("+OK\r\n", _h.Run("SET", "k", "x", "IFEQ", "w"));
        Assert.Equal("-ERR syntax error\r\n", _h.Run("SET", "k", "v", "NX", "XX"));
        Assert.Equal("-ERR syntax error\r\n", _h.Run("SET", "k", "v", "EX", "10", "PX", "10"));
        Assert.Equal("-ERR invalid expire time in 'set' command\r\n", _h.Run("SET", "k", "v", "EX", "0"));
        Assert.Equal("-ERR value is not an integer or out of range\r\n", _h.Run("SET", "k", "v", "EX", "1.5"));
    }

    [Fact]
    public void Set_with_past_expiry_deletes_existing_key()
    {
        _h.Run("SET", "k", "v");
        Assert.Equal("+OK\r\n", _h.Run("SET", "k", "v", "PXAT", "1"));
        Assert.Equal(":0\r\n", _h.Run("EXISTS", "k"));
    }

    [Fact]
    public void Incr_family_and_errors()
    {
        Assert.Equal(":1\r\n", _h.Run("INCR", "n"));
        Assert.Equal(":11\r\n", _h.Run("INCRBY", "n", "10"));
        Assert.Equal(":6\r\n", _h.Run("DECRBY", "n", "5"));
        Assert.Equal("$3\r\nint\r\n", _h.Run("OBJECT", "ENCODING", "n"));
        _h.Run("SET", "big", "9223372036854775807");
        Assert.Equal("-ERR increment or decrement would overflow\r\n", _h.Run("INCR", "big"));
        _h.Run("SET", "s", "abc");
        Assert.Equal("-ERR value is not an integer or out of range\r\n", _h.Run("INCR", "s"));
        Assert.Equal("-ERR decrement would overflow\r\n", _h.Run("DECRBY", "n", "-9223372036854775808"));
        _h.Run("RPUSH", "l", "a");
        Assert.Equal("-WRONGTYPE Operation against a key holding the wrong kind of value\r\n", _h.Run("INCR", "l"));
    }

    [Fact]
    public void Incrbyfloat_formats_like_ld2string()
    {
        Assert.Equal("$4\r\n10.5\r\n", _h.Run("INCRBYFLOAT", "f", "10.5"));
        Assert.Equal("$20\r\n10.59999999999999964\r\n", _h.Run("INCRBYFLOAT", "f", "0.1"));
        Assert.Equal("-ERR value is not a valid float\r\n", _h.Run("INCRBYFLOAT", "f", " 1"));
        Assert.Equal("-ERR value is not a valid float\r\n", _h.Run("INCRBYFLOAT", "g", "1e400"));
        Assert.Equal("-ERR increment would produce NaN or Infinity\r\n", _h.Run("INCRBYFLOAT", "g", "inf"));
    }

    [Fact]
    public void Range_commands()
    {
        _h.Run("SET", "k", "Hello World");
        Assert.Equal("$5\r\nHello\r\n", _h.Run("GETRANGE", "k", "0", "4"));
        Assert.Equal("$5\r\nWorld\r\n", _h.Run("GETRANGE", "k", "-5", "-1"));
        Assert.Equal("$0\r\n\r\n", _h.Run("GETRANGE", "k", "-1", "-5"));
        Assert.Equal(":11\r\n", _h.Run("SETRANGE", "k", "6", "Redis"));
        Assert.Equal("$11\r\nHello Redis\r\n", _h.Run("GET", "k"));
        Assert.Equal(":8\r\n", _h.Run("SETRANGE", "z", "5", "abc"));
        Assert.Equal("$8\r\n\0\0\0\0\0abc\r\n", _h.Run("GET", "z"));
        Assert.Equal(":16\r\n", _h.Run("APPEND", "k", "!!!!!"));
        Assert.Equal(":16\r\n", _h.Run("STRLEN", "k"));
    }

    [Fact]
    public void Setrange_rejects_offsets_that_would_overflow()
    {
        Assert.Equal("-ERR string exceeds maximum allowed size (proto-max-bulk-len)\r\n", _h.Run("SETRANGE", "k", "9223372036854775807", "A"));
        _h.Run("SET", "k", "v");
        Assert.Equal("-ERR string exceeds maximum allowed size (proto-max-bulk-len)\r\n", _h.Run("SETRANGE", "k", "9223372036854775807", "A"));
    }

    [Fact]
    public void Lcs_matches_the_valkey_documentation_example()
    {
        _h.Run("MSET", "key1", "ohmytext", "key2", "mynewtext");
        Assert.Equal("$6\r\nmytext\r\n", _h.Run("LCS", "key1", "key2"));
        Assert.Equal(":6\r\n", _h.Run("LCS", "key1", "key2", "LEN"));
        Assert.Equal(
            "*4\r\n$7\r\nmatches\r\n*2\r\n*3\r\n*2\r\n:4\r\n:7\r\n*2\r\n:5\r\n:8\r\n:4\r\n*3\r\n*2\r\n:2\r\n:3\r\n*2\r\n:0\r\n:1\r\n:2\r\n$3\r\nlen\r\n:6\r\n",
            _h.Run("LCS", "key1", "key2", "IDX", "WITHMATCHLEN"));
    }

    [Fact]
    public void Bit_commands()
    {
        Assert.Equal(":0\r\n", _h.Run("SETBIT", "b", "7", "1"));
        Assert.Equal(":1\r\n", _h.Run("GETBIT", "b", "7"));
        Assert.Equal("$1\r\n\u0001\r\n", _h.Run("GET", "b"));
        Assert.Equal("-ERR bit is not an integer or out of range\r\n", _h.Run("SETBIT", "b", "1", "2"));
        Assert.Equal("-ERR bit offset is not an integer or out of range\r\n", _h.Run("SETBIT", "b", "-1", "1"));
    }

    [Fact]
    public void Multi_key_commands()
    {
        Assert.Equal("+OK\r\n", _h.Run("MSET", "a", "1", "b", "2"));
        Assert.Equal("*3\r\n$1\r\n1\r\n$1\r\n2\r\n$-1\r\n", _h.Run("MGET", "a", "b", "c"));
        Assert.Equal(":0\r\n", _h.Run("MSETNX", "c", "3", "a", "x"));
        Assert.Equal(":1\r\n", _h.Run("MSETEX", "2", "c", "3", "d", "4", "EX", "100"));
        Assert.Equal(":100\r\n", _h.Run("TTL", "d"));
        Assert.Equal("-ERR invalid numkeys value or out of range\r\n", _h.Run("MSETEX", "0", "k", "v"));
        Assert.Equal("-ERR wrong number of arguments for 'mset' command\r\n", _h.Run("MSET", "a", "1", "b"));
    }

    [Fact]
    public void Expire_options()
    {
        _h.Run("SET", "k", "v");
        Assert.Equal(":0\r\n", _h.Run("EXPIRE", "k", "100", "XX"));
        Assert.Equal(":1\r\n", _h.Run("EXPIRE", "k", "100", "NX"));
        Assert.Equal(":0\r\n", _h.Run("EXPIRE", "k", "50", "GT"));
        Assert.Equal(":1\r\n", _h.Run("EXPIRE", "k", "50", "LT"));
        Assert.Equal(":50\r\n", _h.Run("TTL", "k"));
        Assert.Equal(":1\r\n", _h.Run("PERSIST", "k"));
        Assert.Equal(":-1\r\n", _h.Run("TTL", "k"));
        Assert.Equal(":-2\r\n", _h.Run("TTL", "nokey"));
        Assert.Equal("-ERR NX and XX, GT or LT options at the same time are not compatible\r\n", _h.Run("EXPIRE", "k", "1", "NX", "GT"));
        Assert.Equal("-ERR Unsupported option foo\r\n", _h.Run("EXPIRE", "k", "1", "foo"));
        Assert.Equal(":1\r\n", _h.Run("EXPIRE", "k", "-1"));
        Assert.Equal(":0\r\n", _h.Run("EXISTS", "k"));
    }

    [Fact]
    public void Keyspace_commands()
    {
        _h.Run("SET", "a", "1");
        _h.Run("SET", "b", "2");
        Assert.Equal("+OK\r\n", _h.Run("RENAME", "a", "c"));
        Assert.Equal("-ERR no such key\r\n", _h.Run("RENAME", "a", "d"));
        Assert.Equal(":0\r\n", _h.Run("RENAMENX", "b", "c"));
        Assert.Equal(":1\r\n", _h.Run("COPY", "c", "e"));
        Assert.Equal(":1\r\n", _h.Run("MOVE", "e", "3"));
        Assert.Equal("+OK\r\n", _h.Run("SELECT", "3"));
        Assert.Equal("$1\r\n1\r\n", _h.Run("GET", "e"));
        Assert.Equal("-ERR DB index is out of range\r\n", _h.Run("SELECT", "16"));
        Assert.Equal("+string\r\n", _h.Run("TYPE", "e"));
        Assert.Equal("+none\r\n", _h.Run("TYPE", "zz"));
        Assert.Equal(":1\r\n", _h.Run("DBSIZE"));
        Assert.Equal("*1\r\n$1\r\ne\r\n", _h.Run("KEYS", "*"));
    }

    [Fact]
    public void Unknown_command_and_arity_errors()
    {
        Assert.Equal("-ERR unknown command 'nope', with args beginning with: 'a' 'b' \r\n", _h.Run("nope", "a", "b"));
        Assert.Equal("-ERR wrong number of arguments for 'get' command\r\n", _h.Run("GET"));
    }

    [Fact]
    public void Multi_exec_queues_and_runs()
    {
        Assert.Equal("+OK\r\n", _h.Run("MULTI"));
        Assert.Equal("+QUEUED\r\n", _h.Run("INCR", "n"));
        Assert.Equal("+QUEUED\r\n", _h.Run("INCR", "n"));
        Assert.Equal("*2\r\n:1\r\n:2\r\n", _h.Run("EXEC"));
        _h.Run("MULTI");
        _h.Run("GET");
        Assert.Equal("-EXECABORT Transaction discarded because of previous errors.\r\n", _h.Run("EXEC"));
    }

    [Fact]
    public void Get_and_incr_on_existing_keys_allocate_nothing()
    {
        var keyspace = new Keyspace();
        var output = new ArrayBufferWriter<byte>(4096);
        var args = new CommandArgs();
        var context = new CommandContext(keyspace, new ServerState(0), new Session(1), args, output);
        keyspace[0].Upsert("key"u8, RString.FromValue("some value"u8), 0);
        keyspace[0].Upsert("counter"u8, RString.FromInt(0), 0);
        ReadOnlyMemory<byte> get = "GET"u8.ToArray(), incr = "INCR"u8.ToArray(), key = "key"u8.ToArray(), counter = "counter"u8.ToArray();

        void RunBoth()
        {
            output.ResetWrittenCount();
            args.Reset();
            args.Add(get);
            args.Add(key);
            CommandTable.Execute(context);
            args.Reset();
            args.Add(incr);
            args.Add(counter);
            CommandTable.Execute(context);
        }

        RunBoth();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            RunBoth();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal("$10\r\nsome value\r\n:1001\r\n", Encoding.ASCII.GetString(output.WrittenSpan));
    }
}
