namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// A single hashed unit within a dimension (e.g. one schema, one endpoint,
/// one DTO type group). <see cref="Details"/> holds the pre-hash canonical
/// material so a mismatch can be diffed by a human without re-running capture.
/// </summary>
internal sealed class ParityEntry
{
    public string Key { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public object? Details { get; set; }
}

/// <summary>One parity dimension: db, api or dto.</summary>
internal sealed class DimensionManifest
{
    public string Dimension { get; set; } = string.Empty;

    public List<ParityEntry> Entries { get; set; } = new();
}
