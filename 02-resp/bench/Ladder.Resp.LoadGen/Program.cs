using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HdrHistogram;

// Load generator for claims 5-8. Modes:
//   load   <port> <keys> <valueBytes>                                   SET key:00000000.. with pipelining
//   closed <port> <conns> <seconds> <keys>                               closed loop GET, one outstanding per connection; prints req/s
//   open   <port> <rate> <seconds> <warmupSeconds> <conns> <keys> <csv> <label>
//          open loop GET at a fixed rate. Request k is *scheduled* at start + k/rate and its latency runs from that
//          scheduled time, so a server that falls behind is charged for the queueing it causes (no coordinated omission).
//   replay <port> <commands.resp> <hashes.bin> [dumpFrom dumpCount dumpFile]
//          sends a file of RESP commands in order, pipelined in windows, and writes the first 8 bytes of SHA-256 of
//          every reply, so two servers' replies can be compared byte for byte without storing them.
//   diff   <hashesA.bin> <hashesB.bin>                                   first mismatching reply index and mismatch count
var inv = CultureInfo.InvariantCulture;
switch (args)
{
    case ["load", var port, var keys, var valueBytes]:
        Load(int.Parse(port, inv), int.Parse(keys, inv), int.Parse(valueBytes, inv));
        break;
    case ["closed", var port, var conns, var seconds, var keys]:
        Closed(int.Parse(port, inv), int.Parse(conns, inv), double.Parse(seconds, inv), int.Parse(keys, inv));
        break;
    case ["open", var port, var rate, var seconds, var warmup, var conns, var keys, var csv, var label]:
        Open(int.Parse(port, inv), double.Parse(rate, inv), double.Parse(seconds, inv), double.Parse(warmup, inv), int.Parse(conns, inv), int.Parse(keys, inv), csv, label);
        break;
    case ["replay", var port, var commands, var hashes, ..var rest]:
        Replay(int.Parse(port, inv), commands, hashes, rest.Length == 3 ? (long.Parse(rest[0], inv), long.Parse(rest[1], inv), rest[2]) : null);
        break;
    case ["diff", var a, var b]:
        return Diff(a, b);
    default:
        Console.Error.WriteLine("usage: see the comment at the top of Program.cs");
        return 2;
}

return 0;

static Socket Connect(int port)
{
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    socket.Connect("127.0.0.1", port);
    return socket;
}

static byte[] GetCommand(int key) => Encoding.ASCII.GetBytes($"*2\r\n$3\r\nGET\r\n$12\r\nkey:{key:D8}\r\n");

static void Load(int port, int keys, int valueBytes)
{
    using Socket socket = Connect(port);
    string value = new('v', valueBytes);
    var reader = new ReplyReader(socket);
    const int Window = 1000;
    for (int start = 0; start < keys; start += Window)
    {
        int n = Math.Min(Window, keys - start);
        var sb = new StringBuilder();
        for (int i = start; i < start + n; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"*3\r\n$3\r\nSET\r\n$12\r\nkey:{i:D8}\r\n${valueBytes}\r\n{value}\r\n");
        }

        socket.Send(Encoding.ASCII.GetBytes(sb.ToString()));
        for (int i = 0; i < n; i++)
        {
            reader.Next();
        }
    }

    Console.WriteLine($"loaded {keys} keys of {valueBytes} bytes");
}

static void Closed(int port, int conns, double seconds, int keys)
{
    long total = 0;
    var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
    var threads = Enumerable.Range(0, conns).Select(c => new Thread(() =>
    {
        using Socket socket = Connect(port);
        var reader = new ReplyReader(socket);
        var random = new Random(c);
        long count = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            socket.Send(GetCommand(random.Next(keys)));
            reader.Next();
            count++;
        }

        Interlocked.Add(ref total, count);
    })).ToList();
    var sw = Stopwatch.StartNew();
    threads.ForEach(t => t.Start());
    threads.ForEach(t => t.Join());
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"closed-loop GET, {conns} connections: {total / sw.Elapsed.TotalSeconds:F0} req/s"));
}

