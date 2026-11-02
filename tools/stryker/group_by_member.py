#!/usr/bin/env python3
"""Adds the enclosing method to each row of judge_survivors.py's CSVs and counts verdicts per method.

Usage: group_by_member.py <source dir> <out.csv> <in.csv>...
The enclosing method is the nearest preceding member declaration (a line that declares a method, constructor or
property at class-member indentation), found by a regex. That is enough for this codebase's one-member-per-block
style. It is not a C# parser."""
import collections
import csv
import os
import re
import sys

src, out_csv, inputs = sys.argv[1], sys.argv[2], sys.argv[3:]
member = re.compile(r"^    (?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|readonly|override|async|unsafe|sealed|partial)\s+)+[\w<>\[\],.? ]+?\b(\w+)\s*(?:<[^>]*>)?\s*(?:\(|$|\{|=>)")
files = {}
for root, _, names in os.walk(src):
    for n in names:
        if n.endswith(".cs") and "/obj" not in root and "/bin" not in root:
            files[n] = open(os.path.join(root, n), encoding="utf-8").read().split("\n")


def enclosing(file, line):
    lines = files[file]
    for i in range(min(line, len(lines)) - 1, -1, -1):
        m = member.match(lines[i])
        if m:
            return m.group(1)
    return "(file scope)"


# A mutant judged twice (two workers met in the middle) keeps its first row.
first = {}
for f in inputs:
    for r in csv.DictReader(open(f)):
        first.setdefault(r["id"], r)
rows = list(first.values())
for r in rows:
    r["member"] = f'{r["file"].removesuffix(".cs")}.{enclosing(r["file"], int(r["line"]))}'
rows.sort(key=lambda r: (r["file"], int(r["line"]), int(r["id"])))
with open(out_csv, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=["id", "file", "line", "member", "stryker", "mutator", "replacement", "judge", "seconds", "detail"])
    w.writeheader()
    w.writerows(rows)

count = collections.defaultdict(collections.Counter)
for r in rows:
    count[r["member"]][r["judge"]] += 1
verdicts = ["KilledByJudge", "SurvivesJudge", "JudgeTimeout", "CompileError"]
print("| Member | " + " | ".join(verdicts) + " |")
print("|---|" + "---:|" * len(verdicts))
for m, c in sorted(count.items(), key=lambda kv: -kv[1]["SurvivesJudge"]):
    print(f"| `{m}` | " + " | ".join(str(c[v]) for v in verdicts) + " |")
