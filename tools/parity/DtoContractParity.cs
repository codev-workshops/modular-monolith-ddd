using System.Reflection;

namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// Hashes the public contract surface each module exposes: its integration
/// events (the wire contract other services consume) and its API DTOs (the
/// shapes returned by query endpoints). The hash captures type name plus the
/// name and type of every public property, so any breaking change to a
/// contract shifts the module's hash.
/// </summary>
internal static class DtoContractParity
{
    public static List<ParityEntry> Capture()
    {
        var entries = new List<ParityEntry>();

        foreach (var module in Modules.All)
        {
            if (module.ApplicationAssembly is null || module.IntegrationEventsAssembly is null)
            {
                continue;
            }

            var contractTypes = new List<Type>();

            var events = LoadAssembly(module.IntegrationEventsAssembly);
            contractTypes.AddRange(PublicTypes(events)
                .Where(t => t.Name.EndsWith("IntegrationEvent", StringComparison.Ordinal)));

            var application = LoadAssembly(module.ApplicationAssembly);
            contractTypes.AddRange(PublicTypes(application)
                .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal)));

            var shapes = contractTypes
                .Select(DescribeType)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Cast<object>()
                .ToList();

            var model = new
            {
                module = module.Name,
                contracts = shapes,
            };

            entries.Add(new ParityEntry
            {
                Key = module.Name,
                Module = module.Name,
                Hash = Canonical.HashObject(model),
                Details = model,
            });
        }

        return entries;
    }

    private static TypeShape DescribeType(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new { name = p.Name, type = FormatType(p.PropertyType) })
            .OrderBy(p => p.name, StringComparer.Ordinal)
            .Cast<object>()
            .ToList();

        return new TypeShape
        {
            Name = type.FullName ?? type.Name,
            BaseType = type.BaseType?.FullName,
            Properties = properties,
        };
    }

    private static IEnumerable<Type> PublicTypes(Assembly assembly) =>
        assembly.GetTypes().Where(t => t.IsPublic && !t.IsInterface);

    private static string FormatType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return FormatType(underlying) + "?";
        }

        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }

            var args = type.GetGenericArguments().Select(FormatType);
            return $"{name}<{string.Join(",", args)}>";
        }

        return type.FullName ?? type.Name;
    }

    private static Assembly LoadAssembly(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(path))
        {
            return Assembly.LoadFrom(path);
        }

        return Assembly.Load(new AssemblyName(assemblyName));
    }

    private sealed class TypeShape
    {
        public string Name { get; set; } = string.Empty;

        public string? BaseType { get; set; }

        public List<object> Properties { get; set; } = new();
    }
}
