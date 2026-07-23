namespace CompanyName.MyMeetings.Tools.Parity;

internal enum Mode
{
    None,
    Capture,
    Verify,
}

internal sealed class BaselineEntry
{
    public string Key { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}

internal sealed class CliOptions
{
    private const string DefaultConnectionString =
        "Server=localhost,1433;Database=MyMeetings;User Id=sa;Password=Test@12345;Encrypt=False;TrustServerCertificate=True;";

    private const string DefaultApiBaseUrl = "http://127.0.0.1:5000";

    private static readonly string[] AllDimensions = { "db", "api", "dto" };

    public Mode Mode { get; private set; } = Mode.None;

    public List<string> Dimensions { get; private set; } = new();

    public string? Module { get; private set; }

    public string? BaselineDir { get; private set; }

    public string? ConfigPath { get; private set; }

    public string? DetailsDir { get; private set; }

    public string ConnectionString { get; private set; } = DefaultConnectionString;

    public string ApiBaseUrl { get; private set; } = DefaultApiBaseUrl;

    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var options = new CliOptions
        {
            Mode = args[0].ToLowerInvariant() switch
            {
                "capture" => Mode.Capture,
                "verify" => Mode.Verify,
                _ => Mode.None,
            },
            ConnectionString = Environment.GetEnvironmentVariable("PARITY_CONNECTION_STRING") ?? DefaultConnectionString,
            ApiBaseUrl = Environment.GetEnvironmentVariable("PARITY_API_BASE_URL") ?? DefaultApiBaseUrl,
        };

        if (options.Mode == Mode.None)
        {
            return null;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dimensions":
                case "-d":
                    options.Dimensions = RequireValue(args, ref i)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => x.ToLowerInvariant())
                        .ToList();
                    break;
                case "--module":
                case "-m":
                    options.Module = RequireValue(args, ref i);
                    break;
                case "--baseline":
                    options.BaselineDir = RequireValue(args, ref i);
                    break;
                case "--config":
                    options.ConfigPath = RequireValue(args, ref i);
                    break;
                case "--details":
                    options.DetailsDir = RequireValue(args, ref i);
                    break;
                case "--connection-string":
                    options.ConnectionString = RequireValue(args, ref i);
                    break;
                case "--api-base-url":
                    options.ApiBaseUrl = RequireValue(args, ref i);
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{arg}'");
            }
        }

        if (options.Dimensions.Count == 0)
        {
            options.Dimensions = AllDimensions.ToList();
        }

        foreach (var dimension in options.Dimensions)
        {
            if (!AllDimensions.Contains(dimension))
            {
                throw new ArgumentException($"unknown dimension '{dimension}' (valid: {string.Join(", ", AllDimensions)})");
            }
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Parity — capture / verify DB, API and DTO contract hashes for the MyMeetings
            modular monolith so microservice extraction can be gated against a baseline.

            Usage:
              parity capture [options]     Compute hashes and write baseline files.
              parity verify  [options]     Recompute and compare against the baseline.

            Options:
              -d, --dimensions <list>   Comma list of db,api,dto (default: all).
              -m, --module <name>       Restrict to one track: Meetings, Administration,
                                        Payments, Registrations, UserAccess, App.
                  --baseline <dir>      Baseline directory (default: tools/parity/baseline).
                  --config <file>       API endpoints config (default: tools/parity/config/endpoints.json).
                  --details <dir>       Also write the full pre-hash canonical model per entry
                                        (for diffing a mismatch). Not used for hashing.
                  --connection-string   SQL Server connection string
                                        (or env PARITY_CONNECTION_STRING).
                  --api-base-url <url>  Running host base URL for the api dimension
                                        (or env PARITY_API_BASE_URL; default http://127.0.0.1:5000).

            Env:
              PARITY_API_BEARER         Optional bearer token sent with api requests.

            Exit codes: 0 = pass, 1 = verify mismatch, 2 = usage error, 3 = runtime error.
            """);
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"missing value for '{args[i]}'");
        }

        return args[++i];
    }
}
