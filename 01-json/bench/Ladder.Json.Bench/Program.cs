using System.Diagnostics;
using BenchmarkDotNet.Running;
using Ladder.Json;
using Ladder.Json.Bench;

// "soak <seconds>" tokenizes the corpus in a loop so dotnet-counters/dotnet-trace can attach; anything else runs BenchmarkDotNet.
if (args is ["soak", var secondsText])
{
    byte[][] docs = [.. Corpus.Files.Select(Corpus.Load)];
    var stopwatch = Stopwatch.StartNew();
    long tokens = 0;
    long bytes = 0;
    Console.WriteLine($"pid {Environment.ProcessId}: tokenizing corpus for {secondsText}s");
    while (stopwatch.Elapsed.TotalSeconds < int.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture))
    {
        foreach (byte[] doc in docs)
        {
            var tokenizer = new JsonTokenizer(doc);
            while (tokenizer.Read())
            {
                tokens++;
            }

            bytes += doc.Length;
        }
    }

    Console.WriteLine($"{tokens:N0} tokens, {bytes / (1024.0 * 1024 * 1024) / stopwatch.Elapsed.TotalSeconds:F2} GiB/s, gen0 collections: {GC.CollectionCount(0)}, allocated: {GC.GetTotalAllocatedBytes(precise: true):N0} bytes");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(TokenizeBenchmarks).Assembly).Run(args);
