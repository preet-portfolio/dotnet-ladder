<!-- Draft for Preet to review and file at https://github.com/dotnet/runtime/issues/new (area-System.Text.Json). Not filed. -->

**Title:** `JsonDocument.Parse` accepts invalid UTF-8 in strings, then `GetRawText()` throws; behaviour undocumented

### Description

`Utf8JsonReader` and `JsonDocument.Parse` accept string literals that contain ill-formed UTF-8: stray continuation
bytes, overlong encodings, encoded surrogates and truncated sequences. Reading completes without error. The error
surfaces later, as `InvalidOperationException`, when a string is materialized. This includes
`JsonElement.GetRawText()` on a document that parsed successfully. `JsonElement.ValueEquals` returns `false` without
any error.

From the source, this looks deliberate: `ConsumeString` validates only when it finds a backslash or a control
character. But the docs do not say so:
- The `Utf8JsonReader` reference says it "by default adheres strictly to the JSON RFC", and RFC 8259 §8.1 requires
  UTF-8.
- The remarks on `ValueTextEquals` say "you cannot have invalid UTF-8 within the JSON payload".

So callers who check validity with `JsonDocument.Parse` get documents that throw later, far from where the bad input
came in.

### Reproduction Steps

```csharp
using System.Text.Json;

byte[] json = [(byte)'[', (byte)'"', 0xFF, (byte)'"', (byte)']'];     // also: C0 AF, ED A0 80, E6 97

var reader = new Utf8JsonReader(json);
while (reader.Read()) { }                                             // no exception

using var doc = JsonDocument.Parse(json);                             // no exception
Console.WriteLine(doc.RootElement[0].ValueEquals("x"));               // False
Console.WriteLine(doc.RootElement.GetRawText());                      // InvalidOperationException
```

### Expected behavior

One of these:
1. **Documentation** says that `Utf8JsonReader` and `JsonDocument` do not validate UTF-8 inside string values, and
   that the error surfaces on materialization as `InvalidOperationException`. This also means correcting the
   `ValueTextEquals` remark.
2. **An opt-in validation option**, for example on `JsonReaderOptions` or `JsonDocumentOptions`, that rejects
   ill-formed UTF-8 while reading with `JsonException`.

For cost: `Utf8.IsValid` over the whole input is fast. On our measurements it was 2–7% of a tokenize pass over the
nativejson-benchmark corpus.

Separately, it seems worth confirming whether `GetRawText()` *should* throw on a document that `Parse` accepted. It
returns the raw text, so transcoding it is the only reason it can fail.

### Actual behavior

Output on .NET 10.0.14, the same for all four inputs:

```
Utf8JsonReader reads every token: OK
JsonDocument.Parse:               OK
GetString():                      InvalidOperationException: Cannot transcode invalid UTF-8 JSON text to UTF-16 string.
ValueEquals("x"):                 False
GetRawText():                     InvalidOperationException: Cannot transcode invalid UTF-8 JSON text to UTF-16 string.
JsonSerializer.Deserialize<string[]>: JsonException: The JSON value could not be converted to System.String.
```

### Regression?

Not known. Observed on .NET 10.0.13 and 10.0.14.

### Known Workarounds

Call `System.Text.Unicode.Utf8.IsValid(bytes)` before parsing.

### Configuration

.NET 10.0.14, Linux x64.

### Other information

- Found while differential-testing a JSON tokenizer against STJ on JSONTestSuite. 20 of the 35 `i_` files are
  accepted by `JsonDocument.Parse` and then fail on `GetString`.
- Not the same as JSON-SURR-1 in #134839, which is about an *escaped* lone surrogate (`"\ud83d"`).
