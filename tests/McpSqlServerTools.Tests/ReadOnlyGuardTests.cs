using McpSqlServerTools.Redaction;
using McpSqlServerTools.Safety;
using Xunit;

namespace McpSqlServerTools.Tests;

public class ConservativeGuardTests
{
    private readonly ConservativeReadOnlyGuard _guard = new();

    [Theory]
    [InlineData("SELECT * FROM Orders")]
    [InlineData("select id, name from Customers where region = 'ON'")]
    [InlineData("WITH t AS (SELECT 1 AS a) SELECT a FROM t")]
    [InlineData("SELECT * FROM Orders;")]
    public void Allows_single_select(string sql) =>
        Assert.True(_guard.Validate(sql).Allowed);

    [Theory]
    [InlineData("SELECT 'do not delete this row' AS note")]
    [InlineData("SELECT * FROM Orders -- drop table Orders")]
    [InlineData("SELECT * FROM Orders /* update Customers */")]
    [InlineData("SELECT * FROM [delete]")]
    public void Allows_keywords_that_are_only_literals_comments_or_identifiers(string sql) =>
        Assert.True(_guard.Validate(sql).Allowed);

    [Theory]
    [InlineData("DELETE FROM Orders")]
    [InlineData("update Orders set total = 0")]
    [InlineData("EXEC sp_who")]
    [InlineData("PRAGMA table_info(Orders)")]
    [InlineData("SELECT 1; DROP TABLE Orders")]
    [InlineData("SELECT 1; -- x\nUPDATE Orders SET a = 1")]
    [InlineData("")]
    public void Rejects_writes_and_batches(string sql) =>
        Assert.False(_guard.Validate(sql).Allowed);
}

public class ScriptDomGuardTests
{
    private readonly ScriptDomReadOnlyGuard _guard = new(RedactionConfig.Empty);

    [Theory]
    [InlineData("SELECT TOP 10 * FROM dbo.Orders")]
    [InlineData("WITH cte AS (SELECT 1 AS a) SELECT a FROM cte")]
    [InlineData("SELECT o.Id FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId")]
    public void Allows_select(string sql) =>
        Assert.True(_guard.Validate(sql).Allowed);

    [Theory]
    [InlineData("SELECT * INTO #tmp FROM dbo.Orders")]
    [InlineData("UPDATE dbo.Orders SET Total = 0")]
    [InlineData("EXEC sp_executesql N'DELETE FROM dbo.Orders'")]
    [InlineData("SELECT 1; DROP TABLE dbo.Orders")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    // A CTE is only ever a named subquery; the write lives in the statement that follows it, and
    // that statement is an InsertStatement rather than a SelectStatement. The existing
    // "exactly one SELECT" check rejects it without needing to see inside the CTE at all.
    [InlineData("WITH cte AS (SELECT 1 AS a) INSERT INTO dbo.Target (a) SELECT a FROM cte")]
    public void Rejects_anything_that_can_write(string sql) =>
        Assert.False(_guard.Validate(sql).Allowed);

    [Fact]
    public void Reports_the_reason_for_a_rejection()
    {
        var result = _guard.Validate("SELECT * INTO #tmp FROM dbo.Orders");
        Assert.False(result.Allowed);
        Assert.Contains("INTO", result.Reason);
    }

    [Theory]
    // FOR XML/JSON only reshapes the result set into text; it has no side effects, no INTO
    // target and cannot invoke EXEC, so it belongs on the allow side like any other SELECT.
    [InlineData("SELECT Id, Name FROM dbo.Customers FOR XML AUTO")]
    // A table-valued function called from a FROM clause is a read: SQL Server does not allow a
    // TVF body to perform DML against anything but its own local table variables, so calling one
    // cannot smuggle a write in through the back door. It's just another table reference.
    [InlineData("SELECT * FROM dbo.GetOrdersByYear(2024) AS t")]
    public void Allows_read_only_constructs_with_no_write_path(string sql) =>
        Assert.True(_guard.Validate(sql).Allowed);
}
