using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.MyMeetings.Parity.Reflection;

namespace CompanyName.MyMeetings.Parity.Contracts;

/// <summary>
/// Baseline 3 — front-end contract grounding. Generates a JSON Schema for every DTO returned by a
/// controller (and every nested DTO reachable from it), with property names cased exactly as the API
/// serializes them (ASP.NET Core's System.Text.Json web defaults = camelCase), so casing/type drift
/// is caught. Writes one schema file per DTO plus a manifest mapping each DTO to the endpoint(s) that
/// return it and its schema SHA-256.
/// </summary>
public sealed class ContractsBaseline
{
    private readonly string _apiBinDir;

    public ContractsBaseline(string apiBinDir) => _apiBinDir = apiBinDir;

    public ContractsManifest Generate(string outputRoot, IReadOnlyList<EndpointInfo> endpoints)
    {
        Directory.CreateDirectory(outputRoot);

        using var catalog = new EndpointCatalog(_apiBinDir);

        // Root DTO types = response types referenced by endpoints.
        var rootTypeNames = endpoints
            .Where(e => e.ResponseType is not null)
            .Select(e => e.ResponseType!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var generator = new JsonSchemaGenerator(catalog.LoadContext, catalog.ApiAssembly);
        var schemas = generator.Generate(rootTypeNames);

        // Map DTO simple-name -> endpoints returning it.
        var endpointsByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in endpoints.Where(e => e.ResponseType is not null))
        {
            var simple = SimpleName(e.ResponseType!);
            if (!endpointsByType.TryGetValue(simple, out var list))
            {
                list = new List<string>();
                endpointsByType[simple] = list;
            }

            list.Add($"{e.HttpMethod} {e.Route}");
        }

        var entries = new List<ContractEntry>();
        foreach (var (name, schema) in schemas.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var json = Canonical.Serialize(schema);
            File.WriteAllText(Path.Combine(outputRoot, name + ".schema.json"), json);

            entries.Add(new ContractEntry
            {
                Dto = name,
                SchemaPath = $"contracts/{name}.schema.json",
                Sha256 = Canonical.Sha256Hex(json),
                ReturnedByEndpoints = endpointsByType.TryGetValue(name, out var eps)
                    ? eps.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList()
                    : new List<string>(),
            });
        }

        var manifest = new ContractsManifest
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
            Serializer = "System.Text.Json (ASP.NET Core web defaults, camelCase)",
            Dtos = entries,
        };

        File.WriteAllText(Path.Combine(outputRoot, "manifest.json"), Canonical.SerializeObject(manifest));
        return manifest;
    }

    private static string SimpleName(string fullName)
    {
        var idx = fullName.LastIndexOf('.');
        return idx >= 0 ? fullName[(idx + 1)..] : fullName;
    }
}

public sealed class ContractEntry
{
    public required string Dto { get; init; }

    public required string SchemaPath { get; init; }

    public required string Sha256 { get; init; }

    public required List<string> ReturnedByEndpoints { get; init; }
}

public sealed class ContractsManifest
{
    public required string GeneratedAtUtc { get; init; }

    public required string Serializer { get; init; }

    public required List<ContractEntry> Dtos { get; init; }
}
