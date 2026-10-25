#!/usr/bin/env python3
"""Builds site/ (a static progress page) from the evidence each project writes. Run after any step; nothing here is hand-edited output."""
import csv
import datetime
import glob
import html
import json
import os
import re
import shutil

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "site")
STATUS_LABEL = {"done": "Done", "in-progress": "In progress", "planned": "Planned"}


def read(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except FileNotFoundError:
        return None


def judge_summary(project):
    text = read(os.path.join(ROOT, project, "judge", "results", "jsontestsuite.txt"))
    if text is None:
        return None
    def grab(label):
        m = re.search(re.escape(label) + r":\s*(\d+)/(\d+)", text)
        return (int(m.group(1)), int(m.group(2))) if m else None
    mism = re.search(r"tree mismatches vs System.Text.Json \(both accepted\):\s*(\d+)", text)
    return {
        "y": grab("y_ accepted"),
        "n": grab("n_ rejected"),
        "i": grab("i_ accepted"),
        "mismatches": int(mism.group(1)) if mism else None,
    }


def micros(text):
    value, unit = text.replace(",", "").split()
    return float(value) * {"ns": 0.001, "μs": 1, "us": 1, "ms": 1000, "s": 1_000_000}[unit]


def benchmarks(project):
    paths = glob.glob(os.path.join(ROOT, project, "bench", "*", "BenchmarkDotNet.Artifacts", "results", "*-report.csv"))
    if not paths:
        return None
    rows = list(csv.DictReader(open(paths[0], encoding="utf-8")))
    files = {}
    for r in rows:
        files.setdefault(r["File"], []).append({
            "method": r["Method"],
            "mean_us": micros(r["Mean"]),
            "ratio": r["Ratio"],
            "allocated": r["Allocated"],
        })
    return files


def stryker(project):
    reports = sorted(glob.glob(os.path.join(ROOT, project, "StrykerOutput", "*", "reports", "mutation-report.md")))
    if not reports:
        return None
    text = read(reports[-1])
    m = re.search(r"final mutation score is\s*(\d+(?:\.\d+)?)\s*%", text)
    counts = {k: int(v) for k, v in re.findall(r"\|\s*([A-Za-z ]+?)\s*\|\s*(\d+)\s*\|", text)}
    return {"score": float(m.group(1)) if m else None, "path": reports[-1], "counts": counts}


def readme_section(project, heading):
    text = read(os.path.join(ROOT, project, "README.md"))
    if text is None:
        return []
    m = re.search(r"^## " + re.escape(heading) + r"\s*\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    if not m:
        return []
    return [line[2:].strip() for line in m.group(1).splitlines() if line.startswith("- ")]


def inline_md(text):
    """Escapes text, then renders `code` and **bold** spans, which is all the README bullets use."""
    out = html.escape(text)
    out = re.sub(r"`([^`]+)`", r"<code>\1</code>", out)
    return re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", out)


def copy_raw(project):
    """Copies every evidence file, the judge's results and the README; .md becomes .txt so browsers display it."""
    dest = os.path.join(OUT, project)
    os.makedirs(dest, exist_ok=True)
    links = []
    paths = [os.path.join(project, "README.md")]
    for folder in (os.path.join(project, "judge", "results"), os.path.join(project, "evidence")):
        full = os.path.join(ROOT, folder)
        if os.path.isdir(full):
            paths += [os.path.join(folder, f) for f in sorted(os.listdir(full)) if not f.startswith(".")]
    for rel in paths:
        src = os.path.join(ROOT, rel)
        if not os.path.isfile(src):
            continue
        name = os.path.basename(rel)
        if name.endswith(".md"):
            name = name[:-3] + ".txt"
        shutil.copyfile(src, os.path.join(dest, name))
        folder = "judge" if f"{os.sep}judge{os.sep}" in rel else "evidence" if f"{os.sep}evidence{os.sep}" in rel else ""
        links.append((f"{folder}/{os.path.basename(rel)}" if folder else os.path.basename(rel), f"{project}/{name}"))
    return links


def readme_claims(project):
    """Rows of the README's claims table: (number, claim, result markdown, state)."""
    text = read(os.path.join(ROOT, project, "README.md")) or ""
    m = re.search(r"^## Claims.*?\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    rows = []
    for line in (m.group(1).splitlines() if m else []):
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) == 3 and cells[0].isdigit():
            state = "held" if cells[2].startswith("**Held") else "failed" if cells[2].startswith("**Failed") else "info"
            rows.append((cells[0], cells[1], cells[2], state))
    return rows


def pill(state, text):
    return f'<span class="pill {state}">{html.escape(text)}</span>'


def bench_block(files):
    labels = {
        "Ladder_JsonTokenizer": "Ours: JsonTokenizer",
        "Stj_Utf8JsonReader": "Utf8JsonReader (baseline)",
        "Stj_Utf8JsonReader_plus_Utf8Validation": "Utf8JsonReader + UTF-8 check",
        "Utf8Validation_only": "UTF-8 check alone",
    }
    parts = []
    for name, rows in files.items():
        peak = max(r["mean_us"] for r in rows)
        bars = []
        for r in rows:
            width = 100 * r["mean_us"] / peak
            cls = "ours" if r["method"].startswith("Ladder") else "ref"
            alloc = "0 B" if r["allocated"] in ("-", "0 B") else r["allocated"]
            bars.append(
                f'<div class="bar-row"><span class="bar-label">{html.escape(labels.get(r["method"], r["method"]))}</span>'
                f'<span class="bar-track"><span class="bar {cls}" style="width:{width:.1f}%"></span></span>'
                f'<span class="bar-value">{r["mean_us"]:,.0f} µs</span>'
                f'<span class="bar-ratio">{html.escape(r["ratio"])}×</span>'
                f'<span class="bar-alloc">{alloc}</span></div>')
        parts.append(f'<figure class="bench"><figcaption>{html.escape(name)}</figcaption>'
                     f'<div class="bar-head"><span></span><span>Mean time per document, bars to scale within the file</span><span>Mean</span><span>Ratio</span><span>Alloc</span></div>'
                     + "".join(bars) + "</figure>")
    return "".join(parts)


def tiles(items):
    return '<div class="tiles">' + "".join(
        f'<div class="tile {st}"><span class="tile-value">{html.escape(v)}</span><span class="tile-label">{html.escape(k)}</span></div>'
        for k, v, st in items) + "</div>"


def json_blocks(project):
    blocks = []
    judge = judge_summary(project)
    if judge:
        blocks.append('<section><h3>External judge: JSONTestSuite @ 1ef36fa</h3>' + tiles([
            ("y_ must accept", f"{judge['y'][0]}/{judge['y'][1]}", "held" if judge["y"][0] == judge["y"][1] else "failed"),
            ("n_ must reject", f"{judge['n'][0]}/{judge['n'][1]}", "held" if judge["n"][0] == judge["n"][1] else "failed"),
            ("i_ accepted (implementation-defined)", f"{judge['i'][0]}/{judge['i'][1]}", "info"),
            ("Tree mismatches vs System.Text.Json", str(judge["mismatches"]), "held" if judge["mismatches"] == 0 else "failed"),
        ]) + "</section>")
    bench = benchmarks(project)
    if bench:
        blocks.append('<section><h3>Benchmarks: Apple M1, .NET 10.0.13</h3>' + bench_block(bench) + "</section>")
    return blocks


def resp_blocks(project):
    blocks = []
    summary = read(os.path.join(ROOT, project, "judge", "results", "summary.txt"))
    if summary:
        items = []
        for f, ok, n in re.findall(r"^(unit/\S+): (\d+)/(\d+) passed", summary, re.M):
            items.append((f, f"{ok}/{n}", "held" if ok == n else "info"))
        total = re.search(r"passes (\d+)/(\d+) = ([\d.]+)%", summary)
        geo = re.search(r"GeoNames differential: reference (\d+) keys.*?(IDENTICAL|\d+ differing lines)", summary)
        if total:
            items.insert(0, ("All 259 tests Valkey passes", f"{total.group(3)}%", "held" if float(total.group(3)) >= 90 else "failed"))
        if geo:
            items.append((f"GeoNames dump, {int(geo.group(1)):,} keys", "identical" if geo.group(2) == "IDENTICAL" else geo.group(2), "held" if geo.group(2) == "IDENTICAL" else "failed"))
        fails = re.findall(r"^    FAIL     (.*)$", summary, re.M)
        fail_list = "".join(f"<li>{html.escape(x)}</li>" for x in fails)
        blocks.append('<section><h3>External judge: Valkey 9.1.2 test suite, run against both servers</h3>' + tiles(items)
                      + (f'<details><summary>{len(fails)} failing tests</summary><ul class="fails">{fail_list}</ul></details>' if fails else "") + "</section>")
    raw = os.path.join(ROOT, project, "evidence", "throughput-raw.csv")
    cpu = os.path.join(ROOT, project, "evidence", "throughput-cpu.csv")
    if os.path.exists(raw):
        import statistics
        rows = list(csv.DictReader(open(raw, encoding="utf-8")))
        cpu_rows = list(csv.DictReader(open(cpu, encoding="utf-8"))) if os.path.exists(cpu) else []
        bars = []
        for test in ("SET", "GET"):
            v = statistics.median(float(r["rps"]) for r in rows if r["server"] == "valkey" and r["test"] == test)
            o = statistics.median(float(r["rps"]) for r in rows if r["server"] == "ladder" and r["test"] == test)
            for label, val, cls in ((f"valkey-server {test}", v, "ref"), (f"Ours {test}", o, "ours")):
                bars.append(f'<div class="bar-row"><span class="bar-label">{label}</span><span class="bar-track"><span class="bar {cls}" style="width:{100 * val / max(v, o):.1f}%"></span></span>'
                            f'<span class="bar-value">{val / 1e6:.2f} M/s</span><span class="bar-ratio">{val / v:.2f}×</span><span class="bar-alloc"></span></div>')
        cpu_note = ""
        if cpu_rows:
            vc = statistics.median(float(r["cpu_seconds"]) for r in cpu_rows if r["server"] == "valkey")
            oc = statistics.median(float(r["cpu_seconds"]) for r in cpu_rows if r["server"] == "ladder")
            cpu_note = tiles([("valkey-server CPU seconds / 2M requests", f"{vc:.2f}", "info"),
                              ("Our CPU seconds / 2M requests", f"{oc:.2f}", "failed" if oc > 2 * vc else "info"),
                              ("CPU cost relative to Valkey", f"{oc / vc:.1f}×", "failed" if oc > 2 * vc else "info")])
        blocks.append('<section><h3>Throughput (valkey-benchmark, 50 clients, pipeline 16) and CPU</h3><figure class="bench">'
                      '<div class="bar-head"><span></span><span>Median requests/sec of 3 alternating rounds</span><span>Rate</span><span>vs Valkey</span><span></span></div>'
                      + "".join(bars) + "</figure>" + cpu_note + "</section>")
    return blocks


PROJECT_BLOCKS = {"01-json": json_blocks, "02-resp": resp_blocks}


def project_detail(project):
    sections = []
    claim_rows = readme_claims(project)
    if claim_rows:
        sections.append('<section><h3>Claims, written before the code</h3><ul class="claims">' + "".join(
            f'<li class="claim">{pill(state, {"held": "Held", "failed": "Failed"}.get(state, "Open"))}'
            f'<div><strong>{inline_md(claim)}</strong><p>{inline_md(result)}</p></div></li>'
            for _, claim, result, state in claim_rows) + "</ul></section>")
    sections += PROJECT_BLOCKS.get(project, lambda _: [])(project)
    strk = stryker(project)
    if strk and strk["score"] is not None:
        score = f"{strk['score']:.1f}%"
        sections.append(f'<section><h3>Mutation testing: Stryker.NET</h3>{tiles([("mutation score", score, "info")])}</section>')
    surprises = readme_section(project, "What surprised us")
    if surprises:
        sections.append('<section><h3>What surprised us</h3><ul class="notes">' + "".join(f"<li>{inline_md(x)}</li>" for x in surprises) + "</ul></section>")
    links = copy_raw(project)
    if links:
        sections.append('<section><h3>Raw evidence</h3><ul class="links">' + "".join(
            f'<li><a href="{html.escape(href)}">{html.escape(label)}</a></li>' for label, href in links) + "</ul></section>")
    return "".join(sections)


def build():
    ladder = json.load(open(os.path.join(ROOT, "tools", "site", "ladder.json"), encoding="utf-8"))
    # Start clean but keep .vercel/, which links this folder to its Vercel project.
    os.makedirs(OUT, exist_ok=True)
    for entry in os.listdir(OUT):
        if entry != ".vercel":
            path = os.path.join(OUT, entry)
            shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)
    # The Vercel CLI can drop credentials (.env.local) next to .vercel/; never upload them.
    with open(os.path.join(OUT, ".vercelignore"), "w", encoding="utf-8") as f:
        f.write(".env*\n.gitignore\nartifact.html\n")
    rungs = []
    active = [p for p in ladder if p["status"] != "planned"]
    # The project being worked on comes first; finished ones follow, newest first.
    active.sort(key=lambda p: (p["status"] != "in-progress", -ladder.index(p)))
    for i, p in enumerate(ladder, 1):
        cls = "rung " + p["status"] + (" current" if p["status"] == "in-progress" else "")
        name = html.escape(p["name"])
        if p["status"] != "planned":
            name = f'<a href="#{p["id"]}">{name}</a>'
        rungs.append(
            f'<li class="{cls}"><span class="rung-no">{i:02d}</span><div class="rung-body">'
            f'<span class="rung-name">{name}</span>'
            f'<span class="rung-judge">Judge: {html.escape(p["judge"])}</span></div>'
            f'{pill(p["status"], STATUS_LABEL[p["status"]])}</li>')
    projects = []
    for p in active:
        no = ladder.index(p) + 1
        projects.append(
            f'<article class="project" id="{p["id"]}"><div class="current-head">'
            f'<span class="eyebrow">Rung {no:02d} · {html.escape(p["spec"])} · {STATUS_LABEL[p["status"]]}</span>'
            f'<h2>{html.escape(p["name"])}</h2><p>{html.escape(p["concept"])}</p></div>'
            f'{project_detail(p["id"])}</article>')
    done = sum(p["status"] == "done" for p in ladder)
    stamp = datetime.datetime.now().astimezone().strftime("%d %b %Y, %H:%M %Z")
    page = TEMPLATE.format(rungs="".join(rungs), done=done, total=len(ladder), projects="".join(projects), stamp=html.escape(stamp))
    with open(os.path.join(OUT, "index.html"), "w", encoding="utf-8") as f:
        f.write(DOCUMENT_HEAD + page + DOCUMENT_TAIL)
    with open(os.path.join(OUT, "artifact.html"), "w", encoding="utf-8") as f:
        f.write(page)
    print(f"wrote {OUT}/index.html and {OUT}/artifact.html")


# The page body starts at <title>: the claude.ai artifact wraps it in its own document skeleton,
# while the Vercel copy gets DOCUMENT_HEAD/DOCUMENT_TAIL around it.
DOCUMENT_HEAD = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
"""
DOCUMENT_TAIL = "</html>\n"

TEMPLATE = """<title>Infrastructure Ladder</title>
<meta name="claude:viewport-fit-sides" content="cover">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wdth,wght@75..100,500..800&family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500&display=swap">
<style>
/* Layout: a vertical ladder rail of ten rungs beside the evidence for the rung being climbed; stacks on phones. */
:root {{
  --bg: #f3f4f1; --surface: #ffffff; --ink: #1b2430; --muted: #5d6673; --line: #d9ddd6;
  --accent: #2448c4; --accent-soft: #e3e9fb;
  --good: #1f7a4d; --good-soft: #e1f1e8; --bad: #b3261e; --bad-soft: #f8e1df; --info-soft: #eceee9;
  --display: "Archivo", "Arial Narrow", system-ui, sans-serif;
  --body: "IBM Plex Sans", system-ui, -apple-system, "Segoe UI", sans-serif;
  --mono: "IBM Plex Mono", ui-monospace, "SF Mono", Menlo, monospace;
}}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) {{
  --bg: #12161c; --surface: #1a2029; --ink: #e6e9ee; --muted: #9aa3b0; --line: #2c3440;
  --accent: #8aa6ff; --accent-soft: #233056;
  --good: #6fcf9a; --good-soft: #17301f; --bad: #ff8a80; --bad-soft: #3a1c1a; --info-soft: #222a35; color-scheme: dark;
}} }}
:root[data-theme="dark"] {{
  --bg: #12161c; --surface: #1a2029; --ink: #e6e9ee; --muted: #9aa3b0; --line: #2c3440;
  --accent: #8aa6ff; --accent-soft: #233056;
  --good: #6fcf9a; --good-soft: #17301f; --bad: #ff8a80; --bad-soft: #3a1c1a; --info-soft: #222a35; color-scheme: dark;
}}
* {{ box-sizing: border-box; }}
body {{ margin: 0; background: var(--bg); color: var(--ink); font: 15px/1.55 var(--body);
  padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); padding-block: 28px 48px; }}
