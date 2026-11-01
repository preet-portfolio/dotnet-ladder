valkey: Server v=7.2.13 sha=00000000:0 malloc=jemalloc-5.3.0 bits=64 build=d168676aca4db8ba
trace: twitter/cache-trace @ 1c503676d6972d4394a360e09162149a6c00f516, samples/2020Mar

## cluster022 (sha256 3ccd39328bd5dd6b…)
mapped: {'get': 780861, 'incr': 135765, 'set': 83268, 'delete': 106} skipped: {}
replayed 1000000 commands
replayed 1000000 commands
replies: 1000000 vs 1000000; mismatches: 0; first mismatch: none
Byte-identical.

## cluster014 (sha256 d129078c7fa63472…)
mapped: {'get': 519668, 'delete': 183848, 'set': 96484} skipped: {}
replayed 800000 commands
replayed 800000 commands
replies: 800000 vs 800000; mismatches: 0; first mismatch: none
Byte-identical.

What the replay exercised. Replayed separately into a fresh valkey-server and read back with `INFO stats` and `DBSIZE`:

| Sample | GET hits | GET misses | Error replies | Keys at end |
|---|---:|---:|---:|---:|
| cluster022 | 23,457 | 757,404 | 0 | 93,175 |
| cluster014 | 279,467 | 240,201 | 0 | 83,354 |

How much this tests:
- cluster022 starts cold, so most of its GETs miss. Its value is the 135,765 `INCRBY` replies.
- cluster014 hits on 54% of its GETs, so it compares real bulk-string values.
- Neither trace produced an error reply, so error *texts* are not covered here. The Valkey Tcl suite covers those.
- The comparison is against the Valkey 7.2.13 stand-in. 9.1.2 could not be built in the session.
