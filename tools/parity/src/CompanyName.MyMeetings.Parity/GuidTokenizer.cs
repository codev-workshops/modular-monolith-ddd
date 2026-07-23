using System.Text.RegularExpressions;

namespace CompanyName.MyMeetings.Parity;

/// <summary>
/// Maps every runtime-generated GUID to a stable ordinal token (e.g. <c>#GUID_0001#</c>).
/// The domain-population step (SUT harness) creates aggregates with <c>Guid.NewGuid()</c>, so raw
/// GUID values differ on every capture. Replacing them with first-seen ordinals lets the hashed
/// baseline stay stable across runs while still detecting structural/relationship changes (the same
/// physical GUID always maps to the same token, so identity fan-out and foreign keys are preserved).
///
/// Tokens are assigned during a deterministic "seed" pass over anchor tables ordered by business
/// keys, so a given logical entity receives the same token on every run.
/// </summary>
public sealed class GuidTokenizer
{
    // Hyphenated GUID (as stored in uniqueidentifier columns / JsonData) and the compact 32-hex form
    // that the event store embeds inside stream identifiers (e.g. "Payer-6eb41b7c2b96...").
    private static readonly Regex HyphenatedGuid = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex CompactGuid = new(
        "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])",
        RegexOptions.Compiled);

    private readonly Dictionary<Guid, int> _tokens = new();
    private int _next = 1;

    public int Count => _tokens.Count;

    /// <summary>Register a GUID value (idempotent) and return its token.</summary>
    public string Register(Guid value)
    {
        if (!_tokens.TryGetValue(value, out var id))
        {
            id = _next++;
            _tokens[value] = id;
        }

        return Token(id);
    }

    public bool TryGet(Guid value, out string token)
    {
        if (_tokens.TryGetValue(value, out var id))
        {
            token = Token(id);
            return true;
        }

        token = string.Empty;
        return false;
    }

    /// <summary>Replace every embedded GUID (hyphenated or compact) inside a string with its token.</summary>
    public string ReplaceInString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = HyphenatedGuid.Replace(input, m =>
            Guid.TryParse(m.Value, out var g) ? Register(g) : m.Value);

        result = CompactGuid.Replace(result, m =>
            Guid.TryParseExact(m.Value, "N", out var g) ? Register(g) : m.Value);

        return result;
    }

    /// <summary>Scan a string for embedded GUIDs and register them (used by the seed pass).</summary>
    public void ScanString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        foreach (Match m in HyphenatedGuid.Matches(input))
        {
            if (Guid.TryParse(m.Value, out var g))
            {
                Register(g);
            }
        }

        foreach (Match m in CompactGuid.Matches(input))
        {
            if (Guid.TryParseExact(m.Value, "N", out var g))
            {
                Register(g);
            }
        }
    }

    private static string Token(int id) => $"#GUID_{id:D4}#";
}
