using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Ladder.Json.Bench;

/// <summary>
/// Claims 4 and 5: how much of the gap to Utf8JsonReader comes back when our tokenizer also does Utf8JsonReader's
/// extra bookkeeping. <c>Ablation_Baseline</c> is the unmodified copy, so it should match <c>Ladder_JsonTokenizer</c>;
/// if it doesn't, the copy is not a fair base.
/// </summary>
[MemoryDiagnoser]
[MedianColumn, MinColumn, MaxColumn]
public class AblationBenchmarks
{
    private static readonly JsonReaderOptions s_readerOptions = new() { MaxDepth = JsonTokenizer.MaxSupportedDepth };
    private byte[] _json = [];

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    public static IEnumerable<string> Files => Corpus.Files;

    [GlobalSetup]
    public void Setup()
    {
        _json = Corpus.Load(File);
        int expected = Stj_Utf8JsonReader();
        foreach (int got in new[] { Ladder_JsonTokenizer(), Ablation_Baseline(), Ablation_Lines(), Ablation_Rollback(), Ablation_Options(), Ablation_AllThree() })
        {
            if (got != expected)
            {
                throw new InvalidOperationException($"{File}: a tokenizer read {got} tokens, Utf8JsonReader read {expected}.");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public int Stj_Utf8JsonReader()
    {
        var reader = new Utf8JsonReader(_json, s_readerOptions);
        int count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public int Ladder_JsonTokenizer()
    {
        var tokenizer = new JsonTokenizer(_json);
        int count = 0;
        while (tokenizer.Read())
        {
            count++;
        }

        return tokenizer.Error.IsError ? -1 : count;
    }

    [Benchmark]
    public int Ablation_Baseline() => Count<Baseline>(_json);

    [Benchmark]
    public int Ablation_Lines() => Count<Lines>(_json);

    [Benchmark]
    public int Ablation_Rollback() => Count<Rollback>(_json);

    [Benchmark]
    public int Ablation_Options() => Count<Options>(_json);

    [Benchmark]
    public int Ablation_AllThree() => Count<AllThree>(_json);

    internal static int Count<T>(ReadOnlySpan<byte> json)
        where T : IAblation
    {
        var tokenizer = new AblationTokenizer<T>(json);
        int count = 0;
        while (tokenizer.Read())
        {
            count++;
        }

        return tokenizer.Error.IsError ? -1 : count;
    }
}
