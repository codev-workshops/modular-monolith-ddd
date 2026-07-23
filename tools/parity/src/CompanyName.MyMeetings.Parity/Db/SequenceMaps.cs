using System.Globalization;

namespace CompanyName.MyMeetings.Parity.Db;

/// <summary>
/// Normalizes the event store's auto-increment / positional columns to dense ranks so their absolute
/// values (which depend on IDENTITY seed state carried across runs) do not leak into the hash, while
/// the relative ordering and the Streams&lt;-&gt;Messages relationship are preserved.
///
///  * <c>payments.Streams.IdInternal</c>  (IDENTITY, PK, referenced by Messages) -> 1-based rank by value
///  * <c>payments.Messages.StreamIdInternal</c> (FK -> Streams.IdInternal)        -> same stream rank
///  * <c>payments.Streams.Position</c> / <c>payments.Messages.Position</c>        -> 0-based global position rank
/// </summary>
public sealed class SequenceMaps
{
    private readonly Dictionary<long, long> _streamRank = new();
    private readonly Dictionary<long, long> _positionRank = new();

    public static SequenceMaps Build(
        IReadOnlyDictionary<string, List<Dictionary<string, object?>>> rowsByKey)
    {
        var maps = new SequenceMaps();

        if (rowsByKey.TryGetValue("payments.streams", out var streams))
        {
            var ids = streams
                .Select(r => ToLong(r["IdInternal"]))
                .Where(v => v.HasValue).Select(v => v!.Value)
                .Distinct().OrderBy(v => v).ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                maps._streamRank[ids[i]] = i + 1;
            }
        }

        var positions = new SortedSet<long>();
        if (rowsByKey.TryGetValue("payments.messages", out var messages))
        {
            foreach (var r in messages)
            {
                var p = ToLong(r["Position"]);
                if (p.HasValue && p.Value >= 0)
                {
                    positions.Add(p.Value);
                }
            }
        }

        var ordered = positions.ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            maps._positionRank[ordered[i]] = i;
        }

        return maps;
    }

    public bool TryNormalize(string schema, string table, string column, object? value, out long normalized)
    {
        normalized = 0;
        var key = $"{schema}.{table}".ToLowerInvariant();
        var col = column.ToLowerInvariant();
        var raw = ToLong(value);
        if (raw is null)
        {
            return false;
        }

        if (key == "payments.streams" && col == "idinternal")
        {
            return _streamRank.TryGetValue(raw.Value, out normalized);
        }

        if (key == "payments.messages" && col == "streamidinternal")
        {
            return _streamRank.TryGetValue(raw.Value, out normalized);
        }

        if ((key == "payments.streams" || key == "payments.messages") && col == "position")
        {
            if (raw.Value < 0)
            {
                normalized = raw.Value; // sentinel (-1) kept as-is
                return true;
            }

            return _positionRank.TryGetValue(raw.Value, out normalized);
        }

        return false;
    }

    private static long? ToLong(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var r) ? r : null,
    };
}
