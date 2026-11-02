#!/usr/bin/env python3
"""Classifies every mutant that both Stryker and the judge left alive (02-resp/evidence/stryker.md).
Usage: classify.py judged-mutants.csv survivors-classified.csv
Mutants Stryker reported NoCoverage are "Untested": no unit test executes the line, and the 4 judged suite files do not
detect the change. They are not examined one by one for equivalence. Mutants Stryker reported Survived (executed by a
unit test, not detected) were each read against the source and classified below by id."""
import csv
import sys

EQ, PERF, GAP = "Equivalent", "Performance only", "Test gap"
R = {}


def put(ids, cls, why):
    for i in ids:
        R[str(i)] = (cls, why)


put([2786], EQ + " [Likely]", "Differs only when both keys are null; the dictionaries never pass null keys")
put([2792], PERF, "Every key hashes the same: dictionaries stay correct, just slow")
put([11], EQ, "Lengths are at most 512 MB (MaxBulkLength), so the int conversion never overflows")
put([13, 18], PERF, "Arena growth: a new arena is allocated one byte early, or with less spare room; Math.Max keeps it large enough")
put([33], EQ + " [Likely]", "Initial value of the per-command clock cache; BeginCommand resets it before any read")
put([35, 37], EQ + " [Likely]", "The clock is re-read inside one command instead of cached; it differs only if a command straddles a millisecond and compares the two reads")
put([81], EQ, "No command has arity 0")
put([2647, 2742, 2667, 2735, 2705, 2660, 2661, 2662], PERF, "Pipe thresholds, zero-byte receive, batching boundary or ConfigureAwait (no synchronization context): changes buffering, not bytes on the wire")
put([2648, 2653, 2674, 2751], EQ, "ConfigureAwait/useSynchronizationContext: the server has no synchronization context")
put([2655, 2673, 2750, 2756], GAP, "Connection teardown (socket dispose/close, pipe completion) removed: no test checks that a closed client is cleaned up")
put([2713], GAP + " (edge)", "Query-buffer limit off by one byte at 1 GB: no test sends exactly the limit")
put([2820, 2836, 2843, 2851, 2857, 2873, 2899, 562, 582, 2883], EQ + " [Likely]", "`>= 0` vs `> 0` on an expiry time: an expiry at Unix time 0 cannot be stored (a past EXPIREAT deletes the key)")
put([2835], GAP, "Upsert treats an expired entry as live, e.g. SET KEEPTTL / SET GET on an expired key")
put([2838, 2931, 548, 1282], GAP + " (edge)", "1 ms boundary: expiry exactly at `now`")
put([2872, 2874], GAP, "Volatile-key index wrong: lazy expiry still hides keys, but active expiry and DBSIZE after expiry differ; untested")
put([2825, 2826, 2937, 2938], GAP, "expired_keys counter in INFO: untested")
put([2869], GAP, "SetExpire's result ignored by callers' replies in a path no test reaches")
put([2887], GAP, "DEL/UNLINK count on an already-expired key: untested")
put([2915, 2919, 2923, 2925, 2943, 2946, 3135, 3137], PERF + " [Likely]", "Active-expiry sampling and time budget: changes how much memory expired keys hold, not what any command returns (lazy expiry hides them)")
put([2950], PERF, "Scan order rebuilt on every SCAN call instead of cached")
put([2957, 2958, 2959, 2960, 2961, 2965, 2966], GAP, "SCAN cursor iteration: SCAN is not in the unit tests or the judged suite files")
put([2990], EQ + " [Likely]", "RANDOMKEY's order tie-break for equal hashes of different keys; any order is valid")
put([2999], EQ, "`>>>` vs `>>` on a non-negative int sum")
put([3012, 3013, 3014], EQ + " [Likely]", "Stable hash used only to order SCAN/RANDOMKEY; any consistent hash satisfies their guarantees")
put([3036], GAP + " (edge)", "Glob recursion limit off by one at depth 1000")
put([3044], PERF, "Consecutive `*` no longer collapsed: same matches, more backtracking")
put([3080, 3081, 3084, 3087, 3088, 3094, 3101, 3120, 3121], GAP, "Glob classes, ranges and escapes (`[a-z]`, `[^x]`, `\\\\`): KEYS patterns in the tests use only `*` and `?`")
put([138, 139, 144, 145, 161, 162, 749, 750, 756, 759, 770, 772, 773, 774, 776, 777, 779, 780, 782, 783, 786, 790, 792, 799,
     2178, 2179, 2180, 2186, 2199, 2201, 2202, 2210, 2221, 2222, 2223, 2225, 2226, 2237, 2238, 2239, 2241, 2248], GAP,
    "Command table entry (name, arity, registration) for a command or argument count that no test sends")
