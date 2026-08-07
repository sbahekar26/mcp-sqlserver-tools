using McpSqlServerTools;
using McpSqlServerTools.Db;
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

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SqlGateway>();
builder.Services.AddSingleton<IReadOnlyGuard>(_ => options.Provider switch
{
    DbProvider.SqlServer => new ScriptDomReadOnlyGuard(),
    _ => new ConservativeReadOnlyGuard()
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
