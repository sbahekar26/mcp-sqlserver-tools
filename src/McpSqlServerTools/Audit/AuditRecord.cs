namespace McpSqlServerTools.Audit;

/// <summary>
/// One line of the audit trail. Deliberately has no field capable of holding a row or a
/// field value — the record describes the request, not the data the request returned.
/// </summary>
public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    string Tool,
    string? Statement,
    string Outcome,
    string? RejectionReason,
    string? ErrorMessage,
    int? RowCount,
    bool Truncated,
    int ElapsedMs,
    string Provider);
