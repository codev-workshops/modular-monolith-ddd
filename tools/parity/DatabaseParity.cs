using System.Data;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// Hashes the structure of each module's database schema: tables (columns, keys,
/// foreign keys, indexes, check constraints), and views (columns + normalized
/// definition). One hash per schema so a track's DB parity can be verified in
/// isolation. Structure only — never row data — so the hash is independent of
/// seed volume.
/// </summary>
internal static class DatabaseParity
{
    public static List<ParityEntry> Capture(string connectionString)
    {
        var schemas = Modules.All.Select(m => m.DbSchema).Distinct().ToList();

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var columns = LoadColumns(connection, schemas);
        var keys = LoadKeyConstraints(connection, schemas);
        var foreignKeys = LoadForeignKeys(connection, schemas);
        var indexes = LoadIndexes(connection, schemas);
        var checks = LoadCheckConstraints(connection, schemas);
        var views = LoadViewDefinitions(connection, schemas);

        var entries = new List<ParityEntry>();
        foreach (var module in Modules.All)
        {
            var schema = module.DbSchema;

            var tableNames = columns
                .Where(c => c.Schema == schema && c.IsBaseTable)
                .Select(c => c.Table)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var tables = tableNames.Select(table => new
            {
                name = table,
                columns = ColumnModel(columns, schema, table),
                primaryKey = keys
                    .Where(k => k.Schema == schema && k.Table == table && k.Type == "PRIMARY KEY")
                    .OrderBy(k => k.Ordinal)
                    .Select(k => k.Column)
                    .ToList(),
                uniqueConstraints = keys
                    .Where(k => k.Schema == schema && k.Table == table && k.Type == "UNIQUE")
                    .GroupBy(k => k.Constraint)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        name = g.Key,
                        columns = g.OrderBy(x => x.Ordinal).Select(x => x.Column).ToList(),
                    })
                    .ToList<object>(),
                foreignKeys = foreignKeys
                    .Where(f => f.Schema == schema && f.Table == table)
                    .GroupBy(f => f.Name)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        name = g.Key,
                        columns = g.OrderBy(x => x.Ordinal).Select(x => x.Column).ToList(),
                        references = $"{g.First().RefSchema}.{g.First().RefTable}",
                        referencedColumns = g.OrderBy(x => x.Ordinal).Select(x => x.RefColumn).ToList(),
                        onDelete = g.First().OnDelete,
                        onUpdate = g.First().OnUpdate,
                    })
                    .ToList<object>(),
                checkConstraints = checks
                    .Where(c => c.Schema == schema && c.Table == table)
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => new { name = c.Name, definition = NormalizeSql(c.Definition) })
                    .ToList<object>(),
                indexes = indexes
                    .Where(i => i.Schema == schema && i.Table == table)
                    .GroupBy(i => i.Index)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        name = g.Key,
                        isUnique = g.First().IsUnique,
                        isPrimaryKey = g.First().IsPrimaryKey,
                        type = g.First().TypeDesc,
                        columns = g.Where(x => !x.IsIncluded).OrderBy(x => x.KeyOrdinal)
                            .Select(x => x.Column).ToList(),
                        includedColumns = g.Where(x => x.IsIncluded)
                            .OrderBy(x => x.Column, StringComparer.Ordinal)
                            .Select(x => x.Column).ToList(),
                    })
                    .ToList<object>(),
            }).ToList<object>();

            var viewNames = columns
                .Where(c => c.Schema == schema && !c.IsBaseTable)
                .Select(c => c.Table)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var schemaViews = viewNames.Select(view => new
            {
                name = view,
                columns = ColumnModel(columns, schema, view),
                definition = NormalizeSql(views.GetValueOrDefault((schema, view), string.Empty)),
            }).ToList<object>();

            var model = new
            {
                schema,
                tables,
                views = schemaViews,
            };

            entries.Add(new ParityEntry
            {
                Key = schema,
                Module = module.Name,
                Hash = Canonical.HashObject(model),
                Details = model,
            });
        }

        return entries;
    }

    private static List<object> ColumnModel(List<ColumnRow> columns, string schema, string table) =>
        columns
            .Where(c => c.Schema == schema && c.Table == table)
            .OrderBy(c => c.Ordinal)
            .Select(c => (object)new
            {
                name = c.Name,
                ordinal = c.Ordinal,
                type = c.DataType,
                maxLength = c.MaxLength,
                precision = c.Precision,
                scale = c.Scale,
                nullable = c.IsNullable,
                @default = c.Default,
                identity = c.IsIdentity,
            })
            .ToList();

    private static string NormalizeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        // Collapse all whitespace runs and trim so cosmetic formatting differences
        // between the monolith DDL and an extracted service's DDL don't change the hash.
        return Regex.Replace(sql, @"\s+", " ").Trim();
    }

    private static string InClause(IReadOnlyList<string> schemas) =>
        string.Join(",", schemas.Select((_, i) => "@s" + i));

    private static void AddSchemaParams(SqlCommand cmd, IReadOnlyList<string> schemas)
    {
        for (var i = 0; i < schemas.Count; i++)
        {
            cmd.Parameters.AddWithValue("@s" + i, schemas[i]);
        }
    }

    private static List<ColumnRow> LoadColumns(SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.ORDINAL_POSITION,
       c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
       c.IS_NULLABLE, c.COLUMN_DEFAULT,
       COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS IsIdentity,
       t.TABLE_TYPE
FROM INFORMATION_SCHEMA.COLUMNS c
JOIN INFORMATION_SCHEMA.TABLES t
  ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
WHERE c.TABLE_SCHEMA IN ({InClause(schemas)})";

        var rows = new List<ColumnRow>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ColumnRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Name = reader.GetString(2),
                Ordinal = reader.GetInt32(3),
                DataType = reader.GetString(4),
                MaxLength = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Precision = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6)),
                Scale = reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7)),
                IsNullable = reader.GetString(8) == "YES",
                Default = reader.IsDBNull(9) ? null : NormalizeSql(reader.GetString(9)),
                IsIdentity = !reader.IsDBNull(10) && Convert.ToInt32(reader.GetValue(10)) == 1,
                IsBaseTable = reader.GetString(11) == "BASE TABLE",
            });
        }

        return rows;
    }

    private static List<KeyRow> LoadKeyConstraints(SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT tc.TABLE_SCHEMA, tc.TABLE_NAME, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE,
       kcu.COLUMN_NAME, kcu.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
  ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE') AND tc.TABLE_SCHEMA IN ({InClause(schemas)})";

        var rows = new List<KeyRow>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new KeyRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Constraint = reader.GetString(2),
                Type = reader.GetString(3),
                Column = reader.GetString(4),
                Ordinal = reader.GetInt32(5),
            });
        }

        return rows;
    }

    private static List<ForeignKeyRow> LoadForeignKeys(SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT sch.name, tp.name, fk.name, cp.name, rsch.name, tr.name, cr.name,
       fk.delete_referential_action_desc, fk.update_referential_action_desc, fkc.constraint_column_id
FROM sys.foreign_keys fk
JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
JOIN sys.schemas sch ON sch.schema_id = tp.schema_id
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
JOIN sys.schemas rsch ON rsch.schema_id = tr.schema_id
JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
WHERE sch.name IN ({InClause(schemas)})";

        var rows = new List<ForeignKeyRow>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ForeignKeyRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Name = reader.GetString(2),
                Column = reader.GetString(3),
                RefSchema = reader.GetString(4),
                RefTable = reader.GetString(5),
                RefColumn = reader.GetString(6),
                OnDelete = reader.GetString(7),
                OnUpdate = reader.GetString(8),
                Ordinal = reader.GetInt32(9),
            });
        }

        return rows;
    }

    private static List<IndexRow> LoadIndexes(SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT sch.name, t.name, i.name, i.is_unique, i.is_primary_key, i.type_desc,
       c.name, ic.key_ordinal, ic.is_included_column
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas sch ON sch.schema_id = t.schema_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE sch.name IN ({InClause(schemas)}) AND i.name IS NOT NULL";

        var rows = new List<IndexRow>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Index = reader.GetString(2),
                IsUnique = reader.GetBoolean(3),
                IsPrimaryKey = reader.GetBoolean(4),
                TypeDesc = reader.GetString(5),
                Column = reader.GetString(6),
                KeyOrdinal = reader.GetByte(7),
                IsIncluded = reader.GetBoolean(8),
            });
        }

        return rows;
    }

    private static List<CheckRow> LoadCheckConstraints(SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT sch.name, t.name, cc.name, cc.definition
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id = cc.parent_object_id
JOIN sys.schemas sch ON sch.schema_id = t.schema_id
WHERE sch.name IN ({InClause(schemas)})";

        var rows = new List<CheckRow>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CheckRow
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                Name = reader.GetString(2),
                Definition = reader.GetString(3),
            });
        }

        return rows;
    }

    private static Dictionary<(string Schema, string View), string> LoadViewDefinitions(
        SqlConnection connection, IReadOnlyList<string> schemas)
    {
        var sql = $@"
SELECT sch.name, v.name, m.definition
FROM sys.views v
JOIN sys.schemas sch ON sch.schema_id = v.schema_id
JOIN sys.sql_modules m ON m.object_id = v.object_id
WHERE sch.name IN ({InClause(schemas)})";

        var result = new Dictionary<(string, string), string>();
        using var cmd = new SqlCommand(sql, connection);
        AddSchemaParams(cmd, schemas);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return result;
    }

    private sealed class ColumnRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int Ordinal { get; set; }

        public string DataType { get; set; } = string.Empty;

        public int? MaxLength { get; set; }

        public int? Precision { get; set; }

        public int? Scale { get; set; }

        public bool IsNullable { get; set; }

        public string? Default { get; set; }

        public bool IsIdentity { get; set; }

        public bool IsBaseTable { get; set; }
    }

    private sealed class KeyRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public string Constraint { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Column { get; set; } = string.Empty;

        public int Ordinal { get; set; }
    }

    private sealed class ForeignKeyRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Column { get; set; } = string.Empty;

        public string RefSchema { get; set; } = string.Empty;

        public string RefTable { get; set; } = string.Empty;

        public string RefColumn { get; set; } = string.Empty;

        public string OnDelete { get; set; } = string.Empty;

        public string OnUpdate { get; set; } = string.Empty;

        public int Ordinal { get; set; }
    }

    private sealed class IndexRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public string Index { get; set; } = string.Empty;

        public bool IsUnique { get; set; }

        public bool IsPrimaryKey { get; set; }

        public string TypeDesc { get; set; } = string.Empty;

        public string Column { get; set; } = string.Empty;

        public int KeyOrdinal { get; set; }

        public bool IsIncluded { get; set; }
    }

    private sealed class CheckRow
    {
        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Definition { get; set; } = string.Empty;
    }
}
