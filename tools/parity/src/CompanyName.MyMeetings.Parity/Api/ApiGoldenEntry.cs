namespace CompanyName.MyMeetings.Parity.Api;

/// <summary>One row of the API golden dataset: an endpoint captured for a single role.</summary>
public sealed class ApiGoldenEntry
{
    public required string Id { get; init; }

    public required string Method { get; init; }

    /// <summary>Route with runtime GUIDs replaced by stable tokens (command routes keep their template).</summary>
    public required string Route { get; init; }

    public required string Role { get; init; }

    public required string? Permission { get; init; }

    /// <summary>Expected authorization outcome for this role, from the role→permission mapping.</summary>
    public required bool Authorized { get; init; }

    /// <summary>True only for GET endpoints (commands are recorded but not executed).</summary>
    public required bool Executed { get; init; }

    public required int? Status { get; init; }

    public required string? ResponseType { get; init; }

    public required string? RequestBody { get; init; }

    public required string? BodySha256 { get; init; }

    public required string? BodyPath { get; init; }

    public required string Note { get; init; }
}
