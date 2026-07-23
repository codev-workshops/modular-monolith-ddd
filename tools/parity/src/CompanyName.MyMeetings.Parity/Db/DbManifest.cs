namespace CompanyName.MyMeetings.Parity.Db;

public sealed class DbObjectEntry
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required int RowCount { get; init; }

    public required string Sha256 { get; init; }

    public required List<string> OrderBy { get; init; }

    public required List<string> VolatileColumns { get; init; }

    public required string Path { get; init; }
}

public sealed class IdentityFanOutResult
{
    public required bool Satisfied { get; init; }

    public required List<string> SharedIdentityTokens { get; init; }

    public required Dictionary<string, int> Counts { get; init; }
}

public sealed class DbManifest
{
    public required string GeneratedAtUtc { get; init; }

    public required string ClockFrozenAtUtc { get; init; }

    public required int GuidTokenCount { get; init; }

    public required List<DbObjectEntry> Objects { get; init; }

    public required Dictionary<string, string> SchemaHashes { get; init; }

    public required IdentityFanOutResult IdentityFanOut { get; init; }

    /// <summary>Transient message-bus tables intentionally excluded from the deterministic baseline.</summary>
    public required List<string> ExcludedNonDeterministic { get; init; }
}
