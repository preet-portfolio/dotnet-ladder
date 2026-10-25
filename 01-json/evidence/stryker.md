# Mutation testing: Stryker.NET 5.0.0

Run with `dotnet dotnet-stryker -f stryker-config.json` from `01-json/` (test runner: Microsoft.Testing.Platform).

| Run | Score | Killed | Timeout | Survived | Note |
|---|---:|---:|---:|---:|---|
| 1 | 0.00% | 0 | 0 | 323 | **Invalid.** Stryker defaulted to the VSTest runner, the tests never ran, and every mutant "survived". Fixed by setting `"test-runner": "mtp"`. |
| 2 | 95.67% | 306 | 3 | 13 | First real measurement. |
| 3 | 97.52% | 309 | 3 | 7 + 1 no coverage | After adding tests for the 5 test gaps run 2 exposed. |
| 4 | **97.83%** | 313 | 3 | 7 | After adding the missing depth-limit test on `{`. Final. |

## Gaps found and closed (runs 2 and 3)

- `return false` → `return true` after a failed push or string read in `JsonTokenizer` (4 mutants). No test checked that `Read()` returns false on the failing call itself, rather than once more after it. The shared test helper now asserts that every `true` from `Read()` carries a real token kind.
- `JsonString.Unescape`: the error reason on `DestinationTooSmall` was never asserted, and neither was a surrogate pair that exactly fills the buffer.
- `ValueHasEscapes = false` → `true` at the start of `Read()`: no test checked the flag on non-string tokens. The helper now checks it on every token.
- Depth limit hit on `{` was never tested (only on `[`).

## The 7 remaining survivors are equivalent mutants

None of them can change observable behaviour, so no test can kill them:

| Location | Mutation | Why it can't be observed |
|---|---|---|
| `DepthBits.cs:14, 19, 23` | `depth >> 6` → `depth >>> 6` | `depth` is never negative, so arithmetic and logical shifts agree. |
| `JsonDocumentParser.cs:87, 117` | remove `ArrayPool<char>.Shared.Return(scratch)` | Not returning a rented buffer to the pool only means the pool allocates a new one later; results don't change. |
| `JsonDocumentParser.cs:113` | `scratch.Length < raw.Length` → `<=` | Rents a new buffer slightly more often; results don't change. |
| `JsonDocumentParser.cs:127` | the `reason == None ? InvalidUtf8 : reason` fallback → `reason` | Only reachable on `DestinationTooSmall`, which can't happen because the buffer is always at least `raw.Length`. A defensive branch. |
