#!/usr/bin/env python3
"""Summarises run-openloop.sh output: per server and load fraction, the median over rounds of each latency
percentile and of CPU microseconds per request, plus the spread (min-max) so noise is visible.
Usage: openloop_report.py <evidence dir>"""
import csv
import os
import statistics
import sys
from collections import defaultdict

d = sys.argv[1]
lat = defaultdict(list)
with open(os.path.join(d, "openloop-raw.csv")) as f:
    for row in csv.DictReader(f):
        _, server, frac = row["label"].split(":")
        lat[(server, float(frac))].append(row)
cpu = defaultdict(list)
rates = defaultdict(list)
with open(os.path.join(d, "openloop-cpu.csv")) as f:
    for row in csv.DictReader(f):
        cpu[row["server"], int(row["rate"])].append(float(row["cpu_us_per_request"]))
fractions = sorted({k[1] for k in lat})
servers = ["valkey", "garnet", "ladder"]


def med(rows, col):
    vals = [float(r[col]) for r in rows]
    return statistics.median(vals), min(vals), max(vals)


print("| Load | Server | Rate (req/s) | p50 µs | p99 µs | p99.9 µs | max µs | CPU µs/req | Late sends | Rounds |")
print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
for frac in fractions:
    for s in servers:
        rows = lat.get((s, frac), [])
        if not rows:
            continue
        rate_vals = [int(float(r["rate"])) for r in rows]
        cpus = [c for r in rate_vals for c in cpu.get((s, r), [])]
        cells = []
        for col in ("p50_us", "p99_us", "p999_us", "max_us"):
            m, lo, hi = med(rows, col)
            cells.append(f"{m:,.0f} ({lo:,.0f}–{hi:,.0f})")
        c = f"{statistics.median(cpus):.2f} ({min(cpus):.2f}–{max(cpus):.2f})" if cpus else "n/a"
        late = sum(int(r["sends_over_1ms_late"]) for r in rows)
        print(f"| {frac:.0%} | {s} | {statistics.median(rate_vals):,.0f} | " + " | ".join(cells) + f" | {c} | {late} | {len(rows)} |")
