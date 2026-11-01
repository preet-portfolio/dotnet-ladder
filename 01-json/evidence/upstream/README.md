# Upstream report for System.Text.Json (draft, not filed)

## (c) `Utf8JsonReader` and `JsonDocument.Parse` accept invalid UTF-8 inside string literals

- **Repro:** [`jsondocument-invalid-utf8/`](jsondocument-invalid-utf8/).
- **Output on runtime 10.0.14:** [`jsondocument-invalid-utf8/output-10.0.14.txt`](jsondocument-invalid-utf8/output-10.0.14.txt).
  System.Text.Json ships in the shared framework, so the version that matters is the runtime's. The 10.0.14 NuGet
  package is not loaded on net10.0.
- **The four inputs:** a lone `0xFF`, the overlong `C0 AF`, the encoded surrogate `ED A0 80`, and the truncated
  `E6 97`, each inside `["…"]`. For every one of them:
  - `Utf8JsonReader` reads every token without error.
  - `JsonDocument.Parse` succeeds.
  - Afterwards, `GetString()` and **`GetRawText()`** throw `InvalidOperationException` ("Cannot transcode invalid
    UTF-8 JSON text to UTF-16 string.").
  - `ValueEquals("x")` silently returns `false`.
  - `JsonSerializer.Deserialize<string[]>` throws `JsonException`.
- **RFC 8259 §8.1:** JSON exchanged between systems "MUST be encoded using UTF-8". The JSONTestSuite marks these files
  `i_` (implementation-defined), and STJ accepts them.

### Is it already reported?

Searched `is:issue repo:dotnet/runtime` on 2026-11-01 with these queries:
- "invalid UTF-8" JsonDocument
- Utf8JsonReader "invalid UTF-8" string validate
- "Cannot transcode invalid UTF-8"
- GetRawText invalid UTF-8
- JsonDocument.Parse invalid utf8
- Utf8JsonReader "invalid UTF8"
- Utf8JsonReader UTF-8 validation

Nothing covers it.
- The only recurring hit is [#134839](https://github.com/dotnet/runtime/issues/134839), a fuzzing meta-issue: open,
  untriaged, with no maintainer replies. Its JSON items are different bugs:
  - JSON-COPY-1: an off-by-one in `CopyString`'s destination size.
  - JSON-SURR-1: an *escaped* lone surrogate `"\ud83d"` that is accepted and then fails on write.
- The campaign's own notes, in the reporter's fork rather than the tracker, call lazy UTF-8 validation "documented
  behaviour". We could not find the documentation they mean:
  - The [Utf8JsonReader how-to](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader)
    and the [API reference](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.utf8jsonreader) say the
    reader "by default adheres strictly to the JSON RFC" and throws `JsonException` on invalid JSON. Neither says that
    UTF-8 inside strings goes unchecked.
  - The source comments in `Utf8JsonReader.ConsumeString` show that only quotes, backslashes and control bytes are
    checked. So the behaviour is deliberate in the code, but it is not documented.
  - `ValueTextEquals`'s XML remarks say "you cannot have invalid UTF-8 within the JSON payload". That is the reverse
    of what the reader allows.

### Is it by design?

It is a likely *performance* design decision, but it is not documented. A maintainer could fairly answer "by design".
So the draft asks for either of two outcomes:
1. document the behaviour; or
2. offer opt-in validation.

It does not claim a bug in the reader. The part that is hardest to defend as intentional: `GetRawText()` on a document
that parsed successfully throws.

Draft: [`DRAFT-invalid-utf8-in-strings.md`](DRAFT-invalid-utf8-in-strings.md).
