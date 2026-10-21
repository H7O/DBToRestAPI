# Multi-Query Chaining: design note

> This note explains how query chaining is built, for people working on the engine. To write a
> chained endpoint, read [docs/topics/14-query-chaining.md](docs/topics/14-query-chaining.md), the
> reference, and [AGENTS.md](AGENTS.md) rule 17.

An endpoint with several `<query>` elements runs them in order, each on its own connection, and
answers with the last one's result. A later query reads an earlier one's result with `{pq{column}}`
for a column of a one-row result, and `{pq{json}}` for the whole result as a JSON array. Request
values (`{{name}}`, `{j{name}}`, ...) stay readable in every query.

## How a chain runs

`ApiController.GetResultFromDbMultipleQueriesAsync`, in this order:

1. `QueryConfigurationParser.Parse` turns the endpoint's `<query>` elements into `QueryDefinition`s.
   Each one has its text, its connection (the `connection_string_name` attribute, else the
   endpoint's `<connection_string_name>`, else `default`), its timeout (the `db_command_timeout`
   attribute, else the endpoint's, else the global one, else the provider's default) and its JSON
   variable name (the `json_var` attribute, default `json`).
2. `<buffer_rows>` is checked before any query runs, so an invalid value is a `500` that runs nothing.
3. Each query but the last:
   1. gets a new connection;
   2. makes its `{http{ ... }http}` calls, then runs;
   3. is read to its second row (`ToChamberedEnumerableAsync(2)`):
      - with zero or one row, the row's named columns become `{pq{column}}` values, and the rows
        become a JSON array (`[]` for none) under the next query's `json_var`;
      - with two or more, every row is read and serialized into that JSON array, and no column
        values are added;
   4. has its reader closed and its connection disposed before the next query starts.
4. The last query runs through `GetResultFromDbFinalQueryAsync`, the same code as a single-query
   endpoint: `<response_structure>`, `<count_query>`, `<root_node>`, `<buffer_rows>` and `<cache>`
   apply to it alone. Its connection lives until the response ends, so its rows can stream.
5. An exception from any query is wrapped as `Query N of M failed: ...`. A status raised in SQL
   (`THROW 50404, ...`) is still found inside the wrapper and becomes that status, and a
   connection the database dropped becomes `503`.

The values of every earlier query accumulate. A column passed by query 1 is still there in query 3,
unless query 2 returned one row with a column of the same name, which replaces it.

## Why `{pq{}}`

The values of earlier queries are registered with the pattern
`(?<open_marker>\{\{|\{pq\{)(?<param>.*?)?(?<close_marker>\}\})` (`DefaultRegex.DefaultPreviousQueryVariablesPattern`),
so `{{column}}` reads them too. But `{{name}}` also matches the request's own values. When the
earlier query's column is `NULL`, or it returned zero rows or several, a request value with the same
name takes its place, so a caller could fill in a value the chain meant to compute. `{pq{name}}`
reads earlier queries only.

## Design decisions

1. **Values pass as parameters, never as SQL text.** A value from one database reaches the next as a
   bound parameter, through the same `DbQueryParams` as request values. So a chain needs no linked
   server, no `OPENQUERY` and no string building, and works between any two providers.
2. **One connection per query.** Each query may target another database. An intermediate connection
   is disposed as soon as its result is read.
3. **Intermediate results are read whole.** The engine has to know whether there was one row, and to
   serialize several, before the next query runs. Only the last query streams.
4. **`json_var` belongs to the receiving query.** The query that reads the JSON names it, so the name
   can say what the next query expects.
5. **A bare value isn't a row of columns.** A one-column result with no column name (an unaliased
   `SELECT COUNT(*)` on SQL Server) adds no column values: a string value would be parsed as JSON,
   and its keys would fill the next query's `{{name}}`. It is still in the JSON array (`[2]`).

## Limits

- The queries run one after another, never in parallel.
- No transaction spans two queries: each runs on its own connection.
- An intermediate result is held in memory, as JSON when it has several rows. Keep intermediate
  results small.
- `<count_query>` runs once, with the last query.

## See also

- [docs/topics/14-query-chaining.md](docs/topics/14-query-chaining.md): the reference, with examples.
- [docs/topics/13-databases.md](docs/topics/13-databases.md): connections and providers.
