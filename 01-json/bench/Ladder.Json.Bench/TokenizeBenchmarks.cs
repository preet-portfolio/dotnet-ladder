using System.Text.Json;
using System.Text.Unicode;
using BenchmarkDotNet.Attributes;

namespace Ladder.Json.Bench;

// Disassembly: BenchmarkDotNet only supports it on Windows/Linux, so CI passes --disasm; locally use DOTNET_JitDisasm.
[MemoryDiagnoser]
public class TokenizeBenchmarks
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

        // A tokenizer that stopped early would look fast; refuse to benchmark unless both read every token.
        int ours = Ladder_JsonTokenizer();
        int stj = Stj_Utf8JsonReader();
        if (ours != stj)
        {
            throw new InvalidOperationException($"{File}: JsonTokenizer read {ours} tokens, Utf8JsonReader read {stj}.");
        }
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

    // Utf8JsonReader does not validate UTF-8 inside strings; this variant adds that work so both sides do the same checks.
    [Benchmark]
    public int Stj_Utf8JsonReader_plus_Utf8Validation()
    {
        if (!Utf8.IsValid(_json))
        {
            return -1;
        }

        return Stj_Utf8JsonReader();
    }

    [Benchmark]
    public bool Utf8Validation_only() => Utf8.IsValid(_json);
}