static void Open(int port, double rate, double seconds, double warmup, int conns, int keys, string csv, string label)
{
    var sockets = Enumerable.Range(0, conns).Select(_ => Connect(port)).ToArray();
    var pending = sockets.Select(_ => new ConcurrentQueue<long>()).ToArray();
    var histograms = sockets.Select(_ => new LongHistogram(TimeSpan.FromSeconds(60).Ticks * 100, 3)).ToArray();
    long completed = 0;
    long start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
    long measureFrom = start + (long)(warmup * Stopwatch.Frequency);
    long total = (long)(rate * (warmup + seconds));
    double ticksPerRequest = Stopwatch.Frequency / rate;
    long sent = 0;

    var receivers = Enumerable.Range(0, conns).Select(c => new Thread(() =>
    {
        var reader = new ReplyReader(sockets[c]);
        long expected = total / conns + (c < total % conns ? 1 : 0);
        for (long i = 0; i < expected; i++)
        {
            reader.Next();
            long now = Stopwatch.GetTimestamp();
            pending[c].TryDequeue(out long scheduled);
            if (scheduled >= measureFrom)
            {
                // Stopwatch ticks -> nanoseconds.
                histograms[c].RecordValue((long)((now - scheduled) * (1e9 / Stopwatch.Frequency)));
                Interlocked.Increment(ref completed);
            }
        }
    }) { IsBackground = true }).ToList();
    receivers.ForEach(t => t.Start());

    var random = new Random(1);
    long lateSends = 0;
    long maxLagTicks = 0;
    while (sent < total)
    {
        long scheduled = start + (long)(sent * ticksPerRequest);
        long now = Stopwatch.GetTimestamp();
        long wait = scheduled - now;
        if (wait > 0)
        {
            if (wait > Stopwatch.Frequency / 500)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(20);
            }

            continue;
        }

        // Send everything that is due, each with its own scheduled time.
        if (-wait > maxLagTicks)
        {
            maxLagTicks = -wait;
        }

        if (-wait > Stopwatch.Frequency / 1000)
        {
            lateSends++;
        }

        int c = (int)(sent % conns);
        pending[c].Enqueue(scheduled);
        sockets[c].Send(GetCommand(random.Next(keys)));
        sent++;
    }

    bool finished = receivers.All(t => t.Join(TimeSpan.FromSeconds(60)));
    var merged = new LongHistogram(TimeSpan.FromSeconds(60).Ticks * 100, 3);
    foreach (LongHistogram h in histograms)
    {
        merged.Add(h);
    }

    static double Us(LongHistogram h, double p) => h.GetValueAtPercentile(p) / 1000.0;
    string line = string.Create(CultureInfo.InvariantCulture,
        $"{label},{rate:F0},{seconds:F0},{conns},{merged.TotalCount},{Us(merged, 50):F1},{Us(merged, 90):F1},{Us(merged, 99):F1},{Us(merged, 99.9):F1},{merged.GetMaxValue() / 1000.0:F1},{lateSends},{maxLagTicks * 1e6 / Stopwatch.Frequency:F0},{(finished ? "ok" : "timeout")}");
    bool header = !File.Exists(csv);
    using (var writer = new StreamWriter(csv, append: true))
    {
        if (header)
        {
            writer.WriteLine("label,rate,seconds,conns,measured,p50_us,p90_us,p99_us,p999_us,max_us,sends_over_1ms_late,max_send_lag_us,status");
        }

        writer.WriteLine(line);
    }

    Console.WriteLine(line);
    foreach (Socket s in sockets)
    {
        s.Dispose();
    }
}

