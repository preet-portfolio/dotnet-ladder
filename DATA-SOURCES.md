# Data sources

Every external suite and dataset used here is open source, pinned to an exact commit, and downloaded at run time by the project's `judge/run-judge.sh`. None of it is copied into this repository. Generated data is used only where generating it is the point: CsCheck property tests and fuzz-style mutation.

## 01 · JSON parser

| What | Used for | Source | Pinned commit | License |
|---|---|---|---|---|
| JSONTestSuite (Nicolas Seriot) | External judge: 318 `y_`/`n_`/`i_` parsing cases | https://github.com/nst/JSONTestSuite | `1ef36fa01286573e846ac449e8683f8833c5b26a` | MIT |
| nativejson-benchmark corpus (Milo Yip) | Benchmark inputs: `canada.json`, `citm_catalog.json`, `twitter.json` | https://github.com/miloyip/nativejson-benchmark/tree/master/data | `478d5727c2a4048e835a29c65adecc7d795360d5` | MIT (repository) |

Provenance of the corpus files, as given by nativejson-benchmark:
- `canada.json`: contour of the Canada border in GeoJSON, originally from https://github.com/mloskot/json_benchmark.
- `citm_catalog.json`: a Java JSON-parser benchmark file, originally from https://github.com/RichardHightower/json-parsers-benchmark.
- `twitter.json`: public tweets from a Twitter search for "一". It contains public user content, which is one more reason it is downloaded rather than committed here.

Reference implementation: System.Text.Json from https://github.com/dotnet/runtime (MIT), the copy that ships with the .NET 10 SDK.

### How to cite

> Seriot, N. *JSONTestSuite: a comprehensive test suite for RFC 8259 compliant JSON parsers.* https://github.com/nst/JSONTestSuite (commit 1ef36fa).
>
> Yip, M. *nativejson-benchmark.* https://github.com/miloyip/nativejson-benchmark (commit 478d572).
