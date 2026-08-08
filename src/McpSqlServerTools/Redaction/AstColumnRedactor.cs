using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpSqlServerTools.Redaction;

/// <summary>
/// Resolves redacted output columns from the parsed statement rather than the result column
/// name alone, so an alias (SELECT Email AS e) still masks. Explicit SELECT list items are
/// traced to their table via the FROM-clause alias map; SELECT * (or t.*) has no named columns
/// in the AST at all, so it falls back to matching the eventual reader column name against
/// whichever tables are in scope for the star.
/// </summary>
public sealed class AstColumnRedactor(RedactionConfig config) : IColumnRedactor
{
    public Func<string, RedactionRule?> Plan(string sql)
    {
        if (config.IsEmpty) return static _ => null;

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        // The read-only guard already rejects anything that fails to parse, or isn't a single
        // SELECT, before ExecuteAsync ever runs it. If we land here some other way (e.g. a
        // direct SqlGateway call from a test) there is nothing to resolve — fail safe by masking
        // nothing extra, not by masking everything.
        if (errors.Count > 0 || fragment is not TSqlScript script)
            return static _ => null;

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1 || statements[0] is not SelectStatement select)
            return static _ => null;

        if (select.QueryExpression is not QuerySpecification spec)
            return config.TryGetRuleByColumnName; // set-operation query (UNION etc.) — name-only fallback

        var aliasToTable = TableAliasResolver.Resolve(spec.FromClause);
        var byOutputName = new Dictionary<string, RedactionRule>(StringComparer.OrdinalIgnoreCase);
        var wildcardTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in spec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                    var starQualifier = star.Qualifier?.Identifiers.LastOrDefault()?.Value;
                    var starTable = TableAliasResolver.ResolveTable(starQualifier, aliasToTable);
                    if (starTable is not null)
                        wildcardTables.Add(starTable);
                    else
                        foreach (var t in aliasToTable.Values.Distinct()) wildcardTables.Add(t);
                    break;

                case SelectScalarExpression { Expression: ColumnReferenceExpression colRef } scalar:
                    var identifiers = colRef.MultiPartIdentifier?.Identifiers;
                    if (identifiers is null || identifiers.Count == 0) break;

                    var column = identifiers[^1].Value;
                    var qualifier = identifiers.Count > 1 ? identifiers[^2].Value : null;
                    var table = TableAliasResolver.ResolveTable(qualifier, aliasToTable);
                    var outputName = scalar.ColumnName?.Value ?? column;

                    if (table is not null && config.TryGetRule(table, column) is { } rule)
                        byOutputName[outputName] = rule;
                    break;

                // Anything else — a function call, CASE expression, subquery, arithmetic — is not
                // a bare column reference, so there is no single source column to trace it back
                // to. Not masked. See README Known limits.
            }
        }

        return outputColumnName =>
        {
            if (byOutputName.TryGetValue(outputColumnName, out var rule)) return rule;
            if (wildcardTables.Count == 0) return null;

            var fallback = config.TryGetRuleByColumnName(outputColumnName);
            return fallback is not null && wildcardTables.Contains(fallback.Table) ? fallback : null;
        };
    }
}
