#!/usr/bin/env python3
"""Claim 8: groups the replies that differ between two servers by command and reply shape.
Usage: classify_replay.py <commands.resp> <mismatches.txt> <dumpA> <dumpB> <nameA> <nameB>
The dumps are written by `Ladder.Resp.LoadGen replay ... <mismatches.txt> <dump>`."""
import collections
import re
import sys

commands_path, mism_path, dump_a, dump_b, name_a, name_b = sys.argv[1:7]
wanted = [int(l) for l in open(mism_path) if l.strip()]
wanted_set = set(wanted)


def commands():
    data = open(commands_path, "rb").read()
    pos, index = 0, 0
    while pos < len(data):
        assert data[pos:pos + 1] == b"*"
        end = data.index(b"\r\n", pos)
        argc = int(data[pos + 1:end])
        pos = end + 2
        args = []
        for _ in range(argc):
            end = data.index(b"\r\n", pos)
            n = int(data[pos + 1:end])
            args.append(data[end + 2:end + 2 + n])
            pos = end + 2 + n + 2
        yield index, args
        index += 1


def dumps(path):
    out = {}
    text = open(path, "rb").read()
    parts = re.split(rb"--- reply (\d+)\n", text)
    for i in range(1, len(parts), 2):
        out[int(parts[i])] = parts[i + 1][:-1]  # drop the newline the dump adds
    return out


a, b = dumps(dump_a), dumps(dump_b)
groups = collections.Counter()
examples = {}
for index, args in commands():
    if index not in wanted_set:
        continue
    shape = lambda r: r.split(b"\r\n", 1)[0][:60].decode("latin-1")
    key = (b" ".join(args[:1]).decode().upper(), shape(a[index]), shape(b[index]))
    groups[key] += 1
    examples.setdefault(key, (index, b" ".join(x[:40] for x in args[:3]).decode("latin-1")))

print(f"| Command | {name_a} reply | {name_b} reply | Count | First index | Example command |")
print("|---|---|---|---:|---:|---|")
for key, n in groups.most_common():
    i, ex = examples[key]
    print(f"| {key[0]} | `{key[1]}` | `{key[2]}` | {n} | {i} | `{ex}` |")
