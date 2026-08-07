#!/usr/bin/env bash
# End-to-end smoke test for the MCP server over its real stdio transport, no VS Code involved.
# Builds in demo (SQLite) mode, sends handwritten JSON-RPC requests on stdin, and checks that:
#   - stdout carries only protocol JSON (one JSON object per line)
#   - four tools are advertised, each with a description
#   - a SELECT returns rows
#   - a DELETE is rejected by the guard with a reason
#   - log output goes to stderr, never stdout
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLL="$ROOT_DIR/src/McpSqlServerTools/bin/Debug/net10.0/McpSqlServerTools.dll"
DB="$ROOT_DIR/demo/dealership.db"

if [[ ! -f "$DLL" ]]; then
    echo "Build first: dotnet build" >&2
    exit 1
fi

if [[ ! -f "$DB" ]]; then
    echo "Seed the demo db first: sqlite3 $DB < $ROOT_DIR/demo/seed.sql" >&2
    exit 1
fi

WORKDIR="$(mktemp -d)"
FIFO="$WORKDIR/stdin.fifo"
STDOUT_LOG="$WORKDIR/stdout.log"
STDERR_LOG="$WORKDIR/stderr.log"
mkfifo "$FIFO"

cleanup() {
    exec 3>&- 2>/dev/null || true
    [[ -n "${SERVER_PID:-}" ]] && kill "$SERVER_PID" 2>/dev/null || true
    rm -rf "$WORKDIR"
}
trap cleanup EXIT

# A real MCP client keeps stdin open for the life of the session; it never sends EOF after a
# single message. If we redirect from a plain file instead of a FIFO, our own shell closes stdin
# the instant the last byte is read, and the app's shutdown path can race ahead of flushing the
# response for whatever request was last in flight. The FIFO plus explicit fd control below keeps
# stdin open until we are done reading, so what we observe is what a real client would see.
MCP_DB_PROVIDER=Sqlite \
MCP_DB_CONNECTION="Data Source=$DB;Mode=ReadOnly" \
    dotnet "$DLL" < "$FIFO" > "$STDOUT_LOG" 2> "$STDERR_LOG" &
SERVER_PID=$!

exec 3>"$FIFO"
send() { printf '%s\n' "$1" >&3; sleep 0.3; }

send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke-test","version":"0.1"}}}'
send '{"jsonrpc":"2.0","method":"notifications/initialized"}'
send '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
send '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_tables","arguments":{}}}'
send '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"query","arguments":{"sql":"SELECT * FROM Dealerships"}}}'
send '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"query","arguments":{"sql":"DELETE FROM ServiceOrders"}}}'

exec 3>&-
kill "$SERVER_PID" 2>/dev/null || true
wait "$SERVER_PID" 2>/dev/null || true
SERVER_PID=""

echo "== stdout (protocol stream) =="
cat "$STDOUT_LOG"
echo
echo "== stderr (logs) =="
cat "$STDERR_LOG"
echo

fail() { echo "FAIL: $1" >&2; exit 1; }

# Every non-empty stdout line must be valid JSON. A stray Console.WriteLine anywhere in the
# process would show up here as a non-JSON line and corrupt the protocol stream.
while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    echo "$line" | python3 -c "import json,sys; json.load(sys.stdin)" \
        || fail "non-JSON line on stdout: $line"
done < "$STDOUT_LOG"

TOOLS_LIST=$(grep '"id":2' "$STDOUT_LOG")
echo "$TOOLS_LIST" | python3 -c "
import json, sys
tools = json.load(sys.stdin)['result']['tools']
assert len(tools) == 4, f'expected 4 tools, found {len(tools)}: {[t[\"name\"] for t in tools]}'
for t in tools:
    assert t.get('description'), f\"tool {t['name']} has no description\"
print('OK: 4 tools advertised, each with a description:', [t['name'] for t in tools])
"

QUERY_RESULT=$(grep '"id":4' "$STDOUT_LOG")
echo "$QUERY_RESULT" | python3 -c "
import json, sys
msg = json.load(sys.stdin)
text = msg['result']['content'][0]['text']
payload = json.loads(text)
assert payload['rowCount'] == 3, payload
print('OK: SELECT returned', payload['rowCount'], 'rows')
"

DELETE_RESULT=$(grep '"id":5' "$STDOUT_LOG")
echo "$DELETE_RESULT" | python3 -c "
import json, sys
msg = json.load(sys.stdin)
text = msg['result']['content'][0]['text']
payload = json.loads(text)
assert payload.get('rejected') is True, payload
assert payload.get('error'), payload
print('OK: DELETE rejected with reason:', payload['error'])
"

grep -q . "$STDERR_LOG" || fail "expected some log output on stderr"
echo "OK: logs present on stderr, nothing on stdout but protocol JSON"

echo
echo "Smoke test passed."
