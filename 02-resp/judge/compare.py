"""Compares the Valkey suite runs (reference vs ours) test by test and the two GeoNames dumps, writes
results/summary.txt, and exits non-zero when claim 1 or claim 4 in the README does not hold."""
import collections
import re
import sys

results, ref_dump, our_dump, files = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
LINE = re.compile(r"^\[(ok|err|exception|skip|ignore)\]: (.*?)(?: \(\d+ ms\))?$")


def parse(path):
    """Returns {file: {"ok": Counter(name), "err": {name: first error line}, "exception": [text]}}."""
    out = {}
    current = None
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    for i, line in enumerate(lines):
        if line.startswith("=== "):
            current = line[4:].strip()
            out[current] = {"ok": collections.Counter(), "err": {}, "exception": []}
            continue
        m = LINE.match(line)
        if not m or current is None:
            continue
        kind, name = m.group(1), m.group(2)
        if kind == "ok":
            out[current]["ok"][name] += 1
        elif kind == "err":
            name = re.sub(r" in tests/\S+$", "", name)
            detail = lines[i + 1].strip() if i + 1 < len(lines) else ""
            out[current]["err"][name] = detail
        elif kind == "exception":
            out[current]["exception"].append(name + " " + (lines[i + 1].strip() if i + 1 < len(lines) else ""))
    return out


ref = parse(f"{results}/suite-reference.txt")
ours = parse(f"{results}/suite-ours.txt")

report = []
total_ref = total_pass = 0
per_file = {}
for f in files:
    r, o = ref.get(f), ours.get(f)
    if r is None:
        report.append(f"{f}: reference run missing")
        continue
    passed, failed, not_run = 0, [], []
    for name, count in r["ok"].items():
        got = min(count, o["ok"][name]) if o else 0
        passed += got
        for _ in range(count - got):
            if o and name in o["err"]:
                failed.append((name, o["err"][name]))
            else:
                not_run.append(name)
    n = sum(r["ok"].values())
    total_ref += n
    total_pass += passed
    per_file[f] = (passed, n)
    report.append(f"{f}: {passed}/{n} passed ({100 * passed / n:.1f}%), {len(failed)} failed, {len(not_run)} not run")
    for name, detail in failed:
        report.append(f"    FAIL     {name}\n             {detail[:200]}")
    for name in not_run:
        report.append(f"    NOT RUN  {name}")
    if o:
        for exc in o["exception"]:
            report.append(f"    EXCEPTION (aborted the rest of the file): {exc[:300]}")

overall = 100 * total_pass / total_ref if total_ref else 0
report.insert(0, f"Valkey 9.1.2 test suite, external mode, tags -needs:debug -needs:repl -needs:reset")
report.insert(1, f"Reference (real valkey-server) passing tests: {total_ref}")
report.insert(2, f"ladder-resp passes {total_pass}/{total_ref} = {overall:.1f}%")
report.insert(3, "")

ref_lines = open(ref_dump, "rb").read().splitlines()
our_lines = open(our_dump, "rb").read().splitlines()
diff = [(a, b) for a, b in zip(ref_lines, our_lines) if a != b]
geo_equal = ref_lines == our_lines
report.append("")
report.append(f"GeoNames differential: reference {len(ref_lines)} keys, ours {len(our_lines)} keys, "
              f"{'IDENTICAL' if geo_equal else f'{len(diff)} differing lines'}")
for a, b in diff[:20]:
    report.append(f"    ref:  {a[:160]!r}\n    ours: {b[:160]!r}")

claim1 = overall >= 90 and per_file.get("unit/type/incr", (0, 1))[0] == per_file.get("unit/type/incr", (0, 1))[1]
report.append("")
report.append(f"Claim 1 (>= 90% overall and 100% of unit/type/incr): {'HELD' if claim1 else 'FAILED'}")
report.append(f"Claim 4 (GeoNames dumps byte-for-byte identical): {'HELD' if geo_equal else 'FAILED'}")

text = "\n".join(report) + "\n"
open(f"{results}/summary.txt", "w", encoding="utf-8").write(text)
print(text)
sys.exit(0 if claim1 and geo_equal else 1)
