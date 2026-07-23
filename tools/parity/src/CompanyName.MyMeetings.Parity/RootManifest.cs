using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompanyName.MyMeetings.Parity;

/// <summary>
/// Merkle-style root rollup: <c>rootSha256 = SHA-256(sorted child-manifest hashes)</c> for the
/// db / api / contracts sub-manifests, so any leaf change bubbles up to a single comparable root.
/// </summary>
public static class RootManifest
{
    private static readonly string[] ChildRelativePaths = { "db/manifest.json", "api/golden.json", "contracts/manifest.json" };

    public static bool AllChildrenPresent(string root) =>
        ChildRelativePaths.All(rel => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));

    public static void Write(string baselineOrWorkRoot)
    {
        var children = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in ChildRelativePaths)
        {
            var path = Path.Combine(baselineOrWorkRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                children[rel] = HashContentCanonically(path);
            }
        }

        var rootSha = Canonical.RollupSha256(children.Values);

        var manifest = new JsonObject
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["clockFrozenAtUtc"] = FixtureConstants.FrozenClockUtc,
            ["rootSha256"] = rootSha,
            ["children"] = new JsonObject(children.ToDictionary(
                kvp => kvp.Key,
                kvp => (JsonNode?)JsonValue.Create(kvp.Value))),
        };

        File.WriteAllText(
            Path.Combine(baselineOrWorkRoot, "baseline.manifest.json"),
            Canonical.Serialize(manifest));
    }

    public static string? TryReadRootSha(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(manifestPath));
            return node?["rootSha256"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hash a child manifest ignoring its volatile <c>generatedAtUtc</c> so the root is reproducible.
    /// </summary>
    private static string HashContentCanonically(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key)
                         .Where(k => string.Equals(k, "generatedAtUtc", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                obj.Remove(key);
            }
        }

        return Canonical.Sha256Hex(Canonical.Serialize(node));
    }
}
