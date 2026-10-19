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
4. For each scenario, database and connection count: a warmup (3 s), then a measured run (10 s).
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
one logical CPU of a Ryzen 7 5800H laptop under Windows:

| Scenario | SQLite: DbToRestAPI / Minimal API req/s | SQL Server: DbToRestAPI / Minimal API req/s |
|---|---:|---:|
| `SELECT 1` | 3,597 / 28,918 | 5,400 / 11,199 |
| One row by id | 4,937 / 22,947 | 4,470 / 10,599 |
| 100 rows | 2,484 / 6,661 | 1,864 / 5,151 |
| 1,000 rows | 421 / 910 | 366 / 674 |
| Insert from a JSON body | 1,292 / 1,481 | 3,638 / 9,098 |

DbToRestAPI spends about 150 to 250 µs of CPU per request more than the hand-written endpoints, the
cost of what it does for every request: route matching, the API key, CORS, JWT and rate-limit
checks, gathering parameters from every source, and reading the result into dynamic rows. That
matters most for small, fast queries. The slower the query, the smaller the share: an insert on
SQLite, which waits on the database's write lock, runs at 87% of the hand-written speed. A result
of 1,000 rows costs about twice the hand-written CPU, in serializing its rows. The engine's working
set stays near 150 MB under load.
