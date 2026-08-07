using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpSqlServerTools.Safety;

public sealed record GuardResult(bool Allowed, string? Reason)
{
    public static readonly GuardResult Ok = new(true, null);
    public static GuardResult Deny(string reason) => new(false, reason);
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
public sealed class ScriptDomReadOnlyGuard : IReadOnlyGuard
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

        return visitor.Violation is { } violation
            ? GuardResult.Deny(violation)
            : GuardResult.Ok;
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
