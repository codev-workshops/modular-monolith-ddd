namespace CompanyName.MyMeetings.Parity;

/// <summary>
/// Re-runs the capture into a working directory and diffs the recomputed leaf artifacts against the
/// committed baseline, reporting the first divergent leaf hash and a summary. Manifest files are not
/// compared byte-for-byte (they carry a volatile <c>generatedAtUtc</c>); instead the content hashes
/// of the hashed leaves (NDJSON / response bodies / DTO schemas) are compared, which is exactly what
/// bubbles up into the Merkle root.
/// </summary>
public static class Verifier
{
    public static VerifyReport Compare(ParityOptions options, IReadOnlyCollection<string> sections)
    {
        var report = new VerifyReport();

        foreach (var section in sections.OrderBy(s => s, StringComparer.Ordinal))
        {
            var patterns = section switch
            {
                "db" => new[] { ("db", "*.ndjson") },
                "api" => new[] { (Path.Combine("api", "bodies"), "*.json"), ("api", "golden.json") },
                "contracts" => new[] { ("contracts", "*.schema.json") },
                _ => Array.Empty<(string, string)>(),
            };

            foreach (var (relDir, pattern) in patterns)
            {
                CompareTree(
                    Path.Combine(options.BaselineDir, relDir),
                    Path.Combine(options.WorkDir, relDir),
                    pattern,
                    section,
                    report);
            }
        }

        CompareRoot(options, report);
        return report;
    }

    private static void CompareTree(string baselineDir, string workDir, string pattern, string section, VerifyReport report)
    {
        var baseline = Hashes(baselineDir, pattern);
        var work = Hashes(workDir, pattern);

        foreach (var rel in baseline.Keys.Union(work.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            report.LeafCount++;
            var hasBase = baseline.TryGetValue(rel, out var bHash);
            var hasWork = work.TryGetValue(rel, out var wHash);

            if (!hasWork)
            {
                report.Add(section, rel, "MISSING (present in baseline, absent after re-run)");
            }
            else if (!hasBase)
            {
                report.Add(section, rel, "EXTRA (produced by re-run, absent in baseline)");
            }
            else if (!string.Equals(bHash, wHash, StringComparison.Ordinal))
            {
                report.Add(section, rel, $"DIVERGENT baseline={bHash![..12]}.. rerun={wHash![..12]}..");
            }
        }
    }

    private static Dictionary<string, string> Hashes(string dir, string pattern)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            result[rel] = Canonical.Sha256Hex(File.ReadAllBytes(file));
        }

        return result;
    }

    private static void CompareRoot(ParityOptions options, VerifyReport report)
    {
        var baselineRoot = RootManifest.TryReadRootSha(Path.Combine(options.BaselineDir, "baseline.manifest.json"));
        var workRoot = RootManifest.TryReadRootSha(Path.Combine(options.WorkDir, "baseline.manifest.json"));
        report.BaselineRootSha = baselineRoot;
        report.RerunRootSha = workRoot;
    }
}

public sealed class VerifyReport
{
    private readonly List<(string Section, string Leaf, string Detail)> _divergences = new();

    public int LeafCount { get; set; }

    public string? BaselineRootSha { get; set; }

    public string? RerunRootSha { get; set; }

    public bool AnyDivergence => _divergences.Count > 0
        || (BaselineRootSha is not null && RerunRootSha is not null
            && !string.Equals(BaselineRootSha, RerunRootSha, StringComparison.Ordinal));

    public void Add(string section, string leaf, string detail) => _divergences.Add((section, leaf, detail));

    public void Print()
    {
        Console.WriteLine($"[verify] compared {LeafCount} leaf artifacts");
        if (_divergences.Count == 0)
        {
            Console.WriteLine("[verify] all leaf hashes match the baseline");
        }
        else
        {
            var first = _divergences[0];
            Console.WriteLine($"[verify] FIRST DIVERGENT LEAF: [{first.Section}] {first.Leaf} -> {first.Detail}");
            Console.WriteLine($"[verify] total divergent leaves: {_divergences.Count}");
            foreach (var (section, leaf, detail) in _divergences.Take(50))
            {
                Console.WriteLine($"    [{section}] {leaf}: {detail}");
            }
        }

        Console.WriteLine($"[verify] rootSha256 baseline={BaselineRootSha ?? "<none>"}");
        Console.WriteLine($"[verify] rootSha256 rerun   ={RerunRootSha ?? "<none>"}");
        Console.WriteLine(AnyDivergence ? "[verify] RESULT: DIVERGENCE DETECTED" : "[verify] RESULT: PARITY OK");
    }
}
