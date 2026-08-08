using System.Text.Json;

namespace McpSqlServerTools.Redaction;

/// <summary>Parsed MCP_REDACTION_CONFIG. Table and column matching is case-insensitive.</summary>
public sealed class RedactionConfig
{
    public static readonly RedactionConfig Empty = new([]);

    public IReadOnlyList<RedactionRule> Rules { get; }
    public bool IsEmpty => Rules.Count == 0;

    private readonly Dictionary<string, RedactionRule> _byTableColumn = new();
    private readonly Dictionary<string, RedactionRule> _byColumnOnly = new();

    public RedactionConfig(IReadOnlyList<RedactionRule> rules)
    {
        Rules = rules;
        foreach (var rule in rules)
        {
            _byTableColumn[Key(rule.Table, rule.Column)] = rule;
            // Name-only index for SELECT * and for the SQLite provider, which has no parser to
            // resolve a table at all. If two tables share a column name, the last rule for that
            // name wins here — see NameOnlyColumnRedactor.
            _byColumnOnly[rule.Column.ToLowerInvariant()] = rule;
        }
    }

    /// <summary>Absent path means redaction is off — the caller (Program.cs) still has to log
    /// that fact, since "off" must never be silent.</summary>
    public static RedactionConfig LoadOrEmpty(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Empty : Load(path);

    public RedactionRule? TryGetRule(string table, string column) =>
        _byTableColumn.GetValueOrDefault(Key(table, column));

    public RedactionRule? TryGetRuleByColumnName(string column) =>
        _byColumnOnly.GetValueOrDefault(column.ToLowerInvariant());

    private static string Key(string table, string column) =>
        $"{table.ToLowerInvariant()}.{column.ToLowerInvariant()}";

    public static RedactionConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Redaction config at '{path}' has no content.");

        var rules = (file.Redactions ?? [])
            .Select(r => new RedactionRule(
                r.Table ?? throw new InvalidOperationException("A redaction entry is missing 'table'."),
                r.Column ?? throw new InvalidOperationException("A redaction entry is missing 'column'."),
                Enum.Parse<RedactionStrategy>(r.Strategy ?? "mask", ignoreCase: true),
                r.KeepLast))
            .ToList();

        return new RedactionConfig(rules);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ConfigFile(List<RuleFile>? Redactions);
    private sealed record RuleFile(string? Table, string? Column, string? Strategy, int? KeepLast);
}
