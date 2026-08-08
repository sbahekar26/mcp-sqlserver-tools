using McpSqlServerTools.Audit;
using McpSqlServerTools.Db;
using McpSqlServerTools.Redaction;
using McpSqlServerTools.Safety;
using McpSqlServerTools.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpSqlServerTools.Tests;

file sealed class CapturingAuditSink : IAuditSink
{
    public AuditRecord? Last { get; private set; }
    public Task WriteAsync(AuditRecord record) { Last = record; return Task.CompletedTask; }
}

/// <summary>
/// A real SQLite database, but masked using AstColumnRedactor (the SQL-Server / ScriptDom
/// path) rather than the NameOnlyColumnRedactor a Sqlite provider would actually get in
/// production. This is deliberate: AstColumnRedactor.Plan only ever parses the SQL *text* via
/// ScriptDom — it never touches the connection — and a plain "SELECT col FROM table" is valid
/// syntax in both T-SQL and SQLite, so this lets output-masking (which needs a real result set)
/// be tested end to end without a SQL Server instance. Predicate rejection is tested separately,
/// directly against the guard, which needs no database at all.
/// </summary>
public sealed class RedactionSqliteFixture : IDisposable
{
    public const string Email = "ada@example.com";
    public const string Phone = "555-0100";

    private readonly string _dbPath;

    public ServerOptions Options { get; }
    public SqlGateway Gateway { get; }
    public RedactionConfig Config { get; } = new([
        new RedactionRule("Customers", "Email", RedactionStrategy.Mask, null),
        new RedactionRule("Customers", "Phone", RedactionStrategy.Hash, null)
    ]);

    public RedactionSqliteFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"redaction-test-{Guid.NewGuid():N}.db");
        Options = new ServerOptions { Provider = DbProvider.Sqlite, ConnectionString = $"Data Source={_dbPath}" };
        Gateway = new SqlGateway(Options, new AstColumnRedactor(Config));

        using var connection = new SqliteConnection(Options.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT, Email TEXT, Phone TEXT); " +
                               $"INSERT INTO Customers (Id, Name, Email, Phone) VALUES (1, 'Ada Lovelace', '{Email}', '{Phone}');";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

