namespace McpSqlServerTools.Audit;

public interface IAuditSink
{
    Task WriteAsync(AuditRecord record);
}
