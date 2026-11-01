#!/usr/bin/env python3
"""Claim 6: fits a per-token-kind cost model (ns per token, separately for ours and Utf8JsonReader) from the
synthetic one-kind documents, then predicts each corpus file's time and gap from its token counts.

Inputs (all in 01-json/evidence/why-faster/): the BDN CSVs of TokenKindBenchmarks and AblationBenchmarks, and
token-counts.csv (from `Ladder.Json.Bench model`). Integer/double split of corpus numbers is passed in because
token-counts.csv counts numbers together (counted with Python's json parse_int/parse_float hooks).
Usage: cost_model.py <evidence/why-faster dir>"""
import csv
import os
import sys

d = sys.argv[1]
# integers, doubles in each corpus file (python -c json.load(..., parse_int=..., parse_float=...))
SPLIT = {"twitter.json": (2108, 1), "citm_catalog.json": (14392, 0), "canada.json": (46, 111080)}


def us(s):
    v, unit = s.replace(",", "").split()
    return float(v) * {"μs": 1.0, "ms": 1000.0, "ns": 0.001}[unit]


def load(name, key, methods):
    out = {}
    with open(os.path.join(d, name)) as f:
        for r in csv.DictReader(f):
            if r["Method"] in methods:
                out[r[key], methods[r["Method"]]] = us(r["Mean"])
    return out


kinds = load("Ladder.Json.Bench.TokenKindBenchmarks-report.csv", "Kind", {"Stj": "stj", "Ours": "ours"})
files = load("Ladder.Json.Bench.AblationBenchmarks-report.csv", "File",
             {"Stj_Utf8JsonReader": "stj", "Ladder_JsonTokenizer": "ours"})
counts = {}
with open(os.path.join(d, "token-counts.csv")) as f:
    for r in csv.DictReader(f):
        counts[r["doc"]] = {k: int(v) for k, v in r.items() if k != "doc"}

cost = {}
for impl in ("stj", "ours"):
    t = lambda k: kinds[k, impl] * 1000.0  # ns per document
    c = {}
    c["int"] = t("Integers") / counts["Integers"]["numbers"]
    c["double"] = t("Doubles") / counts["Doubles"]["numbers"]
    c["literal"] = t("Literals") / counts["Literals"]["literals"]
    c["container"] = t("EmptyContainers") / counts["EmptyContainers"]["containers"]
    c["ws_byte"] = (t("IndentedLiterals") - counts["IndentedLiterals"]["literals"] * c["literal"]) / counts["IndentedLiterals"]["whitespace_bytes"]
    # strings: fixed cost per string + cost per string byte, from the short- and long-string documents
    s1, b1, t1 = counts["ShortStrings"]["strings"], counts["ShortStrings"]["string_bytes"], t("ShortStrings")
    s2, b2, t2 = counts["LongStrings"]["strings"], counts["LongStrings"]["string_bytes"], t("LongStrings")
    det = s1 * b2 - s2 * b1
    c["string"] = (t1 * b2 - t2 * b1) / det
    c["string_byte"] = (s1 * t2 - s2 * t1) / det
    p = counts["Properties"]
    c["property"] = (t("Properties") - p["literals"] * c["literal"] - p["containers"] * c["container"]
                     - p["string_bytes"] * c["string_byte"]) / p["properties"]
    cost[impl] = c

print("## Fitted costs (ns)\n")
print("| Feature | Utf8JsonReader | Ours | Difference |")
print("|---|---:|---:|---:|")
for k in cost["stj"]:
    print(f"| {k} | {cost['stj'][k]:.3f} | {cost['ours'][k]:.3f} | {cost['stj'][k] - cost['ours'][k]:+.3f} |")


def predict(doc, impl):
    n, c = counts[doc], cost[impl]
    ints, dbls = SPLIT[doc]
    parts = {
        "numbers": ints * c["int"] + dbls * c["double"],
        "strings": n["strings"] * c["string"] + n["string_bytes"] * c["string_byte"],
        "properties": n["properties"] * c["property"],
        "literals": n["literals"] * c["literal"],
        "containers": n["containers"] * c["container"],
        "whitespace": n["whitespace_bytes"] * c["ws_byte"],
    }
    return parts


print("\n## Predicted vs measured (µs)\n")
print("| File | STJ predicted | STJ measured | Ours predicted | Ours measured | Gap predicted | Gap measured | Gap error |")
print("|---|---:|---:|---:|---:|---:|---:|---:|")
breakdown = {}
for doc in SPLIT:
    ps, po = predict(doc, "stj"), predict(doc, "ours")
    S, O = sum(ps.values()) / 1000, sum(po.values()) / 1000
    ms, mo = files[doc, "stj"], files[doc, "ours"]
    gp, gm = S - O, ms - mo
    breakdown[doc] = {k: (ps[k] - po[k]) / 1000 for k in ps}
    print(f"| {doc} | {S:,.0f} | {ms:,.0f} | {O:,.0f} | {mo:,.0f} | {gp:,.0f} | {gm:,.0f} | {(gp - gm) / gm:+.0%} |")

print("\n## Predicted gap by feature (µs)\n")
print("| File | " + " | ".join(next(iter(breakdown.values())).keys()) + " |")
print("|---|" + "---:|" * 6)
for doc, b in breakdown.items():
    print(f"| {doc} | " + " | ".join(f"{v:,.0f}" for v in b.values()) + " |")
