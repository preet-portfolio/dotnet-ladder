#!/usr/bin/env python3
"""Re-tests Stryker's surviving and uncovered mutants against a project's external judge.

Stryker only runs test projects; a console judge (05's specification suite, 02's Valkey Tcl suite) cannot be one. This
applies each mutant from Stryker's mutation-report.json to the source, one at a time, runs the judge command, and
restores the file. A judge failure (non-zero exit) means the judge kills the mutant; a build failure is recorded
separately. Output: one CSV row per mutant.
Usage: judge_survivors.py <mutation-report.json> <out.csv> <timeout seconds> -- <judge command...>"""
import csv
import json
import subprocess
import sys
import time

report, out_csv, timeout = sys.argv[1], sys.argv[2], int(sys.argv[3])
judge = sys.argv[sys.argv.index("--") + 1:]
data = json.load(open(report))


def offset(text, line, column):
    lines = text.split("\n")
    return sum(len(l) + 1 for l in lines[:line - 1]) + column - 1


rows = []
todo = [(path, m) for path, f in data["files"].items() for m in f["mutants"] if m["status"] in ("Survived", "NoCoverage")]
print(f"{len(todo)} mutants to judge", flush=True)
for n, (path, m) in enumerate(todo, 1):
    original = open(path, encoding="utf-8").read()
    s, e = m["location"]["start"], m["location"]["end"]
    a, b = offset(original, s["line"], s["column"]), offset(original, e["line"], e["column"])
    mutated = original[:a] + m["replacement"] + original[b:]
    started = time.time()
    try:
        open(path, "w", encoding="utf-8").write(mutated)
        try:
            p = subprocess.run(judge, capture_output=True, text=True, timeout=timeout)
            out = p.stdout + p.stderr
            if "error CS" in out or "Build FAILED" in out or "The build failed" in out:
                verdict = "CompileError"
            else:
                verdict = "KilledByJudge" if p.returncode != 0 else "SurvivesJudge"
        except subprocess.TimeoutExpired:
            verdict = "JudgeTimeout"
    finally:
        open(path, "w", encoding="utf-8").write(original)
    rows.append({"id": m["id"], "file": path.split("/")[-1], "line": s["line"], "stryker": m["status"],
                 "mutator": m["mutatorName"], "replacement": m["replacement"].replace("\n", " ")[:80],
                 "judge": verdict, "seconds": round(time.time() - started), "detail": (out.strip().splitlines() or [""])[-1][:60] if verdict != "JudgeTimeout" else ""})
    print(n, rows[-1]["file"], rows[-1]["line"], verdict, flush=True)

with open(out_csv, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0]))
    w.writeheader()
    w.writerows(rows)
