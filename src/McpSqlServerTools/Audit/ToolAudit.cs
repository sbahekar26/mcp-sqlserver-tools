using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace McpSqlServerTools.Audit;

/// <summary>What a guarded tool body decided, before the audit record is written.</summary>
public readonly record struct AuditOutcome(
    string Outcome,
    string? RejectionReason,
    string? ErrorMessage,
    int? RowCount,
    bool Truncated,
    string Payload)
{
    public static AuditOutcome Allowed(int rowCount, bool truncated, string payload) =>
        new("allowed", null, null, rowCount, truncated, payload);

    public static AuditOutcome Rejected(string reason, string payload) =>
        new("rejected", reason, null, null, false, payload);
}

/// <summary>
/// The single choke point every tool method calls through, so auditing an outcome is not
/// something a new tool has to remember to do by hand: it writes the record, and enforces
/// fail-closed — if the sink itself throws, the tool call fails instead of completing
/// unrecorded, unless MCP_AUDIT_FAIL_OPEN=true.
/// </summary>
public static class ToolAudit
{
    // A server process has exactly one session; a static field is simpler than threading an
    // id through DI for something that never varies within a run.
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    public static async Task<string> RunAsync(
        IAuditSink sink,
        ServerOptions options,
        ILogger logger,
        string tool,
        string? statement,
        Func<Task<AuditOutcome>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        AuditOutcome outcome;
        try
        {
            outcome = await action();
        }
        catch (Exception ex)
        {
            // Message only, never the stack trace: consistent with what the tool already
            // returns to the model, and the audit record should not leak more than the client sees.
            outcome = new AuditOutcome("error", null, ex.Message, null, false,
                JsonSerializer.Serialize(new { error = ex.Message, rejected = false }));
        }
        stopwatch.Stop();

        var record = new AuditRecord(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: SessionId,
            Tool: tool,
            Statement: statement,
            Outcome: outcome.Outcome,
            RejectionReason: outcome.RejectionReason,
            ErrorMessage: outcome.ErrorMessage,
            RowCount: outcome.RowCount,
            Truncated: outcome.Truncated,
            ElapsedMs: (int)stopwatch.ElapsedMilliseconds,
            Provider: options.Provider.ToString());

        try
        {
            await sink.WriteAsync(record);
        }
        catch (Exception auditEx) when (options.AuditFailOpen)
        {
            logger.LogWarning(auditEx,
                "Audit sink unavailable for tool {Tool}; continuing because MCP_AUDIT_FAIL_OPEN=true.",
                tool);
        }
        catch (Exception auditEx)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Audit sink unavailable ({auditEx.Message}). Refusing to complete '{tool}' " +
                        "so the call is not left unrecorded. Set MCP_AUDIT_FAIL_OPEN=true to override."
            });
        }

        return outcome.Payload;
    }
}