public class RedactionOutputMaskingTests
{
    [Fact]
    public async Task Selecting_a_redacted_column_returns_the_masked_form()
    {
        using var fixture = new RedactionSqliteFixture();
        var result = await fixture.Gateway.ExecuteAsync("SELECT Email FROM Customers", null, null, default);

        Assert.Equal("[REDACTED]", Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task Select_star_masks_the_redacted_column_and_leaves_others_alone()
    {
        using var fixture = new RedactionSqliteFixture();
        var result = await fixture.Gateway.ExecuteAsync("SELECT * FROM Customers", null, null, default);

        var row = Assert.Single(result.Rows);
        var columns = result.Columns.ToList();
        Assert.Equal("[REDACTED]", row[columns.IndexOf("Email")]);
        Assert.Equal("Ada Lovelace", row[columns.IndexOf("Name")]); // non-redacted column: unaffected
    }

    [Fact]
    public async Task Aliased_redacted_column_is_still_masked()
    {
        using var fixture = new RedactionSqliteFixture();
        var result = await fixture.Gateway.ExecuteAsync("SELECT Email AS e FROM Customers", null, null, default);

        Assert.Equal(["e"], result.Columns);
        Assert.Equal("[REDACTED]", Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task Non_redacted_column_in_the_same_table_is_unaffected()
    {
        using var fixture = new RedactionSqliteFixture();
        var result = await fixture.Gateway.ExecuteAsync("SELECT Name FROM Customers", null, null, default);

        Assert.Equal("Ada Lovelace", Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public void Hash_strategy_is_stable_across_calls_for_the_same_input()
    {
        var rule = new RedactionRule("Customers", "Phone", RedactionStrategy.Hash, null);

        var first = rule.Apply(RedactionSqliteFixture.Phone);
        var second = rule.Apply(RedactionSqliteFixture.Phone);

        Assert.Equal(first, second);
        Assert.NotEqual(RedactionSqliteFixture.Phone, first);
    }
}

public class RedactionPredicateGuardTests
{
    // No database involved: the guard rejects on the parsed SQL text alone.
    private static readonly RedactionConfig Config = new([
        new RedactionRule("Customers", "Email", RedactionStrategy.Mask, null)
    ]);

    private readonly ScriptDomReadOnlyGuard _guard = new(Config);

    [Fact]
    public void Where_on_a_redacted_column_is_rejected_naming_the_column()
    {
        var verdict = _guard.Validate("SELECT Name FROM Customers WHERE Email = 'ada@example.com'");

        Assert.False(verdict.Allowed);
        Assert.Contains("Email", verdict.Reason);
    }

    [Fact]
    public void Join_on_on_a_redacted_column_is_rejected()
    {
        var verdict = _guard.Validate(
            "SELECT c.Name FROM Customers c JOIN Orders o ON o.Email = c.Email");

        Assert.False(verdict.Allowed);
        Assert.Contains("Email", verdict.Reason);
    }

    [Fact]
    public void Group_by_on_a_redacted_column_is_rejected()
    {
        // The SELECT list itself may reference the column freely — only the GROUP BY makes this
        // one illegal.
        var verdict = _guard.Validate("SELECT Email, COUNT(*) FROM Customers GROUP BY Email");

        Assert.False(verdict.Allowed);
        Assert.Contains("Email", verdict.Reason);
    }

    [Fact]
    public void Having_on_a_redacted_column_is_rejected()
    {
        var verdict = _guard.Validate(
            "SELECT Name, COUNT(*) FROM Customers GROUP BY Name HAVING Email = 'ada@example.com'");

        Assert.False(verdict.Allowed);
        Assert.Contains("Email", verdict.Reason);
    }

    [Fact]
    public void Order_by_on_a_redacted_column_is_rejected()
    {
        var verdict = _guard.Validate("SELECT Name FROM Customers ORDER BY Email");

        Assert.False(verdict.Allowed);
        Assert.Contains("Email", verdict.Reason);
    }

    [Fact]
    public void Selecting_the_redacted_column_outright_is_still_allowed()
    {
        var verdict = _guard.Validate("SELECT Email FROM Customers");

        Assert.True(verdict.Allowed);
    }
}

public class RedactionAuditTests
{
    [Fact]
    public async Task Rejected_predicate_query_is_audited_without_the_literal()
    {
        var config = new RedactionConfig([new RedactionRule("Customers", "Email", RedactionStrategy.Mask, null)]);
        // Never actually opened: the guard rejects before SqlGateway.ExecuteAsync runs.
        var options = new ServerOptions { Provider = DbProvider.SqlServer, ConnectionString = "unused" };
        var sink = new CapturingAuditSink();
        var tools = new QueryTools(
            new SqlGateway(options, new AstColumnRedactor(config)), new ScriptDomReadOnlyGuard(config), sink,
            options, NullLogger<QueryTools>.Instance);

        await tools.QueryAsync("SELECT Name FROM Customers WHERE Email = 'ada@example.com'");

        Assert.NotNull(sink.Last);
        Assert.Equal("rejected", sink.Last!.Outcome);
        Assert.Contains("Email", sink.Last.RejectionReason);
        Assert.DoesNotContain("ada@example.com", sink.Last.Statement);
    }
}

public class RedactionConfigTests
{
    [Fact]
    public void Missing_config_path_means_redaction_is_off()
    {
        Assert.True(RedactionConfig.LoadOrEmpty(null).IsEmpty);
    }

    [Fact]
    public void Loads_all_three_strategies_from_the_documented_json_shape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redaction-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "redactions": [
                { "table": "Customers", "column": "Email", "strategy": "mask" },
                { "table": "Customers", "column": "Phone", "strategy": "hash" },
                { "table": "Vehicles",  "column": "Vin",   "strategy": "partial", "keepLast": 4 }
              ]
            }
            """);
        try
        {
            var config = RedactionConfig.LoadOrEmpty(path);

            Assert.False(config.IsEmpty);
            Assert.Equal(3, config.Rules.Count);
            Assert.Equal(RedactionStrategy.Mask, config.TryGetRule("customers", "email")!.Strategy);
            Assert.Equal(RedactionStrategy.Hash, config.TryGetRule("Customers", "Phone")!.Strategy);
            Assert.Equal(4, config.TryGetRule("Vehicles", "Vin")!.KeepLast);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
