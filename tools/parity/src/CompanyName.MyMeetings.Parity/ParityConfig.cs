namespace CompanyName.MyMeetings.Parity;

/// <summary>Runtime options resolved from CLI args / environment variables.</summary>
public sealed class ParityOptions
{
    public required string ConnectionString { get; init; }

    public required string ApiBaseUrl { get; init; }

    /// <summary>Directory that contains the built application assemblies (for reflection).</summary>
    public required string AppBinDir { get; init; }

    /// <summary>Output directory for the baseline tree (the committed <c>parity-baseline/</c>).</summary>
    public required string BaselineDir { get; init; }

    /// <summary>Working directory used by <c>verify</c> to recompute artifacts before diffing.</summary>
    public required string WorkDir { get; init; }

    public required string RepoRoot { get; init; }
}

/// <summary>
/// Determinism configuration: which columns are volatile (excluded from the hash) and the order in
/// which "anchor" tables are visited so runtime GUIDs receive stable ordinal tokens.
/// </summary>
public static class ParityConfig
{
    /// <summary>
    /// Columns whose values are non-deterministic across runs and are therefore excluded from the
    /// canonical/hashed payload (they are still listed under <c>volatileColumns</c> in the manifest).
    /// Keyed by "schema.table" (lowercase) -> set of column names (case-insensitive).
    /// Covers: wall-clock timestamps populated via GETDATE()/GETUTCDATE()/SystemClock.Now, and the
    /// event-store's derived stream hash columns (deterministic functions of IdOriginal, which IS
    /// hashed after GUID normalization).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, HashSet<string>> VolatileColumns =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Password hashes carry a per-registration random salt (deterministic only for the fixed
            // seed identity), so they are excluded from the hash.
            ["users.users"] = Ci("Password"),
            ["users.v_users"] = Ci("Password"),
            ["registrations.userregistrations"] = Ci("RegisterDate", "ConfirmedDate", "Password"),
            ["registrations.v_userregistrations"] = Ci("RegisterDate", "ConfirmedDate", "Password"),
            ["meetings.meetinggroups"] = Ci("CreateDate"),
            ["meetings.meetings"] = Ci("CreateDate", "ChangeDate", "CancelDate"),
            ["meetings.meetinggroupproposals"] = Ci("ProposalDate", "ProposalDateStandardized", "DecisionDate"),
            ["meetings.v_meetinggroupproposals"] = Ci("DecisionDate"),
            ["administration.meetinggroupproposals"] = Ci("ProposalDate", "DecisionDate"),
            ["administration.v_meetinggroupproposals"] = Ci("DecisionDate"),
            ["meetings.meetingcomments"] = Ci("CreateDate", "EditDate"),
            ["payments.messages"] = Ci("Created"),
            ["payments.subscriptiondetails"] = Ci("ExpirationDate"),
            ["payments.subscriptionpayments"] = Ci("Date"),
            ["payments.streams"] = Ci("Id", "IdOriginalReversed"),
            // Async read-model cursor: advances as the subscription projector consumes the event
            // stream, so its exact position is timing-dependent across environments (the checkpoint
            // row itself, keyed by Code, is retained — only the moving Position is excluded).
            ["payments.subscriptioncheckpoints"] = Ci("Position"),
            ["app.emails"] = Ci("Date"),
            ["app.migrationsjournal"] = Ci("Applied"),
        };

    /// <summary>
    /// Fixed visitation order for the GUID-token seed pass. Anchor entity tables (which give each
    /// GUID a business meaning via a unique natural key) come first so tokens are assigned stably;
    /// remaining tables are visited in canonical (schema, name) order afterwards.
    /// </summary>
    public static readonly string[] AnchorOrder =
    {
        "meetings.countries",
        "users.users",
        "registrations.userregistrations",
        "meetings.members",
        "administration.members",
        "payments.payers",
        "meetings.meetinggroups",
        "meetings.meetings",
        "meetings.meetinggroupproposals",
        "administration.meetinggroupproposals",
        "payments.pricelistitems",
        "payments.subscriptiondetails",
        "payments.subscriptionpayments",
        "payments.streams",
        "payments.messages",
        "meetings.meetingattendees",
        "meetings.meetinggroupmembers",
    };

    /// <summary>
    /// Transient message-bus plumbing tables. Their contents and even row counts depend on
    /// asynchronous outbox/inbox processing timing, so they are excluded from the deterministic
    /// baseline (listed by name in the manifest for transparency). They are not domain invariants.
    /// </summary>
    private static readonly string[] ExcludedTableSuffixes =
    {
        "inboxmessages", "outboxmessages", "internalcommands",
    };

    public static bool IsVolatile(string schema, string table, string column, string sqlType)
    {
        var key = $"{schema}.{table}".ToLowerInvariant();
        _ = sqlType;
        return VolatileColumns.TryGetValue(key, out var cols) && cols.Contains(column);
    }

    public static bool IsExcludedNonDeterministic(string table)
    {
        var lower = table.ToLowerInvariant();
        return ExcludedTableSuffixes.Any(s => lower.Equals(s, StringComparison.Ordinal));
    }

    public static bool IsDateType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" or "date" => true,
        _ => false,
    };

    private static HashSet<string> Ci(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);
}
