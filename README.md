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

## Tests

```bash
dotnet test
```

The guard tests are the ones that matter. They cover the bypasses that a naive implementation gets
wrong: keywords inside string literals, keywords inside comments, keywords as bracketed
identifiers, `SELECT ... INTO`, `EXEC` inside an otherwise valid select, and semicolon-separated
batches with the write in second position.

## Known limits

- `sample_rows` interpolates a quoted table name because a table name cannot be a bound parameter.
  The identifier is delimiter-escaped and the resulting statement is still passed through the
  guard, but a bound parameter would be stronger if the provider allowed it.
- Row estimates on SQL Server come from `sys.partitions` and are approximate by design.
- No result caching. Repeated identical queries hit the database each time.
