using System.Diagnostics;

namespace CompanyName.MyMeetings.Parity.Api;

/// <summary>
/// Starts the DEBUG-built API as a child process with the parity determinism controls (frozen clock,
/// deterministic connection string), waits until its discovery endpoint is ready, and stops it on
/// dispose. Capturing the DB baseline BEFORE this host starts is essential: the running API's
/// background outbox/inbox processors mutate the database continuously, so the DB must be snapshotted
/// while the API is down.
/// </summary>
public sealed class ApiHost : IDisposable
{
    private readonly Process _process;

    private ApiHost(Process process) => _process = process;

    public static ApiHost Start(ParityOptions options)
    {
        var dll = Path.Combine(options.AppBinDir, "CompanyName.MyMeetings.API.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"API assembly not found at {dll}. Build the API in Debug first " +
                "(dotnet build src/API/CompanyName.MyMeetings.API -c Debug).");
        }

        var logPath = Path.Combine(Path.GetTempPath(), "parity-api-host.log");
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = options.AppBinDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = options.ApiBaseUrl;
        psi.Environment["PARITY_FROZEN_CLOCK"] = FixtureConstants.FrozenClockUtc;
        psi.Environment["Meetings_ConnectionStrings__MeetingsConnectionString"] = options.ConnectionString;

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new ApiHost(process);
        try
        {
            WaitForReadiness(options.ApiBaseUrl);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already gone.
        }

        _process.Dispose();
    }

    private static void WaitForReadiness(string baseUrl)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var discovery = baseUrl.TrimEnd('/') + "/.well-known/openid-configuration";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = http.GetAsync(discovery).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not up yet.
            }
            catch (TaskCanceledException)
            {
                // Timed out; retry.
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException($"API did not become ready at {discovery} within 60s.");
    }
}
