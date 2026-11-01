#!/usr/bin/env python3
"""Claim 8: turns a Twitter cache trace (twitter/cache-trace, Yang et al., OSDI 2020) into RESP commands.

Trace rows are: timestamp, anonymized key, key size, value size, client id, operation, TTL.
The trace has no values, only their sizes, so each write stores a deterministic value of the recorded size: decimal
digits that start with a non-zero digit, so INCR/DECR on a key a SET wrote is a valid integer up to 19 digits and a
real overflow error beyond. Mapping (memcached operation -> RESP command):

  get, gets         GET key
  set               SET key value [EX ttl]
  add               SET key value NX [EX ttl]
  replace, cas      SET key value XX [EX ttl]      (cas has no token in the trace; XX keeps its "only if present")
  append            APPEND key value
  prepend           counted and skipped: RESP has no prepend
  delete            DEL key
  incr, decr        INCRBY / DECRBY key 1

Requests are replayed in trace order, as fast as possible; timestamps are ignored. The TTLs of the clusters used
(1800 s and longer) cannot expire during a replay that lasts seconds, so expiry cannot make the servers differ.

Usage: trace_to_resp.py <trace.csv> <out.resp> <max_rows>
"""
import collections
import sys


def bulk(b):
    return b"$%d\r\n%s\r\n" % (len(b), b)


def command(*parts):
    return b"*%d\r\n" % len(parts) + b"".join(bulk(p) for p in parts)


def value(row_index, size):
    digits = str(row_index + 1).encode()
    out = (b"7" + digits) * (size // (len(digits) + 1) + 1)
    return out[:size]


def main(trace_path, out_path, max_rows):
    counts = collections.Counter()
    skipped = collections.Counter()
    with open(trace_path, "rb") as trace, open(out_path, "wb") as out:
        for i, line in enumerate(trace):
            if i >= max_rows:
                break
            fields = line.rstrip(b"\r\n").split(b",")
            # The key may itself contain commas; the last five fields are fixed, the first is the timestamp.
            key = b",".join(fields[1:-5])
            value_size, op, ttl = int(fields[-4]), fields[-2].decode(), int(fields[-1])
            expiry = [b"EX", str(ttl).encode()] if ttl > 0 else []
            if op in ("get", "gets"):
                out.write(command(b"GET", key))
            elif op == "set":
                out.write(command(b"SET", key, value(i, value_size), *expiry))
            elif op == "add":
                out.write(command(b"SET", key, value(i, value_size), b"NX", *expiry))
            elif op in ("replace", "cas"):
                out.write(command(b"SET", key, value(i, value_size), b"XX", *expiry))
            elif op == "append":
                out.write(command(b"APPEND", key, value(i, value_size)))
            elif op == "delete":
                out.write(command(b"DEL", key))
            elif op == "incr":
                out.write(command(b"INCRBY", key, b"1"))
            elif op == "decr":
                out.write(command(b"DECRBY", key, b"1"))
            else:
                skipped[op] += 1
                continue
            counts[op] += 1
    print("mapped:", dict(counts), "skipped:", dict(skipped))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]))
