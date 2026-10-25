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
