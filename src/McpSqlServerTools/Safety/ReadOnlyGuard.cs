using System.Text;
using McpSqlServerTools.Redaction;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpSqlServerTools.Safety;

public sealed record GuardResult(bool Allowed, string? Reason, string? SanitizedStatement = null)
{
    public static readonly GuardResult Ok = new(true, null);
    public static GuardResult Deny(string reason) => new(false, reason);

    // SanitizedStatement carries a copy of the SQL with the offending clause blanked out, for
    // callers (the audit log) that must not record the literal being compared against a
    // redacted column — that literal is itself the sensitive guess.
    public static GuardResult DenyRedactedPredicate(string reason, string sanitizedStatement) =>
        new(false, reason, sanitizedStatement);
}

public interface IReadOnlyGuard
{
    GuardResult Validate(string sql);
}

/// <summary>
/// Parses T-SQL into an AST and allows a statement only if every batch contains
/// exactly one SELECT with no INTO clause and no nested EXEC. This is an allow-list:
/// anything the parser does not recognise as a plain SELECT is rejected, so new or
/// obscure statement types fail closed rather than slipping through a keyword filter.
/// </summary>
public sealed class ScriptDomReadOnlyGuard(RedactionConfig redaction) : IReadOnlyGuard
{
    public GuardResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return GuardResult.Deny("Empty statement.");

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
            return GuardResult.Deny($"Parse error at line {errors[0].Line}: {errors[0].Message}");

        if (fragment is not TSqlScript script)
            return GuardResult.Deny("Statement did not parse as a T-SQL script.");

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();

        if (statements.Count == 0)
            return GuardResult.Deny("No statement found.");

        if (statements.Count > 1)
            return GuardResult.Deny(
                $"Only one statement is permitted; {statements.Count} were supplied.");

        if (statements[0] is not SelectStatement select)
            return GuardResult.Deny(
                $"Only SELECT is permitted; found {statements[0].GetType().Name}.");

        var visitor = new MutationVisitor();
        select.Accept(visitor);

        if (visitor.Violation is { } violation)
            return GuardResult.Deny(violation);

        if (!redaction.IsEmpty && select.QueryExpression is QuerySpecification spec)
        {
            var predicateViolation = CheckRedactedPredicates(sql, spec);
            if (predicateViolation is not null) return predicateViolation;
        }

