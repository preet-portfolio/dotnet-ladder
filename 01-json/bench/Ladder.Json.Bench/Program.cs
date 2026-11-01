using System.Diagnostics;
using BenchmarkDotNet.Running;
using Ladder.Json;
using Ladder.Json.Bench;

// "soak <seconds> [file]" tokenizes the corpus (or one file) in a loop so dotnet-counters/dotnet-trace can attach;
// "soak-stj <seconds> [file]" does the same with Utf8JsonReader, for side-by-side CPU profiles. Anything else runs BenchmarkDotNet.
if (args is ["soak" or "soak-stj", var secondsText, ..])
{
    bool stj = args[0] == "soak-stj";
    byte[][] docs = [.. Corpus.Files.Where(f => args.Length < 3 || f == args[2]).Select(Corpus.Load)];
    var stopwatch = Stopwatch.StartNew();
    long tokens = 0;
    long bytes = 0;
    Console.WriteLine($"pid {Environment.ProcessId}: tokenizing corpus for {secondsText}s");
    while (stopwatch.Elapsed.TotalSeconds < int.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture))
    {
        foreach (byte[] doc in docs)
        {
            tokens += stj ? CountStj(doc) : CountOurs(doc);
            bytes += doc.Length;
        }
    }

    Console.WriteLine($"{tokens:N0} tokens, {bytes / (1024.0 * 1024 * 1024) / stopwatch.Elapsed.TotalSeconds:F2} GiB/s, gen0 collections: {GC.CollectionCount(0)}, allocated: {GC.GetTotalAllocatedBytes(precise: true):N0} bytes");
    return;
}

// "model" prints, for each corpus file and synthetic document, the token counts the per-kind cost model needs.
if (args is ["model"])
{
    Console.WriteLine("doc,bytes,tokens,numbers,strings,properties,literals,containers,whitespace_bytes,string_bytes");
    foreach (string name in Corpus.Files)
    {
        PrintCounts(name, Corpus.Load(name));
    }

    foreach (string kind in new[] { "Doubles", "Integers", "ShortStrings", "LongStrings", "Literals", "IndentedLiterals", "EmptyContainers", "Properties" })
    {
        PrintCounts(kind, TokenKindBenchmarks.Generate(kind));
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(TokenizeBenchmarks).Assembly).Run(args);

static void PrintCounts(string name, byte[] doc)
{
    long numbers = 0, strings = 0, properties = 0, literals = 0, containers = 0, tokens = 0, stringBytes = 0, whitespace = 0;
    int end = 0;
    var tokenizer = new JsonTokenizer(doc);
    while (tokenizer.Read())
    {
        tokens++;
        // Whitespace is counted only between tokens, never inside strings.
        for (int i = end; i < tokenizer.TokenOffset; i++)
        {
            if (doc[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                whitespace++;
            }
        }

        int length;
        switch (tokenizer.Kind)
        {
            case JsonTokenKind.Number: numbers++; length = tokenizer.ValueSpan.Length; break;
            case JsonTokenKind.String: strings++; stringBytes += tokenizer.ValueSpan.Length; length = tokenizer.ValueSpan.Length + 2; break;
            case JsonTokenKind.PropertyName: properties++; stringBytes += tokenizer.ValueSpan.Length; length = tokenizer.ValueSpan.Length + 2; break;
            case JsonTokenKind.True or JsonTokenKind.Null: literals++; length = 4; break;
            case JsonTokenKind.False: literals++; length = 5; break;
            default: containers++; length = 1; break;
        }

        end = tokenizer.TokenOffset + length;
    }

    for (int i = end; i < doc.Length; i++)
    {
        if (doc[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
        {
            whitespace++;
        }
    }
    Console.WriteLine(FormattableString.Invariant($"{name},{doc.Length},{tokens},{numbers},{strings},{properties},{literals},{containers},{whitespace},{stringBytes}"));
}

static long CountOurs(byte[] doc)
{
    long n = 0;
    var tokenizer = new JsonTokenizer(doc);
    while (tokenizer.Read())
    {
        n++;
    }

    return n;
}

static long CountStj(byte[] doc)
{
    long n = 0;
    var reader = new System.Text.Json.Utf8JsonReader(doc);
    while (reader.Read())
    {
        n++;
    }

    return n;
}
