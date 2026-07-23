using System.Text.Json;
using CompanyName.MyMeetings.Tools.Parity;

var options = CliOptions.Parse(args);
if (options is null)
{
    CliOptions.PrintUsage();
    return 2;
}

var toolDir = Paths.FindToolDirectory();
var baselineDir = options.BaselineDir ?? Path.Combine(toolDir, "baseline");
var configPath = options.ConfigPath ?? Path.Combine(toolDir, "config", "endpoints.json");

try
{
    switch (options.Mode)
    {
        case Mode.Capture:
            return await CaptureAsync(options, baselineDir, configPath);
        case Mode.Verify:
            return await VerifyAsync(options, baselineDir, configPath);
        default:
            CliOptions.PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"parity: {ex.Message}");
    return 3;
}

async Task<int> CaptureAsync(CliOptions opts, string baseline, string config)
{
    Directory.CreateDirectory(baseline);

    foreach (var dimension in opts.Dimensions)
    {
        var entries = await ComputeAsync(dimension, opts, config);
        entries = Filter(entries, opts.Module);

        WriteDetails(opts.DetailsDir, dimension, entries);

        var slim = entries
            .OrderBy(e => e.Module, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new BaselineEntry { Key = e.Key, Module = e.Module, Hash = e.Hash })
            .ToList();

        var path = Path.Combine(baseline, dimension + ".json");
        File.WriteAllText(path, Canonical.ToJson(slim));
        Console.WriteLine($"captured {dimension}: {slim.Count} entries -> {Path.GetRelativePath(Environment.CurrentDirectory, path)}");
    }

    return 0;
}

async Task<int> VerifyAsync(CliOptions opts, string baseline, string config)
{
    var totalMismatches = 0;

    foreach (var dimension in opts.Dimensions)
    {
        var path = Path.Combine(baseline, dimension + ".json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[{dimension}] MISSING BASELINE: {path} (run 'capture' first)");
            totalMismatches++;
            continue;
        }

        var expected = JsonSerializer.Deserialize<List<BaselineEntry>>(File.ReadAllText(path)) ?? new();
        var expectedMap = expected.ToDictionary(e => e.Key, e => e);

        var actual = Filter(await ComputeAsync(dimension, opts, config), opts.Module);
        WriteDetails(opts.DetailsDir, dimension, actual);
        var actualMap = actual.ToDictionary(e => e.Key, e => e);

        var keys = expectedMap.Keys.Union(actualMap.Keys)
            .OrderBy(k => k, StringComparer.Ordinal);

        var dimMismatches = 0;
        var comparedCount = 0;
        foreach (var key in keys)
        {
            var hasExpected = expectedMap.TryGetValue(key, out var exp);
            var hasActual = actualMap.TryGetValue(key, out var act);

            if (opts.Module is not null && hasExpected && !exp!.Module.Equals(opts.Module, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            comparedCount++;
            if (hasExpected && hasActual)
            {
                if (exp!.Hash != act!.Hash)
                {
                    Console.Error.WriteLine($"[{dimension}] MISMATCH  {key}  (module {act.Module})");
                    Console.Error.WriteLine($"           expected {exp.Hash}");
                    Console.Error.WriteLine($"           actual   {act.Hash}");
                    dimMismatches++;
                }
            }
            else if (hasExpected)
            {
                Console.Error.WriteLine($"[{dimension}] MISSING   {key} present in baseline but not produced");
                dimMismatches++;
            }
            else
            {
                Console.Error.WriteLine($"[{dimension}] EXTRA     {key} produced but absent from baseline (module {act!.Module})");
                dimMismatches++;
            }
        }

        if (dimMismatches == 0)
        {
            Console.WriteLine($"[{dimension}] OK ({comparedCount} entries match)");
        }
        else
        {
            Console.WriteLine($"[{dimension}] FAILED ({dimMismatches} mismatch(es) of {comparedCount})");
        }

        totalMismatches += dimMismatches;
    }

    if (totalMismatches == 0)
    {
        Console.WriteLine("PARITY VERIFY PASSED");
        return 0;
    }

    Console.Error.WriteLine($"PARITY VERIFY FAILED: {totalMismatches} mismatch(es)");
    return 1;
}

async Task<List<ParityEntry>> ComputeAsync(string dimension, CliOptions opts, string config)
{
    return dimension switch
    {
        "db" => DatabaseParity.Capture(opts.ConnectionString),
        "dto" => DtoContractParity.Capture(),
        "api" => await ApiParity.CaptureAsync(opts.ApiBaseUrl, config),
        _ => throw new ArgumentException($"unknown dimension '{dimension}'"),
    };
}

void WriteDetails(string? detailsDir, string dimension, List<ParityEntry> entries)
{
    if (detailsDir is null)
    {
        return;
    }

    var dir = Path.Combine(detailsDir, dimension);
    Directory.CreateDirectory(dir);
    foreach (var entry in entries)
    {
        var safeKey = string.Join("_", $"{entry.Module}__{entry.Key}".Split(Path.GetInvalidFileNameChars()));
        File.WriteAllText(Path.Combine(dir, safeKey + ".json"), Canonical.ToJson(entry.Details ?? new { }));
    }
}

List<ParityEntry> Filter(List<ParityEntry> entries, string? module) =>
    module is null
        ? entries
        : entries.Where(e => e.Module.Equals(module, StringComparison.OrdinalIgnoreCase)).ToList();
