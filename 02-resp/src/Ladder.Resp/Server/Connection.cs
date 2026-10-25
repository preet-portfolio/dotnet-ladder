using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using Ladder.Resp.Commands;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Server;

/// <summary>
/// One client. Three loops share two pipes, as in Kestrel's SocketConnection:
/// receive (socket → input pipe), process (input pipe → commands → output pipe), send (output pipe → socket).
/// The pipes' pause thresholds give back-pressure: a client that pipelines faster than we answer, or reads
/// replies slower than we produce them, stalls its own loops instead of growing memory without bound.
/// </summary>
public sealed class Connection
{
    private const int MinReceiveBuffer = 4096;

    private static readonly PipeOptions s_options = new(
        pauseWriterThreshold: 1024 * 1024,
        resumeWriterThreshold: 512 * 1024,
        useSynchronizationContext: false);

    private readonly Socket _socket;
    private readonly Pipe _input = new(s_options);
    private readonly Pipe _output = new(s_options);
    private readonly CommandContext _context;
    private readonly ServerState _server;
    private readonly Session _session;

    public Connection(Socket socket, Keyspace keyspace, ServerState server)
    {
        _socket = socket;
        _server = server;
        _session = new Session(server.NewClientId());
        _context = new CommandContext(keyspace, server, _session, new CommandArgs(), _output.Writer);
    }

    public async Task RunAsync()
    {
        _server.Clients[_session.Id] = _session;
        try
        {
            await Task.WhenAll(ReceiveAsync(), ProcessAsync(), SendAsync()).ConfigureAwait(false);
        }
        finally
        {
            _server.Clients.TryRemove(_session.Id, out _);
            _socket.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        PipeWriter writer = _input.Writer;
        Exception? error = null;
        try
        {
            while (true)
            {
                // A zero-byte receive waits for data without holding a buffer, so idle clients cost no buffer memory.
                await _socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None).ConfigureAwait(false);
                Memory<byte> buffer = writer.GetMemory(MinReceiveBuffer);
                int received = await _socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                if (received == 0)
                {
                    break;
                }

                writer.Advance(received);
                FlushResult flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (SocketException ex)
        {
            error = ex;
        }
        catch (ObjectDisposedException ex)
        {
            error = ex;
        }

        await writer.CompleteAsync(error).ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        PipeReader reader = _input.Reader;
        PipeWriter output = _output.Writer;
        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    break;
                }

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition consumed = ProcessBuffer(buffer, out bool close);
                reader.AdvanceTo(consumed, buffer.End);

                FlushResult flush = await output.FlushAsync().ConfigureAwait(false);
                if (close || flush.IsCompleted || result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await output.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes every complete request in <paramref name="buffer"/> and returns how far it got. A partial request
    /// at the end is left unconsumed; the next read retries it with more bytes.
    /// </summary>
    private SequencePosition ProcessBuffer(ReadOnlySequence<byte> buffer, out bool close)
    {
        close = false;
        var reader = new SequenceReader<byte>(buffer);
        SequencePosition consumed = buffer.Start;
        while (!reader.End)
        {
            ParseStatus status = RespParser.TryRead(ref reader, _context.Args, out string? error);
            if (status == ParseStatus.Incomplete)
            {
                break;
            }

            if (status == ParseStatus.ProtocolError)
            {
                // Valkey answers a protocol error and then closes the connection.
                RespWriter.Error(_context.Out, "ERR " + error);
                close = true;
                return buffer.End;
            }

            consumed = reader.Position;
            if (status == ParseStatus.Empty)
            {
                continue;
            }

            CommandTable.Execute(_context);
            if (_session.CloseAfterReply)
            {
                close = true;
                break;
            }
        }

        return consumed;
    }

    private async Task SendAsync()
    {
        PipeReader reader = _output.Reader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    int sent = 0;
                    while (sent < segment.Length)
                    {
                        sent += await _socket.SendAsync(segment[sent..], SocketFlags.None).ConfigureAwait(false);
                    }
                }

                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }

            _socket.Close();
        }
    }
}
