// JsonDocument.Parse and Utf8JsonReader accept a string literal that is not valid UTF-8; the error appears only when
// the string is read.
using System.Text;
using System.Text.Json;

byte[][] inputs =
[
    [(byte)'[', (byte)'"', 0xFF, (byte)'"', (byte)']'],                // a byte that never appears in UTF-8
    [(byte)'[', (byte)'"', 0xC0, 0xAF, (byte)'"', (byte)']'],          // overlong encoding of '/'
    [(byte)'[', (byte)'"', 0xED, 0xA0, 0x80, (byte)'"', (byte)']'],    // UTF-8-encoded surrogate U+D800
    [(byte)'[', (byte)'"', 0xE6, 0x97, (byte)'"', (byte)']'],          // truncated 3-byte sequence
];

Console.WriteLine($"System.Text.Json {typeof(JsonDocument).Assembly.GetName().Version}, {typeof(JsonDocument).Assembly.Location}");
foreach (byte[] json in inputs)
{
    Console.WriteLine($"\ninput: {Convert.ToHexString(json)}  (Utf8.IsValid: {System.Text.Unicode.Utf8.IsValid(json)})");

    Console.WriteLine($"  Utf8JsonReader reads every token: {Describe(() => ReadAll(json))}");

    JsonDocument? document = null;
    Console.WriteLine($"  JsonDocument.Parse:               {Describe(() => { document = JsonDocument.Parse(json); return "OK"; })}");
    if (document is not null)
    {
        Console.WriteLine($"  GetString():                      {Describe(() => document.RootElement[0].GetString()!)}");
        Console.WriteLine($"  ValueEquals(\"x\"):                 {Describe(() => document.RootElement[0].ValueEquals("x").ToString())}");
        Console.WriteLine($"  GetRawText():                     {Describe(() => document.RootElement.GetRawText())}");
        document.Dispose();
    }

    Console.WriteLine($"  JsonSerializer.Deserialize<string[]>: {Describe(() => JsonSerializer.Deserialize<string[]>(json)![0])}");
}

static string ReadAll(byte[] json)
{
    var reader = new Utf8JsonReader(json);
    while (reader.Read())
    {
    }

    return "OK";
}

static string Describe(Func<string> action)
{
    try
    {
        return action();
    }
    catch (Exception e)
    {
        return $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
    }
}
