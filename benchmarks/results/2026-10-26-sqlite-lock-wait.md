# SQLite inserts from 32 connections: the lock wait, 1.7.28 and 1.7.29

2026-10-26, on one machine, SQLite only, the `insert` scenario (`POST /bench/sqlite/writes`), each
server pinned to one logical CPU as in [the README](../README.md), warmed up for 60 s.

1.7.28 is the engine as released, which leaves SQLite's busy timeout at 0: a write that finds the
database locked fails at once inside Microsoft.Data.Sqlite, which sleeps 150 ms before each retry.
1.7.29 sets each query's busy timeout to its command timeout, so it waits inside SQLite, in steps of
1 to 100 ms. The Minimal API is unchanged between the runs and keeps the driver's defaults, so it
shows how much of a difference comes from the machine.

| Run | Connections | Seconds | DbToRestAPI req/s | p50 / p99 (ms) | Minimal API req/s | p50 / p99 (ms) |
|---|---:|---:|---:|---:|---:|---:|
| 1.7.28 | 32 | 10 | 146 | 71.02 / 3,522.64 | 162 | 1.08 / 4,981.83 |
| 1.7.29 | 32 | 10 | 268 | 1.27 / 2,983.08 | 311 | 0.66 / 3,356.83 |
| 1.7.28 | 32 | 20 | 425 | 0.75 / 73.74 | 684 | 0.61 / 31.04 |
| 1.7.29 | 32 | 20 | 1,265 | 0.69 / 68.61 | 749 | 0.89 / 41.05 |

In the first pair, both servers had requests waiting seconds, which no 150 ms retry explains: the
machine stalled every writer, and the pair shows little. In the second, without such stalls, the
engine went from 425 to 1,265 inserts a second while the unchanged Minimal API went from 684 to 749,
so the engine now answers more inserts than the hand-written API, which still sleeps 150 ms on each
collision.

`SqliteLockWaitTests` measures one collision directly: a write that waited for a lock goes on about
20 ms after its release (Windows; 10 to 28 ms on Linux), against about 150 ms without the busy
timeout.