        return GuardResult.Ok;
    }

    /// <summary>
    /// Rejects a query that uses a redacted column anywhere it would let the model infer the
    /// value instead of just seeing it: WHERE, JOIN ON, GROUP BY, HAVING, ORDER BY. Selecting
    /// the column outright is still allowed — SqlGateway masks it on the way out.
    /// </summary>
    private GuardResult? CheckRedactedPredicates(string sql, QuerySpecification spec)
    {
        var aliasToTable = TableAliasResolver.Resolve(spec.FromClause);

        GuardResult? CheckClause(TSqlFragment? clause)
        {
            if (clause is null) return null;

            var collector = new ColumnReferenceCollector();
            clause.Accept(collector);

            foreach (var colRef in collector.Columns)
            {
                var identifiers = colRef.MultiPartIdentifier?.Identifiers;
                if (identifiers is null || identifiers.Count == 0) continue; // e.g. COUNT(*)

                var column = identifiers[^1].Value;
                var qualifier = identifiers.Count > 1 ? identifiers[^2].Value : null;
                var table = TableAliasResolver.ResolveTable(qualifier, aliasToTable);

                if (table is null || redaction.TryGetRule(table, column) is null)
                    continue;

                // Blank out the whole clause, not just the literal being compared: pinpointing
                // exactly the literal AST node for every predicate shape (comparison, IN-list,
                // LIKE, BETWEEN, IS NULL...) is a lot of casework for a query that is being
                // rejected anyway, and the query never runs, so hiding a few extra
                // non-sensitive characters alongside it costs nothing.
                var sanitized = sql[..clause.StartOffset] + "<redacted>" +
                                sql[(clause.StartOffset + clause.FragmentLength)..];

                return GuardResult.DenyRedactedPredicate(
                    $"Column '{column}' is redacted and cannot be used in a WHERE, JOIN ON, GROUP BY, " +
                    "HAVING or ORDER BY clause — it would let a filter or a match confirm the value one " +
                    "guess at a time. Select it directly instead; it will come back masked.",
                    sanitized);
            }

            return null;
        }

        var result = CheckClause(spec.WhereClause)
            ?? CheckClause(spec.GroupByClause)
            ?? CheckClause(spec.HavingClause)
            ?? CheckClause(spec.OrderByClause);
        if (result is not null) return result;

        var joinCollector = new QualifiedJoinCollector();
        spec.FromClause?.Accept(joinCollector);
        foreach (var join in joinCollector.Joins)
        {
            result = CheckClause(join.SearchCondition);
            if (result is not null) return result;
        }

        return null;
    }

    private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> Columns { get; } = [];
        public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);
    }

    private sealed class QualifiedJoinCollector : TSqlFragmentVisitor
    {
        public List<QualifiedJoin> Joins { get; } = [];
        public override void Visit(QualifiedJoin node) => Joins.Add(node);
    }

    private sealed class MutationVisitor : TSqlFragmentVisitor
    {
        public string? Violation { get; private set; }

        // SELECT ... INTO #t creates a table, so it is a write despite being a SelectStatement.
        // `Into` lives on SelectStatement itself (not QuerySpecification) in this ScriptDom version.
        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
                Violation ??= "SELECT ... INTO is not permitted.";
        }

        // EXEC / EXECUTE can run arbitrary DML from inside an otherwise read-only shape.
        public override void Visit(ExecuteStatement node)
            => Violation ??= "EXEC is not permitted.";

        public override void Visit(ExecutableProcedureReference node)
            => Violation ??= "Procedure execution is not permitted.";

        // OPENROWSET / OPENQUERY reach outside the connection's permission boundary.
        public override void Visit(OpenRowsetTableReference node)
            => Violation ??= "OPENROWSET is not permitted.";

        public override void Visit(OpenQueryTableReference node)
            => Violation ??= "OPENQUERY is not permitted.";
    }
}

/// <summary>
/// Fallback for providers with no T-SQL parser (SQLite demo mode). Strips comments and
/// string literals first so that a keyword hidden inside either cannot smuggle a write
/// past the check, then requires the remaining text to be a single SELECT or WITH.
/// </summary>
public sealed class ConservativeReadOnlyGuard : IReadOnlyGuard
{
    private static readonly string[] Forbidden =
    [
        "insert", "update", "delete", "drop", "alter", "create", "truncate", "merge",
        "grant", "revoke", "attach", "detach", "pragma", "vacuum", "replace", "reindex",
        "exec", "execute"
    ];

    public GuardResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return GuardResult.Deny("Empty statement.");

        var stripped = StripLiteralsAndComments(sql).Trim().TrimEnd(';');

        if (stripped.Contains(';'))
            return GuardResult.Deny("Only one statement is permitted.");

        var tokens = stripped
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('(', ')', ',').ToLowerInvariant())
            .ToArray();

        if (tokens.Length == 0)
            return GuardResult.Deny("No statement found.");

        if (tokens[0] is not ("select" or "with"))
            return GuardResult.Deny($"Only SELECT is permitted; statement begins with '{tokens[0]}'.");

        foreach (var token in tokens)
        {
            if (Forbidden.Contains(token))
                return GuardResult.Deny($"Keyword '{token}' is not permitted.");
        }

        return GuardResult.Ok;
    }

    /// <summary>
    /// Replaces the contents of quoted literals and comments with spaces, preserving
    /// statement separators so that ';' inside a literal is not mistaken for one.
    /// </summary>
    internal static string StripLiteralsAndComments(string sql)
    {
        var output = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                output.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, sql.Length);
                output.Append(' ');
            }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    // Doubled delimiter is an escaped delimiter, not a terminator.
                    if (sql[i] == close && i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                    if (sql[i] == close) { i++; break; }
                    i++;
                }
                output.Append(" x ");
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }
}
