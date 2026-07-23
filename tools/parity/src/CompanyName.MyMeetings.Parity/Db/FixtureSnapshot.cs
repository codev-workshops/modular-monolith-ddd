using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace CompanyName.MyMeetings.Parity.Db;

/// <summary>
/// An in-memory snapshot of the deterministic fixture database: all discovered (non-excluded) objects,
/// their rows, the event-store sequence maps, and a GUID tokenizer seeded so a given logical entity
/// always receives the same stable token. Shared by the DB baseline (baseline 1) and the API golden
/// dataset (baseline 2) so response GUIDs normalize to exactly the same tokens as the DB rows.
/// </summary>
public sealed class FixtureSnapshot
{
    private FixtureSnapshot(
        IReadOnlyList<DbObject> objects,
        Dictionary<string, List<Dictionary<string, object?>>> rowsByKey,
        SequenceMaps sequences,
        GuidTokenizer tokenizer,
        List<string> excludedNames)
    {
        Objects = objects;
        RowsByKey = rowsByKey;
        Sequences = sequences;
        Tokenizer = tokenizer;
        ExcludedNames = excludedNames;
    }

    public IReadOnlyList<DbObject> Objects { get; }

    public Dictionary<string, List<Dictionary<string, object?>>> RowsByKey { get; }

    public SequenceMaps Sequences { get; }

    public GuidTokenizer Tokenizer { get; }

    public List<string> ExcludedNames { get; }

    public static FixtureSnapshot Capture(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var allObjects = DbSchema.Discover(connection);
        var excludedNames = allObjects
            .Where(o => ParityConfig.IsExcludedNonDeterministic(o.Name))
            .Select(o => $"{o.Schema}.{o.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var objects = allObjects.Where(o => !ParityConfig.IsExcludedNonDeterministic(o.Name)).ToList();

        var rowsByKey = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            rowsByKey[obj.Key] = ReadRows(connection, obj);
        }

        var sequences = SequenceMaps.Build(rowsByKey);

        var tokenizer = new GuidTokenizer();
        SeedTokens(objects, rowsByKey, sequences, tokenizer);

        return new FixtureSnapshot(objects, rowsByKey, sequences, tokenizer, excludedNames);
    }

    private static List<Dictionary<string, object?>> ReadRows(SqlConnection connection, DbObject obj)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var cmd = new SqlCommand($"SELECT * FROM {obj.QualifiedName}", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var col in obj.Columns)
            {
                var ordinal = reader.GetOrdinal(col.Name);
                row[col.Name] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void SeedTokens(
        IReadOnlyList<DbObject> objects,
        IReadOnlyDictionary<string, List<Dictionary<string, object?>>> rowsByKey,
        SequenceMaps sequences,
        GuidTokenizer tokenizer)
    {
        var byKey = objects.ToDictionary(o => o.Key, o => o, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var order = new List<DbObject>();
        foreach (var key in ParityConfig.AnchorOrder)
        {
            if (byKey.TryGetValue(key, out var obj) && visited.Add(key))
            {
                order.Add(obj);
            }
        }

        foreach (var obj in objects
                     .Where(o => o.Kind == "table")
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            if (visited.Add(obj.Key))
            {
                order.Add(obj);
            }
        }

        foreach (var obj in objects
                     .Where(o => o.Kind == "view")
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            if (visited.Add(obj.Key))
            {
                order.Add(obj);
            }
        }

        foreach (var obj in order)
        {
            var rows = rowsByKey[obj.Key];
            var guidColumns = obj.Columns.Where(c => c.IsGuid).ToList();

            // Deterministic order: by a GUID-agnostic key built from non-volatile columns (GUIDs and
            // timestamps stripped, event-store sequence columns dense-ranked), so token assignment
            // never depends on the random GUID values it is about to tokenize.
            var ordered = rows
                .OrderBy(r => SeedKey(obj, r, sequences), StringComparer.Ordinal)
                .ToList();

            foreach (var row in ordered)
            {
                foreach (var col in guidColumns)
                {
                    if (row[col.Name] is Guid g)
                    {
                        tokenizer.Register(g);
                    }
                }

                foreach (var col in obj.Columns)
                {
                    if (row[col.Name] is string s)
                    {
                        tokenizer.ScanString(s);
                    }
                }
            }
        }
    }

    private static string SeedKey(DbObject obj, Dictionary<string, object?> row, SequenceMaps sequences)
    {
        var sb = new StringBuilder();
        foreach (var col in obj.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (ParityConfig.IsVolatile(obj.Schema, obj.Name, col.Name, col.SqlType))
            {
                continue;
            }

            sb.Append(col.Name).Append('=');

            if (sequences.TryNormalize(obj.Schema, obj.Name, col.Name, row[col.Name], out var rank))
            {
                sb.Append("seq:").Append(rank.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var value = row[col.Name];
                sb.Append(value switch
                {
                    // GUIDs are not yet tokenized here; use a constant so ordering ignores identity.
                    Guid => "#G#",
                    string s => VolatileText.Strip(s),
                    _ => Stringify(value),
                });
            }

            sb.Append('|');
        }

        return sb.ToString();
    }

    private static string Stringify(object? value) => value switch
    {
        null => "\u0000null",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
