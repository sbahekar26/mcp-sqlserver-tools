using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpSqlServerTools.Redaction;

/// <summary>
/// Shared AST plumbing for both output masking (AstColumnRedactor) and predicate rejection
/// (ScriptDomReadOnlyGuard): maps FROM-clause aliases, and bare table names, to the real table
/// name, so a qualified column reference like c.Email or Customers.Email resolves the same way
/// regardless of which form the query used.
/// </summary>
internal static class TableAliasResolver
{
    public static Dictionary<string, string> Resolve(TSqlFragment? fromClause)
    {
        var visitor = new AliasVisitor();
        fromClause?.Accept(visitor);
        return visitor.AliasToTable;
    }

    /// <summary>
    /// Resolves a (possibly absent) qualifier to a table name. An unqualified column is
    /// attributed to the sole table in scope; with more than one table and no qualifier there is
    /// no way to know which table it came from without a real binder, so this returns null and
    /// the column is left unmasked / unchecked. See README Known limits.
    /// </summary>
    public static string? ResolveTable(string? qualifier, IReadOnlyDictionary<string, string> aliasToTable)
    {
        if (qualifier is not null)
            return aliasToTable.GetValueOrDefault(qualifier);

        var distinctTables = aliasToTable.Values.Distinct().ToList();
        return distinctTables.Count == 1 ? distinctTables[0] : null;
    }

    private sealed class AliasVisitor : TSqlFragmentVisitor
    {
        public Dictionary<string, string> AliasToTable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var table = node.SchemaObject?.BaseIdentifier?.Value;
            if (table is null) return;

            AliasToTable[table] = table;
            if (node.Alias?.Value is { } alias) AliasToTable[alias] = table;
        }
    }
}