.wrap {{ max-width: 1120px; margin: 0 auto; display: grid; gap: 28px; }}
header {{ display: flex; flex-wrap: wrap; align-items: end; justify-content: space-between; gap: 12px 24px; border-bottom: 2px solid var(--ink); padding-bottom: 14px; }}
h1 {{ font: 800 clamp(30px, 5vw, 46px)/1 var(--display); font-stretch: 80%; letter-spacing: -0.01em; margin: 0; text-wrap: balance; }}
.sub {{ color: var(--muted); margin: 6px 0 0; max-width: 62ch; }}
.meta {{ font: 500 12px/1.4 var(--mono); color: var(--muted); text-align: right; }}
.meta strong {{ color: var(--ink); font-size: 20px; display: block; font-variant-numeric: tabular-nums; }}
.layout {{ display: grid; grid-template-columns: minmax(0, 330px) minmax(0, 1fr); gap: 28px; align-items: start; }}
@media (max-width: 860px) {{ .layout {{ grid-template-columns: minmax(0, 1fr); }} }}
.ladder {{ list-style: none; margin: 0; padding: 0; display: grid; gap: 0; border-left: 3px solid var(--ink); }}
.rung {{ display: grid; grid-template-columns: 40px minmax(0, 1fr) auto; gap: 10px; align-items: center; padding: 10px 10px 10px 0; border-bottom: 1px solid var(--line); }}
.rung-no {{ font: 600 13px var(--mono); color: var(--muted); text-align: center; position: relative; }}
.rung-no::before {{ content: ""; position: absolute; left: -3px; top: 50%; width: 14px; height: 3px; background: var(--ink); }}
.rung.current {{ background: var(--accent-soft); }}
.rung.current .rung-no {{ color: var(--accent); }}
.rung-body {{ display: grid; min-width: 0; }}
.rung-name {{ font: 700 16px/1.2 var(--display); font-stretch: 88%; }}
.rung-judge {{ font-size: 12.5px; color: var(--muted); }}
.rung.planned .rung-name {{ color: var(--muted); }}
.pill {{ font: 600 11px/1 var(--body); letter-spacing: 0.04em; text-transform: uppercase; padding: 5px 8px; border-radius: 999px; white-space: nowrap; background: var(--info-soft); color: var(--muted); }}
.pill.in-progress {{ background: var(--accent); color: var(--surface); }}
.pill.done, .pill.held {{ background: var(--good-soft); color: var(--good); }}
.pill.failed {{ background: var(--bad-soft); color: var(--bad); }}
main {{ display: grid; gap: 40px; min-width: 0; }}
.project {{ display: grid; gap: 18px; min-width: 0; scroll-margin-top: 16px; }}
.project + .project {{ border-top: 2px solid var(--ink); padding-top: 28px; }}
.rung-name a {{ color: inherit; text-decoration: none; }}
.rung-name a:hover {{ color: var(--accent); }}
details summary {{ cursor: pointer; color: var(--muted); font-size: 14px; }}
.fails {{ margin: 8px 0 0; padding-left: 18px; color: var(--muted); font-size: 13.5px; display: grid; gap: 2px; }}
.current-head {{ display: grid; gap: 4px; }}
.eyebrow {{ font: 600 12px var(--mono); letter-spacing: 0.06em; text-transform: uppercase; color: var(--accent); }}
h2 {{ font: 800 30px/1.1 var(--display); font-stretch: 85%; margin: 0; text-wrap: balance; }}
.current-head p {{ margin: 0; color: var(--muted); }}
section {{ background: var(--surface); border: 1px solid var(--line); border-radius: 6px; padding: 18px; display: grid; gap: 12px; min-width: 0; }}
h3 {{ font: 700 13px/1.3 var(--body); letter-spacing: 0.05em; text-transform: uppercase; margin: 0; color: var(--muted); }}
.claims, .notes, .links {{ list-style: none; margin: 0; padding: 0; display: grid; gap: 12px; }}
.claim {{ display: grid; grid-template-columns: 70px minmax(0, 1fr); gap: 12px; align-items: start; }}
.claim .pill {{ text-align: center; }}
.claim p {{ margin: 2px 0 0; color: var(--muted); font-size: 14px; }}
.tiles {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; }}
.tile {{ display: grid; gap: 4px; padding: 12px; border-radius: 4px; background: var(--info-soft); border-top: 3px solid var(--muted); }}
.tile.held {{ background: var(--good-soft); border-top-color: var(--good); }}
.tile.failed {{ background: var(--bad-soft); border-top-color: var(--bad); }}
.tile-value {{ font: 700 26px/1 var(--mono); font-variant-numeric: tabular-nums; }}
.tile-label {{ font-size: 12.5px; color: var(--muted); }}
.bench {{ margin: 0; display: grid; gap: 6px; overflow-x: auto; }}
.bench figcaption {{ font: 600 13px var(--mono); }}
.bar-head, .bar-row {{ display: grid; grid-template-columns: minmax(150px, 210px) minmax(120px, 1fr) 80px 56px 48px; gap: 10px; align-items: center; min-width: 520px; }}
.bar-head {{ font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; }}
.bar-label {{ font-size: 13px; }}
.bar-track {{ height: 12px; background: var(--info-soft); border-radius: 2px; overflow: hidden; }}
.bar {{ display: block; height: 100%; background: var(--muted); }}
.bar.ours {{ background: var(--accent); }}
.bar-value, .bar-ratio, .bar-alloc {{ font: 500 12.5px var(--mono); font-variant-numeric: tabular-nums; text-align: right; }}
.notes li {{ padding-left: 14px; border-left: 2px solid var(--line); }}
.links {{ grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 8px; }}
a {{ color: var(--accent); text-underline-offset: 3px; }}
a:focus-visible {{ outline: 2px solid var(--accent); outline-offset: 2px; }}
code {{ font: 13px var(--mono); background: var(--info-soft); padding: 1px 4px; border-radius: 3px; }}
.muted {{ color: var(--muted); margin: 0; }}
footer {{ font-size: 12.5px; color: var(--muted); }}
</style>
<div class="wrap">
  <header>
    <div>
      <h1>Infrastructure Ladder</h1>
      <p class="sub">Ten pieces of infrastructure rebuilt in .NET 10 from their specs. An outside test suite or reference implementation judges each one; nothing here is graded by its author. Source: <a href="https://github.com/preet-portfolio/dotnet-ladder">github.com/preet-portfolio/dotnet-ladder</a>.</p>
    </div>
    <div class="meta"><strong>{done}/{total}</strong>rungs complete</div>
  </header>
  <div class="layout">
    <ol class="ladder" aria-label="Projects">{rungs}</ol>
    <main>
      {projects}
    </main>
  </div>
  <footer>Generated by tools/site/build.py from the repo's evidence files · {stamp}</footer>
</div>
"""

if __name__ == "__main__":
    build()
