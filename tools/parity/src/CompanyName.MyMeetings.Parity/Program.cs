using CompanyName.MyMeetings.Parity;
using CompanyName.MyMeetings.Parity.Api;
using CompanyName.MyMeetings.Parity.Contracts;
using CompanyName.MyMeetings.Parity.Db;
using CompanyName.MyMeetings.Parity.Reflection;

var (mode, only) = ArgParser.Parse(args);
if (mode is null)
{
    Console.Error.WriteLine(
        "usage: parity <capture|verify> [--only db,api,contracts]\n" +
        "environment:\n" +
        "  PARITY_CONNECTION_STRING   SQL Server connection string (default: MyMeetings_SUTDatabaseConnectionString)\n" +
        "  PARITY_API_BASE_URL        API base url (default: http://127.0.0.1:5000)\n" +
        "  PARITY_APP_BIN_DIR         built API bin dir (for reflection)\n" +
        "  PARITY_REPO_ROOT           repo root (default: auto-detected)");
    return 2;
}

var options = OptionsBuilder.Build();
Console.WriteLine($"[parity] mode={mode} only={string.Join(',', only)}");
Console.WriteLine($"[parity] baselineDir={options.BaselineDir}");

var targetRoot = mode == "capture" ? options.BaselineDir : options.WorkDir;

if (only.Contains("db"))
{
    var dbOut = Path.Combine(targetRoot, "db");
    Directory.CreateDirectory(dbOut);
    Console.WriteLine("[parity] db: extracting tables + views ...");
    var manifest = new DbBaseline(options).Generate(dbOut);
    Console.WriteLine(
        $"[parity] db: {manifest.Objects.Count} objects, {manifest.GuidTokenCount} guid tokens, " +
        $"fan-out satisfied={manifest.IdentityFanOut.Satisfied} " +
        $"(shared={string.Join(",", manifest.IdentityFanOut.SharedIdentityTokens)})");
}

List<EndpointInfo> endpoints = new();
if (only.Contains("api") || only.Contains("contracts"))
{
    using var catalog = new EndpointCatalog(options.AppBinDir);
    endpoints = catalog.Enumerate();
    Console.WriteLine($"[parity] reflected {endpoints.Count} endpoints from the API assembly");
}

if (only.Contains("contracts"))
{
    var contractsOut = Path.Combine(targetRoot, "contracts");
    Console.WriteLine("[parity] contracts: generating DTO schemas ...");
    var manifest = new ContractsBaseline(options.AppBinDir).Generate(contractsOut, endpoints);
    Console.WriteLine($"[parity] contracts: {manifest.Dtos.Count} DTO schemas generated");
}

if (only.Contains("api"))
{
    // Start the API only now — after the DB snapshot — so its background processors never mutate the
    // database under the DB baseline. PARITY_MANAGE_API=false reuses an already-running API instead.
    var manageApi = !string.Equals(
        Environment.GetEnvironmentVariable("PARITY_MANAGE_API"), "false", StringComparison.OrdinalIgnoreCase);
    ApiHost? host = null;
    if (manageApi)
    {
        Console.WriteLine($"[parity] api: starting DEBUG API (frozen clock) from {options.AppBinDir} ...");
        host = ApiHost.Start(options);
    }

    try
    {
        var apiOut = Path.Combine(targetRoot, "api");
        Directory.CreateDirectory(apiOut);
        Console.WriteLine($"[parity] api: capturing golden dataset against {options.ApiBaseUrl} ...");
        var golden = await new ApiBaseline(options).GenerateAsync(apiOut, endpoints);
        var executed = golden.Count(e => e.Executed);
        Console.WriteLine(
            $"[parity] api: {golden.Count} entries ({executed} GET responses captured, " +
            $"{golden.Count - executed} matrix-only), 2 roles");
    }
    finally
    {
        host?.Dispose();
    }
}

// Merkle root: write it whenever all three child manifests are present in the target tree, so a split
// capture (db,contracts then api) still rolls up correctly.
if (RootManifest.AllChildrenPresent(targetRoot))
{
    RootManifest.Write(targetRoot);
    Console.WriteLine($"[parity] root: {RootManifest.TryReadRootSha(Path.Combine(targetRoot, "baseline.manifest.json"))}");
}

if (mode == "verify")
{
    var report = Verifier.Compare(options, only);
    report.Print();
    return report.AnyDivergence ? 1 : 0;
}

return 0;
