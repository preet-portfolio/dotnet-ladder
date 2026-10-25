"""Dumps key<TAB>TYPE<TAB>hex(value) for every key, using valkey-cli in --pipe-free batches of MGET."""
import subprocess
import sys

port, keys_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
keys = [k for k in open(keys_path, "rb").read().split(b"\n") if k]
with open(out_path, "wb") as out:
    for i in range(0, len(keys), 500):
        batch = keys[i:i + 500]
        # --no-raw would quote; -x reads the last argument from stdin, so feed keys as arguments via RESP instead.
        raw = subprocess.run(["valkey-cli", "-p", port, "--raw", "--no-auth-warning", "-e", "MGET", *[k.decode() for k in batch]],
                             capture_output=True, check=True).stdout
        values = raw.split(b"\n")[: len(batch)]
        for k, v in zip(batch, values):
            out.write(k + b"\t" + v.hex().encode() + b"\n")
print(f"{len(keys)} keys dumped from port {port}")
