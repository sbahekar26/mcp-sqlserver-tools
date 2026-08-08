# mcp-sqlserver-tools

An [MCP](https://modelcontextprotocol.io) server in C# that exposes a SQL Server database to an
LLM client as a set of read-only tools. Built to answer a specific question: what does it take to
let a model query a production-shaped database without giving it a way to change anything?

The interesting part is not the plumbing — the MCP C# SDK handles that. It is the safety layer:
model-authored SQL is parsed into a T-SQL abstract syntax tree and admitted only if it is exactly
one `SELECT`, with no `INTO`, no `EXEC`, and no `OPENROWSET`. That is an allow-list over an AST,
not a keyword denylist over a string, so a statement type the parser does not recognise as a plain
`SELECT` fails closed.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_tables` | Every visible table with schema and approximate row count |
| `describe_table` | Columns, types, nullability, primary key, foreign keys |
| `sample_rows` | A capped sample so the model can see the data shape before querying |
| `query` | One read-only `SELECT`, row- and byte-capped |

## Running it

**Demo mode — no SQL Server needed.** This is the fastest way to see it work:

```bash
sqlite3 demo/dealership.db < demo/seed.sql
dotnet build
```

Then in VS Code, open the Command Palette → **MCP: List Servers** → start `sqlserver-tools-demo`.
Open Copilot Chat in Agent mode and ask something like *"which dealerships have open service
orders and how many labour hours are outstanding?"* — the model will call `list_tables`, then
`describe_table`, then compose a `SELECT`.

**Against SQL Server.** Start the `sqlserver-tools` server instead; VS Code will prompt for a
connection string and keep it out of the workspace file. Point it at a login granted
`db_datareader` and nothing else — the guard is defence in depth, not the only defence.

## Configuration

All configuration is environment variables, so no connection string ever reaches source control.

| Variable | Default | Notes |
| --- | --- | --- |
| `MCP_DB_PROVIDER` | `SqlServer` | `SqlServer` or `Sqlite` |
| `MCP_DB_CONNECTION` | *(required)* | Use a read-only login |
| `MCP_MAX_ROWS` | `200` | Hard ceiling; a tool call asking for more is clamped |
| `MCP_MAX_RESPONSE_BYTES` | `262144` | Protects the client's context window |
| `MCP_COMMAND_TIMEOUT` | `15` | Seconds |
| `MCP_AUDIT_PATH` | *(unset)* | JSON Lines audit file. Unset means the audit trail goes to stderr, not that it is off |
| `MCP_AUDIT_FAIL_OPEN` | `false` | If the audit sink throws, the default is to fail the tool call. Set `true` to log a warning and let the call through instead |
| `MCP_REDACTION_CONFIG` | *(unset)* | Path to a JSON column-redaction config. Unset means redaction is off, logged as a warning at startup |

## Design notes

**Logging goes to stderr, always.** With the stdio transport, stdout *is* the protocol stream.
A single `Console.WriteLine` corrupts it, and the failure surfaces on the client as an opaque JSON
parse error. `Program.cs` sets `LogToStandardErrorThreshold` to `Trace` for this reason.

**The guard only polices model-authored SQL.** Catalogue queries in `Db/Dialect.cs` are fixed and
parameterised, written by us, and go straight to the gateway. Running our own queries through a
guard designed for untrusted input would be theatre.

**Caps are enforced in one place.** `SqlGateway.ExecuteAsync` is the only path to the database, and
it applies the row cap, the byte cap and the timeout on every call. A new tool cannot forget them.

**Truncation is reported, not hidden.** When a result is cut short the response says so and says
why, so the model can narrow the query rather than silently reasoning over a partial answer.

**Two guards, one interface.** SQL Server gets AST parsing via `ScriptDom`. SQLite has no
equivalent parser available, so it gets a conservative tokeniser that strips comments and string
literals *before* checking keywords — otherwise `SELECT 'do not delete this row'` would be rejected
and `SELECT 1; /* */ DROP TABLE x` might not be.

**Two redactors, one interface — same split as the guards, for the same reason.**
`IColumnRedactor` has an `AstColumnRedactor` (SQL Server) and a `NameOnlyColumnRedactor` (SQLite).
The AST-based one is strictly more capable — it resolves aliases and `SELECT *` correctly — but
requires the same parser SQLite doesn't have. See [Redaction](#redaction).

## Audit log

Every call to `list_tables`, `describe_table`, `sample_rows` or `query` produces exactly one
JSON Lines record — one JSON object per line, append-only — whether it was allowed, rejected by
the guard, or errored. Rejections are the point: this is the log that answers "who asked what,
and can you prove it," and a rejected attempt is at least as interesting as a successful one.

```json
{"timestamp":"2026-08-07T14:02:37.902Z","sessionId":"7e2f9a1c4b6d4e2f8a1c4b6d4e2f8a1c","tool":"query","statement":"DELETE FROM ServiceOrders WHERE Status = 'closed'","outcome":"rejected","rejectionReason":"Only SELECT is permitted; statement begins with 'delete'.","truncated":false,"elapsedMs":1,"provider":"Sqlite"}
```

More examples, including an `allowed` and an `error` record, are in
[`demo/audit-sample.jsonl`](demo/audit-sample.jsonl).

**The record never carries result data.** `AuditRecord` (`src/McpSqlServerTools/Audit`) has no
field that can hold a row or a field value — `rowCount` is the only number that survives from a
query result, everything else is metadata about the request. An audit log that contains the data
it is auditing is a second, unaccounted-for copy of the sensitive thing.

**Fails closed by default.** If the sink can't write — disk full, bad path, permissions — the
tool call fails with an explanatory error rather than completing unrecorded. An audit trail with
silent gaps is worse than no audit trail, because it looks complete. `MCP_AUDIT_FAIL_OPEN=true`
is the explicit, named override: the call goes through and a warning is logged instead.

**Audited in a decorator around the tools, not in `SqlGateway.ExecuteAsync`.** A rejected
statement never reaches the gateway — the guard stops it first — and `list_tables` /
`describe_table` never go through the guard at all, so a gateway-level hook would miss exactly
the outcomes that matter most. `Audit/ToolAudit.RunAsync` is the single place every tool method
routes through instead, so a new tool can't forget to be audited without also forgetting how to
return a result.

**The audit file is separate from `ILogger`,** even when both happen to point at stderr (the
default, `MCP_AUDIT_PATH` unset). One is human-readable operational logging; the other is an
append-only compliance record with a fixed schema. Conflating them would mean a log-format change
silently breaking whatever ingests the audit trail.

## Redaction

Masking a column's *output* is not enough on its own. If `Email` is masked but the model can
still run `SELECT COUNT(*) FROM Customers WHERE Email = 'someone@example.com'`, it can confirm
whether an address exists in the table, one guess at a time — the predicate leaks what the output
mask was supposed to hide. So there are two enforcement points, not one:

1. **Output masking**, in `SqlGateway.ExecuteAsync`. Selecting a redacted column is allowed; the
   value comes back masked.
2. **Predicate rejection**, in the guard. Using a redacted column in `WHERE`, `JOIN ON`,
   `GROUP BY`, `HAVING` or `ORDER BY` is not allowed — the whole query is rejected, naming the
   column, before it runs.

Configure it with `MCP_REDACTION_CONFIG`, e.g. [`demo/redaction.json`](demo/redaction.json):

```json
{
  "redactions": [
    { "table": "Customers", "column": "Email",  "strategy": "mask" },
    { "table": "Customers", "column": "Phone",  "strategy": "hash" },
    { "table": "Vehicles",  "column": "Vin",    "strategy": "partial", "keepLast": 4 }
  ]
}
```

Table and column names match case-insensitively. Three strategies:

| Strategy | Output |
| --- | --- |
| `mask` | Fixed placeholder (`[REDACTED]`) |
| `hash` | Stable, unsalted SHA-256 prefix — equal inputs always hash equal, so a join or a `GROUP BY` on the *hashed* column still works without the model ever seeing the real value |
| `partial` | The last `keepLast` characters, everything before them replaced with `*` |

```
$ query: SELECT Name, ContactEmail, ContactPhone FROM Dealerships WHERE Id = 1
{"columns":["Name","ContactEmail","ContactPhone"],"rowCount":1,
 "rows":[["Lakeshore Motors","[REDACTED]","139c5834a7edf12d"]], ...}
```

**Output masking is resolved from the AST, not the result column name.** `reader.GetName(i)`
reports whatever the query aliased a column to, so a naive "mask any output column literally
named Email" check misses `SELECT Email AS e`. `AstColumnRedactor` parses the statement with
ScriptDom, walks the FROM clause to map aliases back to real table names, and traces every plain
SELECT-list item (including a qualified one like `c.Email`) back to its `(table, column)` before
deciding whether to mask it. `SELECT *` has no named columns in the AST at all — that case falls
back to matching the reader's column name against whichever tables are in scope for the star.

**Provider asymmetry — SQL Server gets both protections, SQLite gets one.** Predicate rejection
needs the same AST that output masking uses; `ScriptDomReadOnlyGuard` (SQL Server) has it,
`ConservativeReadOnlyGuard` (SQLite, see [Design notes](#design-notes)) is a keyword tokenizer
with no table/column resolution at all. So on SQLite: output masking still runs, but as a
name-only match with no AST to trace an alias through (`NameOnlyColumnRedactor`), and
`WHERE Email = 'x'` is **not** rejected — it runs and comes back masked, which does not stop the
equality check from confirming the guess. The server logs a warning about this at startup
whenever redaction is configured on the SQLite provider. See Known limits.

**The audit log never records the value being redacted, or the guess against it.**
[`AuditRecord`](src/McpSqlServerTools/Audit/AuditRecord.cs) has no field that can hold a row
value, so masked output was already safe. A rejected predicate is the harder case: the literal
being compared *is* the sensitive guess ("does `Email = 'someone@example.com'` match anything?"),
so logging the raw statement would defeat the rejection. The guard returns a sanitized copy of
the statement — the whole offending clause blanked out, not just the literal, since carving out
exactly the literal for every predicate shape (`IN`, `LIKE`, `BETWEEN`, `IS NULL`, ...) is a lot
of casework for a query that never runs — and `QueryTools` logs that instead of the original.

## Tests

```bash
dotnet test
```

The guard tests are the ones that matter. They cover the bypasses that a naive implementation gets
wrong: keywords inside string literals, keywords inside comments, keywords as bracketed
identifiers, `SELECT ... INTO`, `EXEC` inside an otherwise valid select, and semicolon-separated
batches with the write in second position.

## Known limits

- **The guard governs SQL arriving through this server. It does not constrain the agent calling
  it.** An assistant that also holds a shell, filesystem access, or a second database client can
  reach the same data without ever invoking these tools — which is what happened the first time
  this was demoed: the agent ran `sqlite3` against the demo database directly instead of calling
  `query`. In a real deployment the enforcing boundary is the database credential and the network
  path, not this guard; the guard reduces what a compromised or confused caller can do through
  this channel, and nothing more.
- `sample_rows` interpolates a quoted table name because a table name cannot be a bound parameter.
  The identifier is delimiter-escaped and the resulting statement is still passed through the
  guard, but a bound parameter would be stronger if the provider allowed it.
- Row estimates on SQL Server come from `sys.partitions` and are approximate by design.
- No result caching. Repeated identical queries hit the database each time.
- **Redaction predicate rejection is SQL-Server-only.** SQLite has no AST parser in this project
  (see [Design notes](#design-notes)), so `WHERE`/`JOIN ON`/`GROUP BY`/`HAVING`/`ORDER BY` on a
  redacted column is not rejected there — only output masking applies. A startup warning is
  logged when this gap is actually relevant (redaction configured, provider is SQLite).
- **Redaction output masking on SQLite is name-only, with no alias resolution.**
  `SELECT Email AS e FROM Customers` is masked correctly on SQL Server (traced via the AST) but
  **not** on SQLite — the reader reports the column as `e`, and there is no parser on that
  provider left to trace `e` back to `Email`.
- **An unqualified column in a multi-table, no-alias-prefix query is not masked or checked.**
  `AstColumnRedactor` and the predicate guard resolve `c.Email` via the FROM-clause alias map, but
  a bare `Email` with more than one table in scope and no qualifier has no way to know which table
  it came from without a real binder/catalog. It is left alone rather than guessed at. Always
  qualifying joined columns avoids this; the demo does.
- **Only a bare column reference is traced for masking or predicate rejection.**
  `SELECT UPPER(Email)` or `WHERE Email LIKE '%x%'`'s wrapping expression is not specially
  understood — the column reference inside is still found by the predicate check (so the `LIKE`
  case is still rejected), but a *derived* SELECT-list value like `UPPER(Email)` is not masked,
  because there is no single source column to trace a function call back to.
- **The redaction hash strategy is unsalted.** Equal inputs must hash equal for joins/`GROUP BY`
  on the hash to work, but for a low-cardinality column (a phone number, a small zip-code range)
  that same property makes it brute-forceable offline — hash every candidate and compare. A keyed
  HMAC would close that at the cost of a secret to manage and hashes that no longer match anything
  computed outside this process; not implemented here.
- The AST-based redactor re-parses the SQL text independently of the read-only guard, so a
  `query`/`sample_rows` call parses the same statement twice. Fine at this scale; a shared
  per-request parse would remove the duplication if it ever mattered.
