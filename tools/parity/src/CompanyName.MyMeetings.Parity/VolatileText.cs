using System.Text.RegularExpressions;

namespace CompanyName.MyMeetings.Parity;

/// <summary>
/// Normalizes non-deterministic fragments embedded inside free-text/JSON string values (event-store
/// payloads, outbox/inbox message data, API response bodies): runtime GUIDs -> stable tokens, and
/// wall-clock ISO-8601 timestamps -> a fixed <c>#TS#</c> placeholder. This is the string-level
/// counterpart to the column-level volatile allowlist.
/// </summary>
public static class VolatileText
{
    private static readonly Regex IsoTimestamp = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
        RegexOptions.Compiled);

    public const string TimestampPlaceholder = "#TS#";

    private static readonly Regex AnyHyphenatedGuid = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex AnyCompactGuid = new(
        "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])",
        RegexOptions.Compiled);

    public static string Normalize(string input, GuidTokenizer tokenizer)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var withTokens = tokenizer.ReplaceInString(input);
        return IsoTimestamp.Replace(withTokens, TimestampPlaceholder);
    }

    /// <summary>
    /// Identity-agnostic strip used only to build deterministic ordering keys: replaces every GUID and
    /// timestamp with a constant placeholder without allocating tokens.
    /// </summary>
    public static string Strip(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = AnyHyphenatedGuid.Replace(input, "#G#");
        result = AnyCompactGuid.Replace(result, "#G#");
        return IsoTimestamp.Replace(result, TimestampPlaceholder);
    }
}
