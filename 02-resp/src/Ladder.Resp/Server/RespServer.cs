using System.Net;
using System.Net.Sockets;
using Ladder.Resp.Commands;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Server;

/// <summary>Accepts TCP clients and runs the active-expiry cycle every 100 ms (Valkey's default hz of 10).</summary>
public sealed class RespServer(IPEndPoint endpoint) : IAsyncDisposable
{
    private readonly Socket _listener = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    private readonly CancellationTokenSource _stop = new();
    private Task _accept = Task.CompletedTask;
    private Task _expire = Task.CompletedTask;

    public Keyspace Keyspace { get; } = new();

    public ServerState State { get; } = new(endpoint.Port);

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndPoint!;

    public void Start()
    {
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(endpoint);
        _listener.Listen(512);
        State.Config["port"] = LocalEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _accept = AcceptLoopAsync();
        _expire = ExpireLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            client.NoDelay = true;
            _ = new Connection(client, Keyspace, State).RunAsync();
        }
    }

    private async Task ExpireLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Keyspace.ActiveExpireCycle();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        await Task.WhenAll(_accept, _expire).ConfigureAwait(false);
        _stop.Dispose();
    }
}
