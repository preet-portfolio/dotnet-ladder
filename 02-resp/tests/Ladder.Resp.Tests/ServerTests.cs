using System.Net;
using System.Net.Sockets;
using System.Text;
using Ladder.Resp.Server;

namespace Ladder.Resp.Tests;

public class ServerTests
{
    [Fact]
    public async Task Replies_to_pipelined_commands_sent_one_byte_at_a_time()
    {
        await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync(server.LocalEndPoint, TestContext.Current.CancellationToken);

        byte[] request = Encoding.ASCII.GetBytes("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$5\r\nhello\r\nGET k\r\n*2\r\n$4\r\nINCR\r\n$1\r\nn\r\n");
        foreach (byte b in request)
        {
            await client.SendAsync(new[] { b }, SocketFlags.None, TestContext.Current.CancellationToken);
        }

        const string expected = "+OK\r\n$5\r\nhello\r\n:1\r\n";
        Assert.Equal(expected, await ReadAsync(client, expected.Length));
    }

    [Fact]
    public async Task Protocol_error_replies_then_closes()
    {
        await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint, TestContext.Current.CancellationToken);
        await client.SendAsync("*1\r\n$x\r\n"u8.ToArray(), SocketFlags.None, TestContext.Current.CancellationToken);

        const string expected = "-ERR Protocol error: invalid bulk length\r\n";
        Assert.Equal(expected, await ReadAsync(client, expected.Length));
        Assert.Equal(0, await client.ReceiveAsync(new byte[1], SocketFlags.None, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Large_value_crosses_many_buffer_segments()
    {
        await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint, TestContext.Current.CancellationToken);

        string value = string.Concat(Enumerable.Range(0, 100_000).Select(i => (char)('a' + (i % 26))));
        byte[] set = ParserTests.Encode([Encoding.ASCII.GetBytes("SET"), Encoding.ASCII.GetBytes("big"), Encoding.ASCII.GetBytes(value)]);
        byte[] get = ParserTests.Encode([Encoding.ASCII.GetBytes("GET"), Encoding.ASCII.GetBytes("big")]);
        await client.SendAsync(set.Concat(get).ToArray(), SocketFlags.None, TestContext.Current.CancellationToken);

        string expected = $"+OK\r\n${value.Length}\r\n{value}\r\n";
        Assert.Equal(expected, await ReadAsync(client, expected.Length));
    }

    [Fact]
    public async Task Request_larger_than_the_pipe_pause_threshold_completes()
    {
        // A 4 MB value cannot be consumed until all of it has arrived, so the input pipe must not pause at 1 MB.
        await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint, TestContext.Current.CancellationToken);

        byte[] value = new byte[4 * 1024 * 1024];
        Array.Fill(value, (byte)'v');
        byte[] set = ParserTests.Encode([Encoding.ASCII.GetBytes("SET"), Encoding.ASCII.GetBytes("big"), value]);
        Task send = client.SendAsync(set, SocketFlags.None, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal("+OK\r\n", await ReadAsync(client, 5));
        await send;
    }

    [Fact]
    public async Task Pipelined_large_replies_are_produced_only_as_fast_as_the_client_reads()
    {
        // 2,000 pipelined GETs of a 64 KB value would build a 128 MB reply if the server ran the whole batch
        // before flushing. With back-pressure the unsent output stays near the pipe's pause threshold.
        await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(server.LocalEndPoint, TestContext.Current.CancellationToken);

        byte[] value = new byte[64 * 1024];
        byte[] set = ParserTests.Encode([Encoding.ASCII.GetBytes("SET"), Encoding.ASCII.GetBytes("v"), value]);
        await client.SendAsync(set, SocketFlags.None, TestContext.Current.CancellationToken);
        Assert.Equal("+OK\r\n", await ReadAsync(client, 5));

        byte[] get = ParserTests.Encode([Encoding.ASCII.GetBytes("GET"), Encoding.ASCII.GetBytes("v")]);
        byte[] batch = [.. Enumerable.Repeat(get, 2000).SelectMany(b => b)];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await client.SendAsync(batch, SocketFlags.None, TestContext.Current.CancellationToken);
        await Task.Delay(1000, TestContext.Current.CancellationToken); // the client reads nothing meanwhile
        long grown = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(grown < 16 * 1024 * 1024, $"server allocated {grown:N0} bytes for replies the client has not read");

        // Once the client reads, every reply arrives: stopping a batch early must not drop or reorder requests.
        string header = $"${value.Length}\r\n";
        int replyLength = header.Length + value.Length + 2;
        string replies = await ReadAsync(client, replyLength * 2000);
        Assert.Equal(replyLength * 2000, replies.Length);
        Assert.Equal(2000, replies.Split(header).Length - 1);
    }

    private static async Task<string> ReadAsync(Socket socket, int length)
    {
        var buffer = new byte[length];
        int read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < length)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, timeout.Token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}
