using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CsCheck;

namespace Ladder.Json.Tests;

/// <summary>
/// Properties checked against System.Text.Json as the reference: its writer produces the inputs,
/// and its reader is the oracle for which mutated inputs are valid.
/// </summary>
public class PropertyTests
{
    private static readonly Gen<string> s_text =
        Gen.Frequency(
                (6, Gen.Int[0, 0x7F]),
                (2, Gen.Int[0x80, 0xD7FF]),
                (1, Gen.Int[0xE000, 0xFFFF]),
                (1, Gen.Int[0x10000, 0x10FFFF]))
            .Array[0, 12]
            .Select(codePoints => string.Concat(codePoints.Select(char.ConvertFromUtf32)));

    private static readonly Gen<Model> s_leaf = Gen.OneOf<Model>(
        Gen.Const<Model>(new Model.Null()),
        Gen.Bool.Select(b => (Model)new Model.Bool(b)),
        Gen.Double.Where(double.IsFinite).Select(d => (Model)new Model.Num(d)),
        Gen.Long.Select(l => (Model)new Model.Num(l)),
        s_text.Select(s => (Model)new Model.Str(s)));

    private static readonly Gen<Model> s_model = ModelOfDepth(4);

    private static readonly Gen<(Model Model, byte[] Json)> s_document =
        Gen.Select(s_model, Gen.Bool, Gen.Bool, (model, relaxed, indented) => (model, Write(model, relaxed, indented)));

    [Fact]
    public void Parses_what_System_Text_Json_writes()
    {
        s_document.Sample(
            doc =>
            {
                Assert.True(JsonDocumentParser.TryParse(doc.Json, out JsonValue? tree, out JsonParseError error), error.ToString());
                Assert.True(Same(doc.Model, tree!), Encoding.UTF8.GetString(doc.Json));
            },
            iter: 20_000);
    }

    [Fact]
    public void Mutated_documents_never_throw_and_agree_with_Utf8JsonReader_on_validity()
    {
        Gen<(int Op, double At, byte Value)> mutation = Gen.Select(Gen.Int[0, 2], Gen.Double[0, 1], Gen.Byte);
        Gen<byte[]> mutated = Gen.Select(s_document, mutation.Array[1, 4], (doc, ops) => Mutate(doc.Json, ops));

        mutated.Sample(
            json =>
            {
                bool tokenizerAccepts = TokenizerAccepts(json);
                _ = JsonDocumentParser.TryParse(json, out _, out _);

                // Utf8JsonReader does not validate UTF-8 inside strings, so it is only an oracle for valid UTF-8 input.
                if (Utf8.IsValid(json))
                {
                    Assert.Equal(ReaderAccepts(json), tokenizerAccepts);
                }
            },
            iter: 50_000,
            print: json => Convert.ToHexString(json) + " " + Encoding.UTF8.GetString(json));
    }

    private static Gen<Model> ModelOfDepth(int depth)
    {
        if (depth == 0)
        {
            return s_leaf;
        }

        Gen<Model> child = ModelOfDepth(depth - 1);
        return Gen.Frequency(
            (3, s_leaf),
            (1, child.Array[0, 5].Select(items => (Model)new Model.Arr(items))),
            (1, Gen.Select(s_text, child).Array[0, 5].Select(members => (Model)new Model.Obj(members))));
    }

    private static byte[] Write(Model model, bool relaxed, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions
        {
            Encoder = relaxed ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : JavaScriptEncoder.Default,
            Indented = indented,
            SkipValidation = true,
        };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            WriteValue(writer, model);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, Model model)
    {
        switch (model)
        {
            case Model.Null:
                writer.WriteNullValue();
                break;
            case Model.Bool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case Model.Num n:
                writer.WriteNumberValue(n.Value);
                break;
            case Model.Str s:
                writer.WriteStringValue(s.Value);
                break;
            case Model.Arr a:
                writer.WriteStartArray();
                foreach (Model item in a.Items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case Model.Obj o:
                writer.WriteStartObject();
                foreach ((string name, Model value) in o.Members)
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, value);
                }

                writer.WriteEndObject();
                break;
        }
    }

    private static bool Same(Model model, JsonValue value) => (model, value) switch
    {
        (Model.Null, JsonLiteral l) => l.Kind == JsonTokenKind.Null,
        (Model.Bool b, JsonLiteral l) => l.Kind == (b.Value ? JsonTokenKind.True : JsonTokenKind.False),
        (Model.Num n, JsonNumber j) => double.Parse(j.Raw, System.Globalization.CultureInfo.InvariantCulture).Equals(n.Value),
        (Model.Str s, JsonStringValue j) => string.Equals(s.Value, j.Value, StringComparison.Ordinal),
        (Model.Arr a, JsonArray j) => a.Items.Length == j.Items.Count && a.Items.Zip(j.Items).All(p => Same(p.First, p.Second)),
        (Model.Obj o, JsonObject j) => o.Members.Length == j.Members.Count
            && o.Members.Zip(j.Members).All(p => string.Equals(p.First.Name, p.Second.Key, StringComparison.Ordinal) && Same(p.First.Value, p.Second.Value)),
        _ => false,
    };

    private static byte[] Mutate(byte[] json, (int Op, double At, byte Value)[] ops)
    {
        var bytes = new List<byte>(json);
        foreach ((int op, double at, byte value) in ops)
        {
            int i = (int)(at * bytes.Count);
            switch (op)
            {
                case 0 when bytes.Count > 0:
                    bytes[Math.Min(i, bytes.Count - 1)] = value;
                    break;
                case 1:
                    bytes.Insert(i, value);
                    break;
                case 2 when bytes.Count > 0:
                    bytes.RemoveAt(Math.Min(i, bytes.Count - 1));
                    break;
            }
        }

        return [.. bytes];
    }

    private static bool TokenizerAccepts(byte[] json)
    {
        var t = new JsonTokenizer(json);
        while (t.Read())
        {
        }

        return !t.Error.IsError;
    }

    private static bool ReaderAccepts(byte[] json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = JsonTokenizer.MaxSupportedDepth });
            int tokens = 0;
            while (reader.Read())
            {
                tokens++;
            }

            // An empty or whitespace-only input reads zero tokens without throwing; RFC 8259 requires a value.
            return tokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal abstract record Model
    {
        internal sealed record Null : Model;

        internal sealed record Bool(bool Value) : Model;

        internal sealed record Num(double Value) : Model;

        internal sealed record Str(string Value) : Model;

        internal sealed record Arr(Model[] Items) : Model;

        internal sealed record Obj((string Name, Model Value)[] Members) : Model;
    }
}
