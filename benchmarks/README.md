# Benchmarks

How much does DbToRestAPI cost per request, compared with writing the same endpoints by hand? This
folder answers that with a re-runnable script. It runs the same SQL through two servers and compares
throughput, latency, CPU per request and memory:

- **DbToRestAPI**, built from this repository, with the endpoints in [engine/sql.xml](engine/sql.xml).
- **A Minimal API** ([MinimalApi/Program.cs](MinimalApi/Program.cs)): the same endpoints written by
  hand with ADO.NET, typed records and System.Text.Json, the fastest way to write them without
  generated code. It answers the same JSON, and `run.ps1` checks that before it measures anything.

## Run it

You need the .NET 10 SDK and PowerShell 7.2 or later (`pwsh`). SQL Server is optional: give an
instance that accepts Windows authentication, and the script creates a `DbToRestApiBench` database
there.

```powershell
cd benchmarks
./run.ps1                                     # SQLite only
./run.ps1 -SqlServer 'localhost\SQLEXPRESS'   # SQLite and SQL Server
./run.ps1 -Scenario by_id,list_100 -Concurrency 1,8,64 -DurationSeconds 30
```

The report is written to `results/<date-time>.md`. Run it from a `pwsh` prompt: through
`pwsh -File`, a list such as `-Concurrency 1,32` arrives as one string.

## What each run does

1. Publishes both servers and the load generator in Release into `.out/` (ignored by git), and
   creates the data: 10,000 contacts, in a SQLite file and, with `-SqlServer`, in SQL Server.
2. Starts one server at a time, pinned to one logical CPU (`-ServerCpu`, default 2) with
   `DOTNET_PROCESSOR_COUNT=1`, as on a 1 vCPU machine. The load generator runs on the other CPUs,
   leaving out the server CPU's hyper-threading sibling.
3. Asks each server for every scenario once and compares the two answers. A difference is reported
   at the top of the results.
4. Loads each server for at least two minutes (`-ServerWarmupSeconds`), through every scenario, 10
   seconds each, and reports none of it. The .NET runtime compiles a hot method a second time,
   optimized, only once no new method has been compiled for a moment. Under full load on one CPU
   that takes about a minute, and until then a server runs at half its speed or less.
5. For each scenario, database and connection count: a warmup (3 s), then a measured run (10 s).
   The load generator keeps that many connections busy, each sending its next request as soon as
   the last one is answered.

| Scenario | Request | SQL |
|---|---|---|
| `ping` | `GET /bench/{db}/ping` | `SELECT 1` |
| `by_id` | `GET /bench/{db}/contacts/{id}`, a random id | one row by primary key |
| `list_100`, `list_1000` | `GET /bench/{db}/contacts?after=N&take=100` | a page of 100 or 1,000 rows |
| `insert` | `POST /bench/{db}/writes` with `{"name": "...", "amount": 12.5}` | one `INSERT`, answering the new id |

## What the numbers mean

- **req/s**: requests answered per second, from the given number of connections.
- **p50 / p99**: the time a request took, as the load generator saw it, for half and for 99% of
  the requests.
- **CPU per request**: the server process's CPU time during the measured run, divided by the
  requests it answered. On a 1 vCPU machine this is what limits throughput: 1,000,000 divided by
  it is the most requests per second one CPU can answer.
- **Memory**: the server's working set at the end of its runs and at its peak.

Everything runs on one machine, so the loopback network, the database and the load generator
share it with the server. Compare the two servers in one report, not numbers across machines.
Both servers log at the ASP.NET template's level (`Microsoft.AspNetCore` at `Warning`): logging
every request would cost more than the request itself.

## Results

The full tables are in [results/](results/). The latest run, from 32 connections, each server on
one logical CPU of a Ryzen 7 5800H laptop under Windows (each cell: DbToRestAPI / Minimal API):

| Scenario | SQLite req/s | SQLite CPU per request (µs) | SQL Server req/s | SQL Server CPU per request (µs) |
|---|---:|---:|---:|---:|
| `SELECT 1` | 10,221 / 33,395 | 98 / 29 | 6,541 / 16,451 | 151 / 59 |
| One row by id | 7,272 / 21,843 | 137 / 45 | 5,727 / 14,355 | 174 / 68 |
| 100 rows | 3,656 / 6,398 | 272 / 156 | 3,235 / 5,832 | 304 / 170 |
| 1,000 rows | 724 / 862 | 1,360 / 1,143 | 733 / 719 | 1,336 / 1,282 |
| Insert from a JSON body | see below | | 4,800 / 11,667 | 205 / 79 |

DbToRestAPI spends about 70 to 110 µs of CPU per request more than the hand-written endpoints, the
cost of what it does for every request: route matching, the API key, CORS, JWT and rate-limit
checks, and gathering parameters from every source. That matters most for small, fast queries: the
slower the query, the smaller the share. Each row adds little to that: a page of 1,000 rows costs
about 1.2 times the hand-written CPU on SQLite, and about the same on SQL Server. The engine's
working set stays near 150 MB under load.

SQLite inserts from 32 connections are left out of the table. SQLite lets one connection write at
a time, and Microsoft.Data.Sqlite waits 150 ms before it retries a write that found the database
locked. How often writes collide depends on how the operating system schedules the server's
threads, so the result varies from run to run: in this run DbToRestAPI answered 1,336 inserts a
second and the Minimal API 1,581, and in repeated 10-second runs of the 1.7.18 build the rate
went from 170 to 1,000. From one connection, where nothing collides, an insert runs at 84% of the
hand-written speed (1,410 / 1,685 a second).
