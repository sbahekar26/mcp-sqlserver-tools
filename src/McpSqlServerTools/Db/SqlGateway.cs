using System.Data.Common;
using System.Text.Json;
using McpSqlServerTools.Redaction;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace McpSqlServerTools.Db;

public sealed record ResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool RowsTruncated,
    bool BytesTruncated,
    int ElapsedMs);

/// <summary>
/// Single point of database access. Every path through here enforces the row cap, the
/// payload-size cap and the command timeout, so no tool can bypass them by accident.
/// </summary>
public sealed class SqlGateway(ServerOptions options, IColumnRedactor redactor)
{
    private readonly Dialect _dialect = Dialect.For(options.Provider);

    public Dialect Dialect => _dialect;
    public ServerOptions Options => options;

    private DbConnection CreateConnection() => options.Provider switch
    {
        DbProvider.SqlServer => new SqlConnection(options.ConnectionString),
        DbProvider.Sqlite => new SqliteConnection(options.ConnectionString),
        _ => throw new NotSupportedException($"Unknown provider {options.Provider}.")
    };

    public async Task<ResultSet> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        int? rowLimit,
        CancellationToken cancellationToken,
        bool applyRedaction = true)
    {
        var effectiveLimit = Math.Clamp(rowLimit ?? options.MaxRows, 1, options.MaxRows);
        var startedAt = DateTimeOffset.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // CA2100 flags dynamic CommandText as potential SQL injection. That's the correct default,
        // but this method IS the single gateway every caller (guarded model SQL and our own fixed
        // catalogue queries) must go through; the guard, not string-literal-only text, is the
        // control here. See IReadOnlyGuard / ScriptDomReadOnlyGuard for the actual enforcement.
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        command.CommandTimeout = options.CommandTimeoutSeconds;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        // Resolved once per query, not per row: the AST (or the SQLite name-only fallback)
        // tells us which output columns are redacted before any row is read. Catalogue queries
        // (list_tables, describe_table) pass applyRedaction: false — they return schema
        // metadata we wrote ourselves, not model-facing table data, so there is nothing to mask.
        var columnRules = applyRedaction ? columns.Select(redactor.Plan(sql)).ToArray() : null;

        var rows = new List<IReadOnlyList<object?>>();
        var approximateBytes = 0;
        var rowsTruncated = false;
        var bytesTruncated = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= effectiveLimit)
            {
                rowsTruncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : Normalise(reader.GetValue(i));
                row[i] = columnRules?[i] is { } rule ? rule.Apply(value) : value;
            }

            approximateBytes += EstimateBytes(row);
            if (approximateBytes > options.MaxResponseBytes)
            {
                bytesTruncated = true;
                break;
            }

            rows.Add(row);
        }

        return new ResultSet(
            columns,
            rows,
            rowsTruncated,
            bytesTruncated,
            (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
    }

    /// <summary>
    /// Converts provider types that JSON cannot represent faithfully into strings, so the
    /// model sees a stable shape rather than provider-dependent serialisation.
    /// </summary>
    private static object? Normalise(object value) => value switch
    {
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        DateTime dt => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        TimeSpan ts => ts.ToString(),
        Guid guid => guid.ToString(),
        decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };

    private static int EstimateBytes(IReadOnlyList<object?> row)
    {
        var total = 0;
        foreach (var value in row)
        {
            total += value switch
            {
                null => 4,
                string s => s.Length + 3,
                _ => 12
            };
        }
        return total;
    }

    public static string ToJson(ResultSet result)
    {
        var payload = new
        {
            columns = result.Columns,
            rowCount = result.Rows.Count,
            rows = result.Rows,
            truncated = result.RowsTruncated || result.BytesTruncated,
            truncationReason = (result.RowsTruncated, result.BytesTruncated) switch
            {
                (true, _) => "Row limit reached. Narrow the query or raise maxRows.",
                (_, true) => "Response size limit reached. Select fewer columns.",
                _ => null
            },
            elapsedMs = result.ElapsedMs
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
