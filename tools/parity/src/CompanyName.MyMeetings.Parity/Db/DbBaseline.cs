using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CompanyName.MyMeetings.Parity.Db;

/// <summary>
/// Baseline 1 — DB data parity. Extracts every base table and read-model view into canonical
/// newline-delimited JSON (sorted keys), normalizing runtime GUIDs to stable tokens and excluding
/// volatile columns, then hashes each object and writes a manifest with per-schema aggregate hashes
/// and the identity fan-out invariant.
/// </summary>
public sealed class DbBaseline
{
    private readonly ParityOptions _options;

    public DbBaseline(ParityOptions options) => _options = options;

    public DbManifest Generate(string outputRoot)
    {
        var snapshot = FixtureSnapshot.Capture(_options.ConnectionString);
        var tokenizer = snapshot.Tokenizer;
        var sequences = snapshot.Sequences;

        // Serialize canonical NDJSON per object and hash it.
        var entries = new List<DbObjectEntry>();
        foreach (var obj in snapshot.Objects.OrderBy(o => o.Schema, StringComparer.Ordinal)
                                    .ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            entries.Add(WriteObject(obj, snapshot.RowsByKey[obj.Key], tokenizer, sequences, outputRoot));
        }

        var schemaHashes = entries
            .GroupBy(e => e.Schema, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => Canonical.RollupSha256(g.Select(e => e.Sha256)),
                StringComparer.Ordinal);

        var invariant = CheckIdentityFanOut(snapshot.RowsByKey, tokenizer);

        var manifest = new DbManifest
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ClockFrozenAtUtc = FixtureConstants.FrozenClockUtc,
            GuidTokenCount = tokenizer.Count,
            Objects = entries.OrderBy(e => e.Schema, StringComparer.Ordinal)
                             .ThenBy(e => e.Name, StringComparer.Ordinal).ToList(),
            SchemaHashes = schemaHashes,
            IdentityFanOut = invariant,
            ExcludedNonDeterministic = snapshot.ExcludedNames,
        };

