using System.Text.Json;
using System.Text.Unicode;
using Ladder.Json;
using SharpFuzz;

// Claim 8. AFL++ runs this through SharpFuzz's fork server; with "check <file>..." it replays inputs without AFL,
// which is how crashes are triaged and how the regression tests were derived. An exception that escapes is a crash.
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

static void Check(byte[] json)
{
    var tokenizer = new JsonTokenizer(json);
    while (tokenizer.Read())
    {
        // Exercise the accessors too: they decode what Read only delimited.
        if (tokenizer.Kind is JsonTokenKind.String or JsonTokenKind.PropertyName)
        {
            char[] chars = new char[tokenizer.ValueSpan.Length];
            _ = tokenizer.TryGetString(chars, out _, out _);
        }
        else if (tokenizer.Kind == JsonTokenKind.Number)
        {
            _ = tokenizer.TryGetDouble(out _);
            _ = tokenizer.TryGetInt64(out _);
        }
    }

    bool tokenizerAccepts = !tokenizer.Error.IsError;
    _ = JsonDocumentParser.TryParse(json, out _, out _);

    // Same oracle as PropertyTests: Utf8JsonReader does not validate UTF-8 inside strings, so it is an oracle only for
    // valid UTF-8 input.
    if (Utf8.IsValid(json) && ReaderAccepts(json) != tokenizerAccepts)
    {
        throw new InvalidOperationException($"validity disagrees with Utf8JsonReader (ours: {tokenizerAccepts}): {Convert.ToHexString(json)}");
    }
}

static bool ReaderAccepts(byte[] json)
{
    try
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = JsonTokenizer.MaxSupportedDepth });
        int tokens = 0;
        while (reader.Read())
        {
            tokens++;
        }

        return tokens > 0;
    }
    catch (JsonException)
    {
        return false;
    }
}
