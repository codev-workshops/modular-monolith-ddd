namespace CompanyName.MyMeetings.Parity;

public static class ArgParser
{
    private static readonly string[] AllSections = { "db", "api", "contracts" };

    public static (string? Mode, HashSet<string> Only) Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return (null, new HashSet<string>());
        }

        var mode = args[0].ToLowerInvariant();
        if (mode != "capture" && mode != "verify")
        {
            return (null, new HashSet<string>());
        }

        var only = new HashSet<string>(AllSections, StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--only" && i + 1 < args.Length)
            {
                only = args[i + 1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.ToLowerInvariant())
                    .Where(AllSections.Contains)
                    .ToHashSet(StringComparer.Ordinal);
                i++;
            }
        }

        return (mode, only);
    }
}

public static class OptionsBuilder
{
    public static ParityOptions Build()
    {
        var repoRoot = Environment.GetEnvironmentVariable("PARITY_REPO_ROOT") ?? DetectRepoRoot();

        var conn = Environment.GetEnvironmentVariable("PARITY_CONNECTION_STRING")
                   ?? Environment.GetEnvironmentVariable("MyMeetings_SUTDatabaseConnectionString")
                   ?? "Server=localhost,1433;Database=MyMeetings;User=sa;Password=Test@12345;Encrypt=False;";

        // Env values are sometimes truncated at the first ';' by upstream tooling; repair if needed.
        if (!conn.Contains("Database=", StringComparison.OrdinalIgnoreCase))
        {
            conn = "Server=localhost,1433;Database=MyMeetings;User=sa;Password=Test@12345;Encrypt=False;";
        }

        // Must match the IdentityServer authority (http://localhost:5000 in IdentityConfiguration.cs):
        // the issuer baked into the token is the request host, and the API validates it against that
        // authority, so calling via 127.0.0.1 yields an issuer-mismatch 500.
        var apiBaseUrl = Environment.GetEnvironmentVariable("PARITY_API_BASE_URL") ?? "http://localhost:5000";

        var appBinDir = Environment.GetEnvironmentVariable("PARITY_APP_BIN_DIR")
                        ?? Path.Combine(repoRoot, "src", "API", "CompanyName.MyMeetings.API", "bin", "Debug", "net8.0");

        return new ParityOptions
        {
            ConnectionString = conn,
            ApiBaseUrl = apiBaseUrl.TrimEnd('/'),
            AppBinDir = appBinDir,
            BaselineDir = Path.Combine(repoRoot, "parity-baseline"),
            WorkDir = Path.Combine(repoRoot, "tools", "parity", ".work"),
            RepoRoot = repoRoot,
        };
    }

    private static string DetectRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "CompanyName.MyMeetings.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