static void Replay(int port, string commandsPath, string hashesPath, (long From, long Count, string File)? dump)
{
    byte[] commands = File.ReadAllBytes(commandsPath);
    using Socket socket = Connect(port);
    var reader = new ReplyReader(socket);
    using var hashes = new BinaryWriter(File.Create(hashesPath));
    using FileStream? dumpFile = dump is { } d ? File.Create(d.File) : null;
    const int Window = 128;
    int offset = 0;
    long index = 0;
    Span<byte> digest = stackalloc byte[32];
    while (offset < commands.Length)
    {
        int end = offset;
        int n = 0;
        while (n < Window && end < commands.Length)
        {
            end += RespFrame.Length(commands.AsSpan(end));
            n++;
        }

        socket.Send(commands.AsSpan(offset, end - offset));
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> reply = reader.Next();
            SHA256.HashData(reply, digest);
            hashes.Write(BinaryPrimitives.ReadUInt64BigEndian(digest));
            if (dump is { } dd && index >= dd.From && index < dd.From + dd.Count)
            {
                dumpFile!.Write(Encoding.ASCII.GetBytes($"--- reply {index}\n"));
                dumpFile.Write(reply);
                dumpFile.Write("\n"u8);
            }

            index++;
        }

        offset = end;
    }

    Console.WriteLine($"replayed {index} commands");
}

static int Diff(string a, string b)
{
    byte[] x = File.ReadAllBytes(a);
    byte[] y = File.ReadAllBytes(b);
    long first = -1;
    long mismatches = 0;
    long n = Math.Min(x.Length, y.Length) / 8;
    for (long i = 0; i < n; i++)
    {
        if (!x.AsSpan((int)(i * 8), 8).SequenceEqual(y.AsSpan((int)(i * 8), 8)))
        {
            mismatches++;
            if (first < 0)
            {
                first = i;
            }
        }
    }

    Console.WriteLine($"replies: {x.Length / 8} vs {y.Length / 8}; mismatches: {mismatches}; first mismatch: {(first < 0 ? "none" : first.ToString(CultureInfo.InvariantCulture))}");
    return mismatches == 0 && x.Length == y.Length ? 0 : 1;
}

/// <summary>Reads whole RESP replies from a socket into a growing buffer.</summary>
internal sealed class ReplyReader(Socket socket)
{
    private byte[] _buffer = new byte[1 << 16];
    private int _start;
    private int _end;

    /// <summary>Returns the next complete reply; the span is valid until the next call.</summary>
    public ReadOnlySpan<byte> Next()
    {
        while (true)
        {
            int length = RespFrame.Length(_buffer.AsSpan(_start, _end - _start));
            if (length > 0)
            {
                var reply = new ReadOnlySpan<byte>(_buffer, _start, length);
                _start += length;
                return reply;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            int read = socket.Receive(_buffer, _end, _buffer.Length - _end, SocketFlags.None);
            if (read == 0)
            {
                throw new IOException("server closed the connection");
            }

            _end += read;
        }
    }
}

/// <summary>Finds where one RESP2 value (a reply, or a command, which is an array of bulk strings) ends.</summary>
internal static class RespFrame
{
    /// <summary>Length in bytes of the complete value at the start of <paramref name="data"/>, or 0 if it is incomplete.</summary>
    public static int Length(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        return Skip(data, ref pos) ? pos : 0;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length)
        {
            return false;
        }

        byte type = data[pos];
        int lineEnd = data[pos..].IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> line = data.Slice(pos + 1, lineEnd - 1);
        pos += lineEnd + 2;
        switch (type)
        {
            case (byte)'+' or (byte)'-' or (byte)':':
                return true;
            case (byte)'$':
            {
                long n = long.Parse(line, CultureInfo.InvariantCulture);
                if (n < 0)
                {
                    return true;
                }

                if (pos + n + 2 > data.Length)
                {
                    return false;
                }

                pos += (int)n + 2;
                return true;
            }

            case (byte)'*':
            {
                long n = long.Parse(line, CultureInfo.InvariantCulture);
                for (long i = 0; i < n; i++)
                {
                    if (!Skip(data, ref pos))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                throw new InvalidDataException($"unexpected RESP type byte 0x{type:X2}");
        }
    }
}
