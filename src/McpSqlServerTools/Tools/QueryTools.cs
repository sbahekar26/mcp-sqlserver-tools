using System.ComponentModel;
using System.Text.Json;
using McpSqlServerTools.Audit;
using McpSqlServerTools.Db;
using McpSqlServerTools.Safety;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpSqlServerTools.Tools;

[McpServerToolType]
public sealed class QueryTools(
    SqlGateway gateway,
    IReadOnlyGuard guard,
    IAuditSink auditSink,
    ServerOptions options,
    ILogger<QueryTools> logger)
{
    [McpServerTool(Name = "query")]
    [Description("Runs a single read-only SELECT statement and returns the rows as JSON. " +
                 "Anything other than one SELECT is rejected. Results are capped, so filter " +
                 "and aggregate in SQL rather than asking for whole tables.")]
    public Task<string> QueryAsync(
        [Description("One SELECT statement. No semicolon-separated batches, no INTO, no EXEC.")]
        string sql,
        [Description("Maximum rows to return. Clamped to the server's configured ceiling.")]
        int? maxRows = null,
        CancellationToken cancellationToken = default) =>
        ToolAudit.RunAsync(auditSink, options, logger, "query", sql, async () =>
        {
            var verdict = guard.Validate(sql);
            if (!verdict.Allowed)
            {
                // Logged at warning so a rejected attempt is visible in the trace, not silent.
                logger.LogWarning("Rejected query: {Reason}", verdict.Reason);
                return AuditOutcome.Rejected(verdict.Reason!,
                    JsonSerializer.Serialize(new { error = verdict.Reason, rejected = true }));
            }

            var result = await gateway.ExecuteAsync(sql, parameters: null, maxRows, cancellationToken);
            logger.LogInformation(
                "Query returned {RowCount} rows in {ElapsedMs}ms", result.Rows.Count, result.ElapsedMs);

            return AuditOutcome.Allowed(
                result.Rows.Count, result.RowsTruncated || result.BytesTruncated, SqlGateway.ToJson(result));
        });

    [McpServerTool(Name = "sample_rows")]
    [Description("Returns a small sample of rows from one table so the shape of the data is " +
                 "visible before writing a real query. Cheaper and safer than 'SELECT *'.")]
    public async Task<string> SampleRowsAsync(
        [Description("Table name, optionally schema-qualified.")] string table,
        [Description("How many rows to sample. Defaults to 5.")] int rows = 5,
        CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(rows <= 0 ? 5 : rows, 1, 50);

        // The table name cannot be a bound parameter, so it is quoted rather than interpolated
        // raw, and the resulting statement is still put through the guard.
        string quoted;
        try
        {
            quoted = Quote(table, gateway.Options.Provider);
        }
        catch (ArgumentException ex)
        {
            // A malformed table name never becomes SQL, so there is nothing to audit as a
            // statement — but the attempt itself still gets a record, same as a guard rejection.
            return await ToolAudit.RunAsync(auditSink, options, logger, "sample_rows", null, () =>
                Task.FromResult(AuditOutcome.Rejected(ex.Message,
                    JsonSerializer.Serialize(new { error = ex.Message, rejected = true }))));
        }

        var sql = gateway.Options.Provider == DbProvider.SqlServer
            ? $"SELECT TOP {count} * FROM {quoted}"
            : $"SELECT * FROM {quoted} LIMIT {count}";

        return await ToolAudit.RunAsync(auditSink, options, logger, "sample_rows", sql, async () =>
        {
            var verdict = guard.Validate(sql);
            if (!verdict.Allowed)
                return AuditOutcome.Rejected(verdict.Reason!,
                    JsonSerializer.Serialize(new { error = verdict.Reason, rejected = true }));

            var result = await gateway.ExecuteAsync(sql, parameters: null, count, cancellationToken);
            return AuditOutcome.Allowed(
                result.Rows.Count, result.RowsTruncated || result.BytesTruncated, SqlGateway.ToJson(result));
        });
    }

    private static string Quote(string table, DbProvider provider)
    {
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length is 0 or > 2)
            throw new ArgumentException("Table must be 'name' or 'schema.name'.", nameof(table));

        return provider == DbProvider.SqlServer
            ? string.Join('.', parts.Select(p => $"[{p.Replace("]", "]]")}]"))
            : string.Join('.', parts.Select(p => $"\"{p.Replace("\"", "\"\"")}\""));
    }
}
