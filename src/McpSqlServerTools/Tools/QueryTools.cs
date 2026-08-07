using System.ComponentModel;
using System.Text.Json;
using McpSqlServerTools.Db;
using McpSqlServerTools.Safety;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpSqlServerTools.Tools;

[McpServerToolType]
public sealed class QueryTools(
    SqlGateway gateway,
    IReadOnlyGuard guard,
    ILogger<QueryTools> logger)
{
    [McpServerTool(Name = "query")]
    [Description("Runs a single read-only SELECT statement and returns the rows as JSON. " +
                 "Anything other than one SELECT is rejected. Results are capped, so filter " +
                 "and aggregate in SQL rather than asking for whole tables.")]
    public async Task<string> QueryAsync(
        [Description("One SELECT statement. No semicolon-separated batches, no INTO, no EXEC.")]
        string sql,
        [Description("Maximum rows to return. Clamped to the server's configured ceiling.")]
        int? maxRows,
        CancellationToken cancellationToken)
    {
        var verdict = guard.Validate(sql);

        if (!verdict.Allowed)
        {
            // Logged at warning so a rejected attempt is visible in the trace, not silent.
            logger.LogWarning("Rejected query: {Reason}", verdict.Reason);
            return JsonSerializer.Serialize(new { error = verdict.Reason, rejected = true });
        }

        try
        {
            var result = await gateway.ExecuteAsync(sql, parameters: null, maxRows, cancellationToken);
            logger.LogInformation(
                "Query returned {RowCount} rows in {ElapsedMs}ms", result.Rows.Count, result.ElapsedMs);

            return SqlGateway.ToJson(result);
        }
        catch (Exception ex)
        {
            // Return the message, not the stack trace: the model needs enough to fix its SQL
            // and nothing that leaks server internals.
            logger.LogError(ex, "Query failed");
            return JsonSerializer.Serialize(new { error = ex.Message, rejected = false });
        }
    }

    [McpServerTool(Name = "sample_rows")]
    [Description("Returns a small sample of rows from one table so the shape of the data is " +
                 "visible before writing a real query. Cheaper and safer than 'SELECT *'.")]
    public async Task<string> SampleRowsAsync(
        [Description("Table name, optionally schema-qualified.")] string table,
        [Description("How many rows to sample. Defaults to 5.")] int rows,
        CancellationToken cancellationToken)
    {
        var count = Math.Clamp(rows <= 0 ? 5 : rows, 1, 50);

        // The table name cannot be a bound parameter, so it is quoted rather than interpolated
        // raw, and the resulting statement is still put through the guard.
        var quoted = Quote(table, gateway.Options.Provider);

        var sql = gateway.Options.Provider == DbProvider.SqlServer
            ? $"SELECT TOP {count} * FROM {quoted}"
            : $"SELECT * FROM {quoted} LIMIT {count}";

        var verdict = guard.Validate(sql);
        if (!verdict.Allowed)
            return JsonSerializer.Serialize(new { error = verdict.Reason, rejected = true });

        var result = await gateway.ExecuteAsync(sql, parameters: null, count, cancellationToken);
        return SqlGateway.ToJson(result);
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
