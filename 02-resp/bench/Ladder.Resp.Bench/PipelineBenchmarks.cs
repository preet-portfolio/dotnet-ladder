using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using Ladder.Resp.Commands;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Bench;

/// <summary>
/// Claim 2: parse a pipelined batch of 16 requests from one buffer, execute each, and write the replies, the
/// way Connection.ProcessBuffer does, minus the socket. Allocated must be 0 B for GET and INCR on existing keys.
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private const int Batch = 16;
    private readonly ArrayBufferWriter<byte> _output = new(64 * 1024);
    private readonly CommandArgs _args = new();
    private CommandContext _context = null!;
    private ReadOnlySequence<byte> _get;
    private ReadOnlySequence<byte> _incr;
    private ReadOnlySequence<byte> _set;

    [GlobalSetup]
    public void Setup()
    {
        var keyspace = new Keyspace();
        _context = new CommandContext(keyspace, new ServerState(0), new Session(1), _args, _output);
        for (int i = 0; i < Batch; i++)
        {
            keyspace[0].Upsert(Encoding.ASCII.GetBytes($"key:{i:D4}"), RString.FromValue(Encoding.ASCII.GetBytes(new string('x', 64))), 0);
            keyspace[0].Upsert(Encoding.ASCII.GetBytes($"counter:{i:D4}"), RString.FromInt(0), 0);
        }

        _get = Requests(i => ["GET", $"key:{i:D4}"]);
        _incr = Requests(i => ["INCR", $"counter:{i:D4}"]);
        _set = Requests(i => ["SET", $"key:{i:D4}", new string('y', 64)]);
    }

    [Benchmark]
    public int Get_x16() => Run(_get);

    [Benchmark]
    public int Incr_x16() => Run(_incr);

    [Benchmark]
    public int Set_x16() => Run(_set);

    private int Run(ReadOnlySequence<byte> requests)
    {
        _output.ResetWrittenCount();
        var reader = new SequenceReader<byte>(requests);
        while (RespParser.TryRead(ref reader, _args, out _) == ParseStatus.Complete)
        {
            CommandTable.Execute(_context);
        }

        return _output.WrittenCount;
    }

    private static ReadOnlySequence<byte> Requests(Func<int, string[]> command)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Batch; i++)
        {
            string[] args = command(i);
            sb.Append('*').Append(args.Length).Append("\r\n");
            foreach (string a in args)
            {
                sb.Append('$').Append(a.Length).Append("\r\n").Append(a).Append("\r\n");
            }
        }

        return new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
