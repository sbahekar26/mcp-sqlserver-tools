namespace McpSqlServerTools.Redaction;

/// <summary>
/// Resolves, once per query, which output columns should be masked. SQL Server can trace
/// aliases and SELECT * back to their source column via the AST (AstColumnRedactor); SQLite has
/// no parser here and falls back to matching the result column name alone
/// (NameOnlyColumnRedactor) — an aliased redacted column will not be caught on that provider.
/// </summary>
public interface IColumnRedactor
{
    Func<string, RedactionRule?> Plan(string sql);
}
