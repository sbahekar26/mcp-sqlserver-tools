namespace McpSqlServerTools.Db;

/// <summary>
/// Provider-specific catalogue queries. These are fixed, parameterised statements written
/// by us — they never pass through the read-only guard, because the guard exists to police
/// model-authored SQL, not our own.
/// </summary>
public sealed record Dialect(string ListTables, string DescribeColumns, string DescribeKeys)
{
    public static readonly Dialect SqlServer = new(
        ListTables: """
            SELECT s.name AS table_schema,
                   t.name AS table_name,
                   SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS row_estimate
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions p ON p.object_id = t.object_id
            GROUP BY s.name, t.name
            ORDER BY s.name, t.name
            """,
        DescribeColumns: """
            SELECT c.name      AS column_name,
                   ty.name     AS data_type,
                   c.max_length AS max_length,
                   c.is_nullable AS is_nullable,
                   c.column_id AS ordinal
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id
            """,
        DescribeKeys: """
            SELECT i.name AS constraint_name,
                   c.name AS column_name,
                   'PRIMARY KEY' AS kind,
                   CAST(NULL AS sysname) AS references_table
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@table)
            UNION ALL
            SELECT fk.name,
                   pc.name,
                   'FOREIGN KEY',
                   OBJECT_NAME(fk.referenced_object_id)
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id
                               AND pc.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@table)
            """);

    public static readonly Dialect Sqlite = new(
        ListTables: """
            SELECT 'main' AS table_schema,
                   name AS table_name,
                   -1 AS row_estimate
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """,
        DescribeColumns: """
            SELECT name AS column_name,
                   type AS data_type,
                   -1 AS max_length,
                   CASE "notnull" WHEN 0 THEN 1 ELSE 0 END AS is_nullable,
                   cid AS ordinal
            FROM pragma_table_info(@table)
            ORDER BY cid
            """,
        DescribeKeys: """
            SELECT 'pk' AS constraint_name,
                   name AS column_name,
                   'PRIMARY KEY' AS kind,
                   NULL AS references_table
            FROM pragma_table_info(@table)
            WHERE pk > 0
            UNION ALL
            SELECT 'fk', "from", 'FOREIGN KEY', "table"
            FROM pragma_foreign_key_list(@table)
            """);

    public static Dialect For(DbProvider provider) => provider switch
    {
        DbProvider.SqlServer => SqlServer,
        DbProvider.Sqlite => Sqlite,
        _ => throw new NotSupportedException($"Unknown provider {provider}.")
    };
}
