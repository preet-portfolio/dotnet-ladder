using System.Buffers;
using System.Text;
using Ladder.Resp.Commands;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Tests;

/// <summary>Runs commands in-process and returns the raw RESP reply, without sockets.</summary>
internal sealed class Harness
{
    private readonly ArrayBufferWriter<byte> _out = new();
    private readonly CommandArgs _args = new();

    public Harness()
    {
        Context = new CommandContext(Keyspace, new ServerState(0), new Session(1), _args, _out);
    }

    public Keyspace Keyspace { get; } = new();

    public CommandContext Context { get; }

    public string Run(params string[] command)
    {
        _out.ResetWrittenCount();
        _args.Reset();
        foreach (string arg in command)
        {
            _args.Add(Encoding.UTF8.GetBytes(arg));
        }

        CommandTable.Execute(Context);
        return Encoding.UTF8.GetString(_out.WrittenSpan);
    }
}
