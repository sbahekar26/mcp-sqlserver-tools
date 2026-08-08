namespace McpSqlServerTools;

public enum DbProvider { SqlServer, Sqlite }

/// <summary>
/// Read from environment variables so the connection string never lands in source control
/// or in the VS Code settings file. See .vscode/mcp.json for how it is prompted for.
/// </summary>
public sealed class ServerOptions
{
    public required DbProvider Provider { get; init; }
    public required string ConnectionString { get; init; }

    /// <summary>Hard cap on rows returned to the model, regardless of what it asks for.</summary>
    public int MaxRows { get; init; } = 200;

    /// <summary>Hard cap on serialised payload size, to protect the context window.</summary>
    public int MaxResponseBytes { get; init; } = 256 * 1024;

    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>File to append JSON Lines audit records to. Unset means stderr, not "off".</summary>
    public string? AuditPath { get; init; }

    /// <summary>
    /// Default is fail-closed: if the audit sink throws, the tool call fails rather than
    /// completing unrecorded. This is the explicit, named override for that default.
    /// </summary>
    public bool AuditFailOpen { get; init; }

    /// <summary>Path to the JSON redaction config. Unset means redaction is off.</summary>
    public string? RedactionConfigPath { get; init; }

    public static ServerOptions FromEnvironment()
    {
        var provider = Environment.GetEnvironmentVariable("MCP_DB_PROVIDER") ?? "SqlServer";
        var connectionString = Environment.GetEnvironmentVariable("MCP_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "MCP_DB_CONNECTION is not set. Point it at a login with db_datareader only.");

        return new ServerOptions
        {
            Provider = Enum.Parse<DbProvider>(provider, ignoreCase: true),
            ConnectionString = connectionString,
            MaxRows = ReadInt("MCP_MAX_ROWS", 200),
            MaxResponseBytes = ReadInt("MCP_MAX_RESPONSE_BYTES", 256 * 1024),
            CommandTimeoutSeconds = ReadInt("MCP_COMMAND_TIMEOUT", 15),
            AuditPath = Environment.GetEnvironmentVariable("MCP_AUDIT_PATH"),
            AuditFailOpen = ReadBool("MCP_AUDIT_FAIL_OPEN", false),
            RedactionConfigPath = Environment.GetEnvironmentVariable("MCP_REDACTION_CONFIG")
        };
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static bool ReadBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