put([181, 186], GAP, "EXPIREAT/PEXPIREAT with an NX/XX/GT/LT option: untested")
put([282], GAP, "COPY onto itself in another database: untested")
put([309, 320, 682, 687], GAP + " (edge)", "Database index range checks at int / database-count limits")
put([439, 444, 449, 454, 1989, 1999, 2012, 2020], GAP + " (edge)", "The same option given twice (e.g. `NX NX`) toggles it off: untested")
put([489, 492, 502, 1513, 1526, 1619, 3229], GAP + " (edge)", "Overflow bound off by one at the extreme value")
put([535, 543], GAP + " (edge)", "EXPIRE GT/LT with a time equal to the current one")
put([624], GAP, "OBJECT ENCODING after CONFIG SET set-max-listpack-entries: untested")
put([3145, 3154, 3155, 3156, 3157], GAP, "OBJECT ENCODING boundary (int vs embstr vs raw): untested at the limits")
put([3180, 3184, 3185, 3186, 3187], PERF, "String buffer growth policy; Math.Max keeps the buffer at least `length`")
put([3190], EQ, "Clears zero bytes when length equals the current length")
put([3193], EQ + " [Likely]", "The region past the logical length is already zero: buffers are freshly allocated and never shrink in place")
put([2506], EQ + " [Likely]", "Empty input falls through to the inline reader, which also reports Incomplete")
put([2514, 2537, 2555], GAP + " (edge)", "Parser limit off by one (1M arguments, 512 MB bulk, 64 KB inline): no test sends exactly the limit")
put([2584], GAP, "`*\\r\\n` (no digits) or a bad byte after `\\r` is accepted as a length: a protocol error Valkey reports is missed. The fuzzer's split-consistency oracle cannot see it")
put([2607, 2613], GAP, "The 18-digit cap on lengths is weakened or removed, so a 19+ digit length overflows instead of being refused")
put([2758, 2759], EQ + " [Likely]", "SO_REUSEADDR on the listener: affects only rebinding a port in TIME_WAIT")
put([2762], GAP, "CONFIG GET port: untested")
put([2776, 2781, 2784], EQ, "ConfigureAwait: no synchronization context")
put([2782, 2783, 2785], GAP, "Server shutdown cleanup: no test checks the listener and background tasks stop")
put([2641, 2642], PERF, "Size hint for the output buffer; GetSpan may return more")
put(list(range(705, 741)), GAP, "Default value of a CONFIG parameter: CONFIG GET of these keys is untested")
put([1230], EQ + " [Likely]", "expireIdx is never 0 (argument 0 is the command name)")
put([1672], GAP, "INCRBYFLOAT dropping the TTL: untested")
put([1695], EQ, "`>>>` vs `>>` on a non-negative offset")
put([1752, 1777, 1812, 1814, 1874, 1899, 1905], GAP, "LCS options and tie-breaking (IDX/MINMATCHLEN/WITHMATCHLEN, wrong types, size limit) beyond what the string suite checks")
put([1970, 2007, 2008], GAP + " (edge)", "SET option conflicts (IFEQ with NX/XX)")

rows = list(csv.DictReader(open(sys.argv[1])))
out = []
for r in rows:
    if r["judge"] != "SurvivesJudge":
        continue
    if r["stryker"] == "NoCoverage":
        cls, why = "Untested", "No unit test executes this line; the 4 judged suite files don't detect the change"
    else:
        cls, why = R.get(r["id"], ("UNCLASSIFIED", ""))
    out.append({**{k: r[k] for k in ("id", "file", "line", "member", "stryker", "mutator", "replacement")}, "class": cls, "why": why})
missing = [o["id"] for o in out if o["class"] == "UNCLASSIFIED"]
if missing:
    sys.exit(f"unclassified: {missing}")
with open(sys.argv[2], "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(out[0]))
    w.writeheader()
    w.writerows(out)
import collections
print(collections.Counter(o["class"] for o in out))
