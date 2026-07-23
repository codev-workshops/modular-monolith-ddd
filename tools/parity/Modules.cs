namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// The five extraction tracks plus the shared <c>app</c> schema. Each track maps
/// to exactly one database schema and (where applicable) one Application +
/// IntegrationEvents assembly pair. This mapping is the single source of truth
/// used by every parity dimension so that a track's DB, API and DTO hashes are
/// grouped consistently.
/// </summary>
internal sealed record ModuleDescriptor(
    string Name,
    string DbSchema,
    string? ApplicationAssembly,
    string? IntegrationEventsAssembly);

internal static class Modules
{
    public static readonly IReadOnlyList<ModuleDescriptor> All = new List<ModuleDescriptor>
    {
        new(
            "Meetings",
            "meetings",
            "CompanyName.MyMeetings.Modules.Meetings.Application",
            "CompanyName.MyMeetings.Modules.Meetings.IntegrationEvents"),
        new(
            "Administration",
            "administration",
            "CompanyName.MyMeetings.Modules.Administration.Application",
            "CompanyName.MyMeetings.Modules.Administration.IntegrationEvents"),
        new(
            "Payments",
            "payments",
            "CompanyName.MyMeetings.Modules.Payments.Application",
            "CompanyName.MyMeetings.Modules.Payments.IntegrationEvents"),
        new(
            "Registrations",
            "registrations",
            "CompanyName.MyMeetings.Modules.Registrations.Application",
            "CompanyName.MyMeetings.Modules.Registrations.IntegrationEvents"),
        new(
            "UserAccess",
            "users",
            "CompanyName.MyMeetings.Modules.UserAccess.Application",
            "CompanyName.MyMeetings.Modules.UserAccess.IntegrationEvents"),
        new(
            "App",
            "app",
            null,
            null),
    };

    public static ModuleDescriptor? ByName(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
