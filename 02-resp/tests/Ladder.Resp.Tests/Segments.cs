using System.Buffers;

namespace Ladder.Resp.Tests;

/// <summary>Builds a ReadOnlySequence from several arrays, to reproduce data arriving in separate socket reads.</summary>
internal sealed class Segment : ReadOnlySequenceSegment<byte>
{
    private Segment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    public static ReadOnlySequence<byte> Split(byte[] data, IEnumerable<int> cuts)
    {
        var points = cuts.Where(c => c > 0 && c < data.Length).Distinct().Order().Append(data.Length).ToList();
        Segment first = new(data.AsMemory(0, points[0]), 0);
        Segment last = first;
        int start = points[0];
        foreach (int end in points.Skip(1))
        {
            var next = new Segment(data.AsMemory(start, end - start), start);
            last.Next = next;
            last = next;
            start = end;
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
