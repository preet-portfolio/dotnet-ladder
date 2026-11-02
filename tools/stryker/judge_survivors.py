#!/usr/bin/env python3
"""Re-tests Stryker's surviving and uncovered mutants against a project's external judge.

Stryker only runs test projects; a console judge (05's specification suite, 02's Valkey Tcl suite) cannot be one. This
applies each mutant from Stryker's mutation-report.json to the source, one at a time, runs the judge command, and
restores the file. A judge failure (non-zero exit) means the judge kills the mutant; a build failure is recorded
separately. Output: one CSV row per mutant.
Usage: judge_survivors.py <mutation-report.json> <out.csv> <timeout seconds> -- <judge command...>
Environment: SHARD=i/k judges every k-th mutant starting at i (0-based); ROOT_FROM/ROOT_TO rewrite source paths, so a
shard can run in its own copy of the repo. The judge sees the mutant's Stryker status as STRYKER_STATUS. Rows are
appended to the CSV as they finish, and mutants already in it are skipped, so a run can be resumed."""
import csv
import glob
import json
import os
import signal
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
shard, shards = map(int, os.environ.get("SHARD", "0/1").split("/"))
fields = ["id", "file", "line", "stryker", "mutator", "replacement", "judge", "seconds", "detail"]
# DONE_GLOB names the CSVs of earlier runs (any sharding), so the work can be re-split.
done = {r["id"] for f in glob.glob(os.environ.get("DONE_GLOB", out_csv)) for r in csv.DictReader(open(f))}
if not os.path.exists(out_csv):
    csv.DictWriter(open(out_csv, "w", newline=""), fieldnames=fields).writeheader()
todo = [(p, m) for p, m in todo if m["id"] not in done][shard::shards]
print(f"{len(todo)} mutants to judge", flush=True)
for n, (path, m) in enumerate(todo, 1):
    path = path.replace(os.environ.get("ROOT_FROM", ""), os.environ.get("ROOT_TO", ""), 1) if os.environ.get("ROOT_FROM") else path
    original = open(path, encoding="utf-8").read()
    s, e = m["location"]["start"], m["location"]["end"]
    a, b = offset(original, s["line"], s["column"]), offset(original, e["line"], e["column"])
    mutated = original[:a] + m["replacement"] + original[b:]
    started = time.time()
    try:
        open(path, "w", encoding="utf-8").write(mutated)
        try:
            # Own process group: on a timeout the judge's server and suite must die too, or the next mutant's
            # suite would talk to this mutant's server.
            p = subprocess.Popen(judge, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                 env={**os.environ, "STRYKER_STATUS": m["status"]}, start_new_session=True)
            try:
                out, _ = p.communicate(timeout=timeout)
            except subprocess.TimeoutExpired:
                os.killpg(p.pid, signal.SIGKILL)
                p.communicate()
                raise
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
    with open(out_csv, "a", newline="") as f:
        csv.DictWriter(f, fieldnames=fields).writerow(rows[-1])
    print(n, rows[-1]["file"], rows[-1]["line"], verdict, flush=True)
