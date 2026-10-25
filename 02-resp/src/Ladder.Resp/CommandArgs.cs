using System.Buffers;

namespace Ladder.Resp;

/// <summary>
/// The arguments of one command, reused for every command on a connection. Each argument points into the
/// pipe's buffer when it sits in one segment, and is copied into a per-connection arena when it crosses one.
/// Valid only until the connection advances its <see cref="System.IO.Pipelines.PipeReader"/>.
/// </summary>
public sealed class CommandArgs
{
    private ReadOnlyMemory<byte>[] _args = new ReadOnlyMemory<byte>[8];
    private byte[] _arena = new byte[256];
    private int _arenaUsed;

    public int Count { get; private set; }

    public ReadOnlySpan<byte> this[int index] => _args[index].Span;

    public ReadOnlyMemory<byte> Memory(int index) => _args[index];

    internal void Reset()
    {
        Count = 0;
        _arenaUsed = 0;
    }

    internal void Add(in ReadOnlySequence<byte> value)
    {
        if (Count == _args.Length)
        {
            Array.Resize(ref _args, _args.Length * 2);
        }

        if (value.IsSingleSegment)
        {
            _args[Count++] = value.First;
            return;
        }

        int length = checked((int)value.Length);
        if (_arenaUsed + length > _arena.Length)
        {
            // Earlier arguments keep pointing at the old arena, which stays alive until they are dropped.
            _arena = new byte[Math.Max(_arena.Length * 2, length)];
            _arenaUsed = 0;
        }

        Memory<byte> slot = _arena.AsMemory(_arenaUsed, length);
        value.CopyTo(slot.Span);
        _arenaUsed += length;
        _args[Count++] = slot;
    }

    internal void Add(ReadOnlyMemory<byte> value)
    {
        if (Count == _args.Length)
        {
            Array.Resize(ref _args, _args.Length * 2);
        }

        _args[Count++] = value;
    }

    /// <summary>Copies the arguments out; used where a command must outlive the input buffer (tests, MULTI queues).</summary>
    public byte[][] ToArrays()
    {
        var copy = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            copy[i] = _args[i].ToArray();
        }

        return copy;
    }
}
