using System.Buffers;
using System.Text;
using CsCheck;

namespace Ladder.Resp.Tests;

public class ParserTests
{
    [Fact]
    public void Parses_a_multibulk_request()
    {
        var args = Parse("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$5\r\nhello\r\n", out ParseStatus status, out int consumed);
        Assert.Equal(ParseStatus.Complete, status);
        Assert.Equal(["SET", "k", "hello"], args);
        Assert.Equal(31, consumed);
    }

    [Fact]
    public void Parses_binary_values_containing_crlf()
    {
        var args = Parse("*2\r\n$4\r\na\r\nb\r\n$0\r\n\r\n", out ParseStatus status, out _);
        Assert.Equal(ParseStatus.Complete, status);
        Assert.Equal(["a\r\nb", ""], args);
    }

    [Theory]
    [InlineData("PING\r\n", new[] { "PING" })]
    [InlineData("SET  k   v\n", new[] { "SET", "k", "v" })]
    [InlineData("\tGET\tk\r\n", new[] { "GET", "k" })]
    public void Parses_inline_requests(string text, string[] expected)
    {
        Assert.Equal(expected, Parse(text, out ParseStatus status, out _));
        Assert.Equal(ParseStatus.Complete, status);
    }

    [Theory]
    [InlineData("*0\r\n")]
    [InlineData("*-1\r\n")]
    [InlineData("   \r\n")]
    public void Empty_requests_are_consumed_without_a_command(string text)
    {
        Parse(text, out ParseStatus status, out int consumed);
        Assert.Equal(ParseStatus.Empty, status);
        Assert.Equal(text.Length, consumed);
    }

    [Theory]
    [InlineData("*2\r\n$3\r\nGET\r\n:1\r\n", "Protocol error: expected '$', got ':'")]
    [InlineData("*1\r\n$x\r\n", "Protocol error: invalid bulk length")]
    [InlineData("*1\r\n$-5\r\n", "Protocol error: invalid bulk length")]
    [InlineData("*1\r\n$3\r\nGETxx", "Protocol error: invalid bulk length")]
    [InlineData("*1\r\n$536870913\r\n", "Protocol error: invalid bulk length")]
    [InlineData("*x\r\n", "Protocol error: invalid multibulk length")]
    [InlineData("*1048577\r\n", "Protocol error: invalid multibulk length")]
    [InlineData("*1\n", "Protocol error: invalid multibulk length")]
    public void Reports_protocol_errors(string text, string message)
    {
        Parse(text, out ParseStatus status, out _, out string? error);
        Assert.Equal(ParseStatus.ProtocolError, status);
        Assert.Equal(message, error);
    }

    [Fact]
    public void Inline_request_without_newline_is_rejected_past_64KB()
    {
        Parse(new string('a', (64 * 1024) + 1), out ParseStatus status, out _, out string? error);
        Assert.Equal(ParseStatus.ProtocolError, status);
        Assert.Equal("Protocol error: too big inline request", error);
    }

    [Fact]
    public void Every_proper_prefix_is_incomplete()
    {
        const string request = "*3\r\n$3\r\nSET\r\n$10\r\nkey:000001\r\n$5\r\nhello\r\n";
        for (int n = 0; n < request.Length; n++)
        {
            Parse(request[..n], out ParseStatus status, out _);
            Assert.True(status == ParseStatus.Incomplete, $"prefix of length {n} returned {status}");
        }
    }

    [Fact]
    public void Any_split_across_segments_parses_like_one_buffer()
    {
        Gen<byte[][]> commands = Gen.Byte.Array[0, 40].Array[1, 6];
        Gen.Select(commands, Gen.Int[0, 400].Array[0, 12]).Sample((cmd, cuts) =>
        {
            byte[] wire = Encode(cmd);
            var sequence = Segment.Split(wire, cuts);
            var reader = new SequenceReader<byte>(sequence);
            var args = new CommandArgs();
            Assert.Equal(ParseStatus.Complete, RespParser.TryRead(ref reader, args, out _));
            Assert.Equal(cmd.Length, args.Count);
            for (int i = 0; i < cmd.Length; i++)
            {
                Assert.Equal(cmd[i], args[i].ToArray());
            }

            Assert.True(reader.End);
        });
    }

    [Fact]
    public void Pipelined_requests_parse_one_after_another()
    {
        byte[] wire = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\nECHO hi\r\n*2\r\n$3\r\nGET\r\n$1\r\nk\r\n");
        var reader = new SequenceReader<byte>(Segment.Split(wire, [3, 17, 20]));
        var args = new CommandArgs();
        var seen = new List<string>();
        while (RespParser.TryRead(ref reader, args, out _) == ParseStatus.Complete)
        {
            seen.Add(string.Join(' ', Enumerable.Range(0, args.Count).Select(i => Encoding.ASCII.GetString(args[i]))));
        }

        Assert.Equal(["PING", "ECHO hi", "GET k"], seen);
    }

    internal static byte[] Encode(byte[][] command)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"*{command.Length}\r\n"));
        foreach (byte[] arg in command)
        {
            ms.Write(Encoding.ASCII.GetBytes($"${arg.Length}\r\n"));
            ms.Write(arg);
            ms.Write("\r\n"u8);
        }

        return ms.ToArray();
    }

    private static string[] Parse(string text, out ParseStatus status, out int consumed) => Parse(text, out status, out consumed, out _);

    private static string[] Parse(string text, out ParseStatus status, out int consumed, out string? error)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(text)));
        var args = new CommandArgs();
        status = RespParser.TryRead(ref reader, args, out error);
        consumed = (int)reader.Consumed;
        return status == ParseStatus.Complete ? Enumerable.Range(0, args.Count).Select(i => Encoding.ASCII.GetString(args[i])).ToArray() : [];
    }
}
