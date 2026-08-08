using McpSqlServerTools;
using McpSqlServerTools.Audit;
using McpSqlServerTools.Db;
using McpSqlServerTools.Redaction;
using McpSqlServerTools.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdio IS the MCP transport, so anything written to stdout corrupts the protocol stream.
// Every log line must go to stderr. This is the single most common way a stdio MCP
// server breaks, and it fails as an unreadable JSON parse error on the client side.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var options = ServerOptions.FromEnvironment();

// Loaded eagerly, same as the connection string: a malformed redaction file should fail the
// server at startup, not run silently with fewer protections than the operator configured.
var redactionConfig = RedactionConfig.LoadOrEmpty(options.RedactionConfigPath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(redactionConfig);
builder.Services.AddSingleton<SqlGateway>();
builder.Services.AddSingleton<IReadOnlyGuard>(_ => options.Provider switch
{
    DbProvider.SqlServer => new ScriptDomReadOnlyGuard(redactionConfig),
    _ => new ConservativeReadOnlyGuard()
});
builder.Services.AddSingleton<IColumnRedactor>(_ => options.Provider switch
{
    DbProvider.SqlServer => new AstColumnRedactor(redactionConfig),
    _ => new NameOnlyColumnRedactor(redactionConfig)
});

// The audit file is a separate stream from ILogger's stderr output, even when both happen
// to land on stderr (MCP_AUDIT_PATH unset) — one is a log, the other is a compliance record.
builder.Services.AddSingleton<IAuditSink>(_ => string.IsNullOrWhiteSpace(options.AuditPath)
    ? JsonlAuditSink.ForStandardError()
    : JsonlAuditSink.ForPath(options.AuditPath));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
if (redactionConfig.IsEmpty)
{
    // Never silently off: an operator scanning startup logs must be able to tell redaction
    // is not protecting anything without having to go read the source to find out.
    startupLogger.LogWarning(
        "Column redaction is OFF: MCP_REDACTION_CONFIG is not set (or its file defines no rules).");
}
else
{
    startupLogger.LogInformation(
        "Column redaction is ON: {Count} rule(s) loaded from {Path}.",
        redactionConfig.Rules.Count, options.RedactionConfigPath);

    if (options.Provider == DbProvider.Sqlite)
    {
        // ConservativeReadOnlyGuard has no parser, so it cannot trace a column reference back
        // to a table the way ScriptDomReadOnlyGuard does. Output masking still applies (by
        // result column name only), but WHERE Email = 'x' is not rejected on this provider —
        // it comes back masked, which does not stop the equality check from silently
        // confirming the guess.
        startupLogger.LogWarning(
            "Predicate protection for redacted columns is unavailable on the Sqlite provider: " +
            "WHERE / JOIN ON / GROUP BY / HAVING / ORDER BY on a redacted column will NOT be " +
            "rejected here. Only output masking applies.");
    }
}

await host.RunAsync();
