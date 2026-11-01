#!/usr/bin/env python3
"""Claim 9 seeds: cuts captured RESP request streams into small seed files of a few whole commands each.
Usage: make_seeds.py <out dir> <files_per_stream> <commands_per_file> <stream.resp>...
Each stream contributes evenly spaced windows, so the seeds cover the stream's whole range of command shapes."""
import os
import sys

out, per_stream, per_file, streams = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4:]
os.makedirs(out, exist_ok=True)


def frames(data):
    pos = 0
    while pos < len(data):
        start = pos
        end = data.index(b"\r\n", pos)
        argc = int(data[pos + 1:end])
        pos = end + 2
        for _ in range(argc):
            end = data.index(b"\r\n", pos)
            pos = end + 2 + int(data[pos + 1:end]) + 2
        yield data[start:pos]


for s in streams:
    cmds = list(frames(open(s, "rb").read()))
    step = max(1, len(cmds) // per_stream)
    name = os.path.splitext(os.path.basename(s))[0]
    for k, i in enumerate(range(0, len(cmds), step)):
        if k >= per_stream:
            break
        with open(os.path.join(out, f"{name}-{k:03d}.resp"), "wb") as f:
            f.write(b"".join(cmds[i:i + per_file]))
print(len(os.listdir(out)), "seed files in", out)
