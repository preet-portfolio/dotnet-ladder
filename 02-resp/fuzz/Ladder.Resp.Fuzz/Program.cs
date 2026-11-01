using System.Buffers;
using System.Text;
using Ladder.Resp;
using SharpFuzz;

// Claim 9. AFL++ runs this through SharpFuzz's fork server; "check <file>..." replays inputs without AFL.
// Oracle: parsing the input as a stream of requests gives the same requests, statuses and errors whether the bytes
// arrive in one segment, one byte per segment, or split at points derived from the input itself. An exception that
// escapes is a crash.
if (args is ["check", .. var files])
{
    foreach (string file in files)
    {
        Check(File.ReadAllBytes(file));
    }

    Console.WriteLine($"checked {files.Length} inputs");
    return;
}

Fuzzer.OutOfProcess.Run(stream =>
{
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    Check(buffer.ToArray());
});

static void Check(byte[] data)
{
    string whole = ParseAll(new ReadOnlySequence<byte>(data));
    string bytewise = ParseAll(Segment.Split(data, Enumerable.Range(1, Math.Max(0, data.Length - 1))));
    // Cut points chosen by the input: each byte's low 4 bits is the distance to the next cut.
    var cuts = new List<int>();
    for (int at = 0; at < data.Length; at += 1 + (data[at] & 0x0F))
    {
        cuts.Add(at);
    }

    string chosen = ParseAll(Segment.Split(data, cuts));
    if (whole != bytewise || whole != chosen)
    {
        throw new InvalidOperationException($"split changes the parse of {Convert.ToHexString(data)}:\nwhole:    {whole}\nbytewise: {bytewise}\nchosen:   {chosen}");
    }
}

// Mirrors the server's process loop: parse requests until one is incomplete or the protocol is violated.
static string ParseAll(ReadOnlySequence<byte> input)
{
    var log = new StringBuilder();
    var args = new CommandArgs();
    var reader = new SequenceReader<byte>(input);
    while (true)
    {
        long before = reader.Consumed;
        ParseStatus status = RespParser.TryRead(ref reader, args, out string? error);
        log.Append(status);
        switch (status)
        {
            case ParseStatus.Complete:
                log.Append('[');
                foreach (byte[] arg in args.ToArrays())
                {
                    log.Append(Convert.ToHexString(arg)).Append(',');
                }

                log.Append("]@").Append(reader.Consumed).Append(' ');
                break;
            case ParseStatus.Empty:
                log.Append('@').Append(reader.Consumed).Append(' ');
                if (reader.Consumed == before)
                {
                    return log.Append("(no progress)").ToString();
                }

                break;
            case ParseStatus.Incomplete:
                return log.Append('@').Append(before).ToString();
            default:
                return log.Append(": ").Append(error).ToString();
        }
    }
}

/// <summary>Builds a ReadOnlySequence from slices of one array (the same helper as the unit tests').</summary>
internal sealed class Segment : ReadOnlySequenceSegment<byte>
{
    private Segment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    public static ReadOnlySequence<byte> Split(byte[] data, IEnumerable<int> cuts)
    {
        if (data.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

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
