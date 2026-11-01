using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Ladder.Json.Bench;

/// <summary>
/// Claim 6: synthetic documents of about 1 MB made of one token kind each, so the time per token of each kind can be
/// measured for both tokenizers. The generator is deterministic (fixed seed). <see cref="Generate"/> is public so the
/// "model" command can report the same documents' token counts.
/// </summary>
[MemoryDiagnoser]
[MedianColumn, MinColumn, MaxColumn]
public class TokenKindBenchmarks
{
    private static readonly JsonReaderOptions s_readerOptions = new() { MaxDepth = JsonTokenizer.MaxSupportedDepth };
    private byte[] _json = [];

    [Params("Doubles", "Integers", "ShortStrings", "LongStrings", "Literals", "IndentedLiterals", "EmptyContainers", "Properties")]
    public string Kind { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _json = Generate(Kind);
        if (Ours() != Stj())
        {
            throw new InvalidOperationException($"{Kind}: token counts differ.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Stj()
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
    public int Ours()
    {
        var tokenizer = new JsonTokenizer(_json);
        int count = 0;
        while (tokenizer.Read())
        {
            count++;
        }

        return tokenizer.Error.IsError ? -1 : count;
    }

    public static byte[] Generate(string kind)
    {
        const int TargetBytes = 1 << 20;
        var random = new Random(20261101);
        var sb = new StringBuilder(TargetBytes + 1024);
        sb.Append('[');
        bool first = true;
        while (sb.Length < TargetBytes)
        {
            if (!first)
            {
                sb.Append(kind == "IndentedLiterals" ? ",\n  " : ",");
            }
            else if (kind == "IndentedLiterals")
            {
                sb.Append("\n  ");
            }

            first = false;
            switch (kind)
            {
                case "Doubles":
                    // Shaped like canada.json's coordinates: up to 17 significant digits, some negative.
                    sb.Append(((random.NextDouble() * 360) - 180).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case "Integers":
                    sb.Append(random.Next(0, 100_000).ToString(CultureInfo.InvariantCulture));
                    break;
                case "ShortStrings":
                    sb.Append('"').Append(Letters(random, 8)).Append('"');
                    break;
                case "LongStrings":
                    sb.Append('"').Append(Letters(random, 120)).Append('"');
                    break;
                case "Literals" or "IndentedLiterals":
                    sb.Append((random.Next(3)) switch { 0 => "true", 1 => "false", _ => "null" });
                    break;
                case "EmptyContainers":
                    sb.Append(random.Next(2) == 0 ? "[]" : "{}");
                    break;
                case "Properties":
                    // One object per element; every value is a literal, so the property cost is the object's cost minus literals.
                    sb.Append("{\"").Append(Letters(random, 8)).Append("\":true,\"").Append(Letters(random, 8)).Append("\":null}");
                    break;
                default:
                    throw new ArgumentException(kind, nameof(kind));
            }
        }

        sb.Append(kind == "IndentedLiterals" ? "\n]" : "]");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Letters(Random random, int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('a' + random.Next(26));
        }

        return new string(chars);
    }
}
