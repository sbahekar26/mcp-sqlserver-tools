namespace McpSqlServerTools.Redaction;

/// <summary>
/// SQLite fallback: no AST is available (see ConservativeReadOnlyGuard), so a column is matched
/// purely by the name the result reader reports. SELECT Email AS e is NOT masked by this —
/// by the time the gateway sees "e" there is no parser left to trace it back to Email. This is
/// the provider asymmetry called out in the README, not an oversight.
/// </summary>
public sealed class NameOnlyColumnRedactor(RedactionConfig config) : IColumnRedactor
{
    public Func<string, RedactionRule?> Plan(string sql) => config.TryGetRuleByColumnName;
}
