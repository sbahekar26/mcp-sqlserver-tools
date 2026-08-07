using System.ComponentModel;
using System.Text.Json;
using McpSqlServerTools.Audit;
using McpSqlServerTools.Db;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpSqlServerTools.Tools;

[McpServerToolType]
public sealed class SchemaTools(
    SqlGateway gateway,
    IAuditSink auditSink,
    ServerOptions options,
    ILogger<SchemaTools> logger)
{
    [McpServerTool(Name = "list_tables")]
    [Description("Lists every table in the database with its schema and approximate row count. " +
                 "Call this first to discover what is available before writing a query.")]
    public Task<string> ListTablesAsync(CancellationToken cancellationToken) =>
        ToolAudit.RunAsync(auditSink, options, logger, "list_tables", statement: null, async () =>
        {
            var result = await gateway.ExecuteAsync(
                gateway.Dialect.ListTables, parameters: null, rowLimit: null, cancellationToken);

            return AuditOutcome.Allowed(
                result.Rows.Count, result.RowsTruncated || result.BytesTruncated, SqlGateway.ToJson(result));
        });

    [McpServerTool(Name = "describe_table")]
    [Description("Returns the columns, data types, nullability, primary key and foreign keys " +
                 "for one table. Use this before writing a query so the column names are exact.")]
    public Task<string> DescribeTableAsync(
        [Description("Table name, optionally schema-qualified, e.g. 'dbo.Orders' or 'Orders'.")]
        string table,
        CancellationToken cancellationToken) =>
        ToolAudit.RunAsync(auditSink, options, logger, "describe_table", statement: null, async () =>
        {
            var parameters = new Dictionary<string, object?> { ["@table"] = table };

            var columns = await gateway.ExecuteAsync(
                gateway.Dialect.DescribeColumns, parameters, rowLimit: null, cancellationToken);

            if (columns.Rows.Count == 0)
            {
                // Not found is a legitimate, completed answer, not a rejection or a server
                // error, so it audits as allowed with a zero row count.
                var notFound = JsonSerializer.Serialize(new
                {
                    error = $"Table '{table}' was not found, or the connection has no rights to it. " +
                            "Call list_tables to see what is visible."
                });
                return AuditOutcome.Allowed(0, truncated: false, notFound);
            }

            var keys = await gateway.ExecuteAsync(
                gateway.Dialect.DescribeKeys, parameters, rowLimit: null, cancellationToken);

            var payload = JsonSerializer.Serialize(new
            {
                table,
                columns = Project(columns),
                keys = Project(keys)
            });

            return AuditOutcome.Allowed(columns.Rows.Count, truncated: false, payload);
        });

    private static List<Dictionary<string, object?>> Project(ResultSet result) =>
        result.Rows
            .Select(row => result.Columns
                .Select((name, i) => (name, value: row[i]))
                .ToDictionary(pair => pair.name, pair => pair.value))
            .ToList();
}
