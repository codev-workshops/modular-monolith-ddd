using Microsoft.Data.SqlClient;

namespace CompanyName.MyMeetings.Parity.Db;

public sealed record DbColumn(string Name, string SqlType, int Ordinal, bool IsGuid);

public sealed record DbObject(
    string Schema,
    string Name,
    string Kind, // "table" | "view"
    IReadOnlyList<DbColumn> Columns,
    IReadOnlyList<string> PrimaryKey)
{
    public string Key => $"{Schema}.{Name}".ToLowerInvariant();

    public string QualifiedName => $"[{Schema}].[{Name}]";
}

/// <summary>Dynamically discovers base tables and views (with columns and PKs) from the live catalog.</summary>
public static class DbSchema
{
    private static readonly string[] IncludedSchemas =
    {
        "meetings", "payments", "administration", "users", "registrations", "app",
    };

    public static List<DbObject> Discover(SqlConnection connection)
    {
        var schemaList = string.Join(",", IncludedSchemas.Select(s => $"'{s}'"));

        var objects = new List<(string Schema, string Name, string Kind)>();
        var listSql = $@"
SELECT s.name AS SchemaName, t.name AS ObjectName, 'table' AS Kind
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name IN ({schemaList})
UNION ALL
SELECT s.name AS SchemaName, v.name AS ObjectName, 'view' AS Kind
FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
WHERE s.name IN ({schemaList})
ORDER BY SchemaName, ObjectName;";

        using (var cmd = new SqlCommand(listSql, connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var result = new List<DbObject>();
        foreach (var (schema, name, kind) in objects)
        {
            var columns = GetColumns(connection, schema, name);
            var pk = GetPrimaryKey(connection, schema, name);
            result.Add(new DbObject(schema, name, kind, columns, pk));
        }

        return result;
    }

    private static List<DbColumn> GetColumns(SqlConnection connection, string schema, string name)
    {
        const string sql = @"
SELECT c.COLUMN_NAME, c.DATA_TYPE, c.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @name
ORDER BY c.ORDINAL_POSITION;";

        var columns = new List<DbColumn>();
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var colName = reader.GetString(0);
            var type = reader.GetString(1);
            var ordinal = reader.GetInt32(2);
            columns.Add(new DbColumn(colName, type, ordinal, type.Equals("uniqueidentifier", StringComparison.OrdinalIgnoreCase)));
        }

        return columns;
    }

    private static List<string> GetPrimaryKey(SqlConnection connection, string schema, string name)
    {
        const string sql = @"
SELECT kcu.COLUMN_NAME, kcu.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
    ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
    AND tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @name
ORDER BY kcu.ORDINAL_POSITION;";

        var pk = new List<string>();
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            pk.Add(reader.GetString(0));
        }

        return pk;
    }
}
