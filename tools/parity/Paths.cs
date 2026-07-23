namespace CompanyName.MyMeetings.Tools.Parity;

internal static class Paths
{
    /// <summary>
    /// Locate the <c>tools/parity</c> directory by walking up from the current
    /// directory and then the executable directory, so baselines resolve to the
    /// source tree regardless of where the tool is launched from.
    /// </summary>
    public static string FindToolDirectory()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "parity");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "config", "endpoints.json")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        // Fall back to the executable directory (baseline/config resolved relative to it).
        return AppContext.BaseDirectory;
    }
}