        var manifestJson = Canonical.SerializeObject(manifest);
        File.WriteAllText(Path.Combine(outputRoot, "manifest.json"), manifestJson);
        return manifest;
    }


    private DbObjectEntry WriteObject(
        DbObject obj,
        List<Dictionary<string, object?>> rows,
        GuidTokenizer tokenizer,
        SequenceMaps sequences,
        string outputRoot)
    {
        var volatileColumns = obj.Columns
            .Where(c => ParityConfig.IsVolatile(obj.Schema, obj.Name, c.Name, c.SqlType))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var hasPk = obj.PrimaryKey.Count > 0;
        var canonicalRows = new List<(JsonObject Node, string Line)>();
        foreach (var row in rows)
        {
            var node = new JsonObject();
            foreach (var col in obj.Columns)
            {
                if (volatileColumns.Contains(col.Name))
                {
                    continue;
                }

                if (sequences.TryNormalize(obj.Schema, obj.Name, col.Name, row[col.Name], out var rank))
                {
                    node[col.Name] = JsonValue.Create(rank);
                    continue;
                }

                node[col.Name] = ToCanonicalValue(row[col.Name], col, tokenizer);
            }

            canonicalRows.Add((node, Canonical.Serialize(node)));
        }

        // Order rows by primary key (post-normalization, type-aware) when one exists — mirroring the
        // documented `orderBy` — falling back to full canonical content for views without a PK.
        List<string> canonicalLines;
        if (hasPk)
        {
            canonicalLines = canonicalRows
                .OrderBy(r => r, RowPkComparer.For(obj.PrimaryKey))
                .Select(r => r.Line)
                .ToList();
        }
        else
        {
            canonicalLines = canonicalRows.Select(r => r.Line).OrderBy(l => l, StringComparer.Ordinal).ToList();
        }

        var ndjson = string.Join('\n', canonicalLines);
        if (canonicalLines.Count > 0)
        {
            ndjson += "\n";
        }

        var relDir = Path.Combine("db", obj.Schema);
        var absDir = Path.Combine(outputRoot, obj.Schema);
        Directory.CreateDirectory(absDir);
        File.WriteAllText(Path.Combine(absDir, obj.Name + ".ndjson"), ndjson, new UTF8Encoding(false));

        var orderBy = obj.PrimaryKey.Count > 0 ? obj.PrimaryKey.ToList() : new List<string> { "<canonical-content>" };

        return new DbObjectEntry
        {
            Schema = obj.Schema,
            Name = obj.Name,
            Kind = obj.Kind,
            RowCount = rows.Count,
            Sha256 = Canonical.Sha256Hex(ndjson),
            OrderBy = orderBy,
            VolatileColumns = volatileColumns,
            Path = Path.Combine(relDir, obj.Name + ".ndjson").Replace('\\', '/'),
        };
    }

    private sealed class RowPkComparer : IComparer<(JsonObject Node, string Line)>
    {
        private readonly IReadOnlyList<string> _pkColumns;

        private RowPkComparer(IReadOnlyList<string> pkColumns) => _pkColumns = pkColumns;

        public static RowPkComparer For(IReadOnlyList<string> pkColumns) => new(pkColumns);

        public int Compare((JsonObject Node, string Line) x, (JsonObject Node, string Line) y)
        {
            foreach (var col in _pkColumns)
            {
                var cmp = CompareNode(x.Node[col], y.Node[col]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            // Total-order tiebreak (e.g. a volatile PK column excluded from the node).
            return string.CompareOrdinal(x.Line, y.Line);
        }

        private static int CompareNode(JsonNode? a, JsonNode? b)
        {
            if (a is null && b is null)
            {
                return 0;
            }

            if (a is null)
            {
                return -1;
            }

            if (b is null)
            {
                return 1;
            }

            if (a is JsonValue av && b is JsonValue bv
                && av.TryGetValue<long>(out var al) && bv.TryGetValue<long>(out var bl))
            {
                return al.CompareTo(bl);
            }

            return string.CompareOrdinal(a.ToJsonString(), b.ToJsonString());
        }
    }

    private static JsonNode? ToCanonicalValue(object? value, DbColumn col, GuidTokenizer tokenizer)
    {
        if (value is null)
        {
            return null;
        }

        switch (value)
        {
            case Guid g:
                return JsonValue.Create(tokenizer.Register(g));
            case string s:
                return JsonValue.Create(VolatileText.Normalize(s, tokenizer));
            case bool b:
                return JsonValue.Create(b);
            case byte[] bytes:
                return JsonValue.Create(Convert.ToBase64String(bytes));
            case DateTime dt:
                return JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture));
            case decimal dec:
                return JsonValue.Create(dec.ToString(CultureInfo.InvariantCulture));
            case byte or short or int or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case float or double:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture));
            default:
                return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }


    private static IdentityFanOutResult CheckIdentityFanOut(
        IReadOnlyDictionary<string, List<Dictionary<string, object?>>> rowsByKey,
        GuidTokenizer tokenizer)
    {
        HashSet<Guid> Ids(string key, string column) =>
            rowsByKey.TryGetValue(key, out var rows)
                ? rows.Where(r => r[column] is Guid).Select(r => (Guid)r[column]!).ToHashSet()
                : new HashSet<Guid>();

        var users = Ids("users.users", "Id");
        var meetingMembers = Ids("meetings.members", "Id");
        var adminMembers = Ids("administration.members", "Id");
        var payers = Ids("payments.payers", "Id");

        var intersection = users
            .Intersect(meetingMembers)
            .Intersect(adminMembers)
            .Intersect(payers)
            .ToList();

        return new IdentityFanOutResult
        {
            Satisfied = intersection.Count > 0,
            SharedIdentityTokens = intersection
                .Select(g => tokenizer.TryGet(g, out var t) ? t : g.ToString())
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
            Counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["users.Users"] = users.Count,
                ["meetings.Members"] = meetingMembers.Count,
                ["administration.Members"] = adminMembers.Count,
                ["payments.Payers"] = payers.Count,
            },
        };
    }
}
