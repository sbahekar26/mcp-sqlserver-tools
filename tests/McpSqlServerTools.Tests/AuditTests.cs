using System.Text.Json;
using McpSqlServerTools.Audit;
using McpSqlServerTools.Db;
using McpSqlServerTools.Safety;
using McpSqlServerTools.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpSqlServerTools.Tests;

/// <summary>An in-memory database with one seeded row, for tests that need a real result set.</summary>
public sealed class SqliteAuditFixture : IDisposable
{
    // Lives only in a result row, never in any SQL text a test submits, so it can stand in
    // for "a value that came back from the database" as distinct from "a value in the query".
    public const string CanaryValue = "canary-distinctive-value-9f3a";

    private readonly string _dbPath;

    public ServerOptions Options { get; }
    public SqlGateway Gateway { get; }

    public SqliteAuditFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}.db");
        Options = new ServerOptions { Provider = DbProvider.Sqlite, ConnectionString = $"Data Source={_dbPath}" };
        Gateway = new SqlGateway(Options);

        using var connection = new SqliteConnection(Options.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Notes (Id INTEGER PRIMARY KEY, Text TEXT); " +
                               $"INSERT INTO Notes (Text) VALUES ('{CanaryValue}');";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

file sealed class CapturingAuditSink : IAuditSink
{
    public AuditRecord? Last { get; private set; }
    public Task WriteAsync(AuditRecord record) { Last = record; return Task.CompletedTask; }
}

file sealed class ThrowingAuditSink : IAuditSink
{
    public Task WriteAsync(AuditRecord record) => throw new IOException("disk full");
}

file sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

public class AuditTests
{
    // No fixture needed: the guard rejects before the gateway ever opens a connection.
    private static readonly ServerOptions InertOptions =
        new() { Provider = DbProvider.Sqlite, ConnectionString = "Data Source=:memory:" };

    [Fact]
    public async Task Rejected_query_is_audited_with_reason()
    {
        var sink = new CapturingAuditSink();
        var tools = new QueryTools(
            new SqlGateway(InertOptions), new ConservativeReadOnlyGuard(), sink, InertOptions,
            NullLogger<QueryTools>.Instance);

        await tools.QueryAsync("DELETE FROM Notes");

        Assert.NotNull(sink.Last);
        Assert.Equal("rejected", sink.Last!.Outcome);
        Assert.False(string.IsNullOrEmpty(sink.Last.RejectionReason));
        Assert.Null(sink.Last.RowCount);
    }

    [Fact]
    public async Task Successful_query_is_audited_with_row_count_and_elapsed()
    {
        using var fixture = new SqliteAuditFixture();
        var sink = new CapturingAuditSink();
        var tools = new QueryTools(
            fixture.Gateway, new ConservativeReadOnlyGuard(), sink, fixture.Options,
            NullLogger<QueryTools>.Instance);

        await tools.QueryAsync("SELECT Id FROM Notes");

        Assert.NotNull(sink.Last);
        Assert.Equal("allowed", sink.Last!.Outcome);
        Assert.Equal(1, sink.Last.RowCount);
        Assert.True(sink.Last.ElapsedMs >= 0);
    }

    [Fact]
    public async Task No_result_values_appear_in_the_emitted_record()
    {
        using var fixture = new SqliteAuditFixture();
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = JsonlAuditSink.ForPath(path);
            var tools = new QueryTools(
                fixture.Gateway, new ConservativeReadOnlyGuard(), sink, fixture.Options,
                NullLogger<QueryTools>.Instance);

            var response = await tools.QueryAsync("SELECT Text FROM Notes");
            Assert.Contains(SqliteAuditFixture.CanaryValue, response); // sanity: the row value came back

            sink.Dispose();
            var line = Assert.Single(await File.ReadAllLinesAsync(path));
            Assert.DoesNotContain(SqliteAuditFixture.CanaryValue, line);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Failing_sink_fails_the_call_when_fail_open_is_off()
    {
        var options = new ServerOptions
        {
            Provider = DbProvider.Sqlite, ConnectionString = "Data Source=:memory:", AuditFailOpen = false
        };
        var tools = new QueryTools(
            new SqlGateway(options), new ConservativeReadOnlyGuard(), new ThrowingAuditSink(), options,
            NullLogger<QueryTools>.Instance);

        var response = await tools.QueryAsync("DELETE FROM Notes");

        using var doc = JsonDocument.Parse(response);
        Assert.Contains("Audit sink unavailable", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Failing_sink_succeeds_with_warning_when_fail_open_is_on()
    {
        using var fixture = new SqliteAuditFixture();
        var options = new ServerOptions
        {
            Provider = fixture.Options.Provider, ConnectionString = fixture.Options.ConnectionString,
            AuditFailOpen = true
        };
        var logger = new RecordingLogger<QueryTools>();
        var tools = new QueryTools(
            new SqlGateway(options), new ConservativeReadOnlyGuard(), new ThrowingAuditSink(), options, logger);

        var response = await tools.QueryAsync("SELECT Id FROM Notes");

        using var doc = JsonDocument.Parse(response);
        Assert.Equal(1, doc.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Contains(logger.Warnings, w => w.Contains("Audit sink unavailable"));
    }

    [Fact]
    public async Task Concurrent_writes_do_not_interleave_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-concurrent-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = JsonlAuditSink.ForPath(path);

            var writes = Enumerable.Range(0, 50).Select(i => sink.WriteAsync(new AuditRecord(
                DateTimeOffset.UtcNow, "session", "query", $"SELECT {i}", "allowed",
                null, null, i, false, 1, "Sqlite")));
            await Task.WhenAll(writes);
            sink.Dispose();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(50, lines.Length);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line); // throws on a corrupted/interleaved line
                Assert.Equal("query", doc.RootElement.GetProperty("tool").GetString());
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
