using System.Reflection;

namespace CompanyName.MyMeetings.Parity.Reflection;

public sealed record EndpointInfo
{
    public required string Controller { get; init; }

    public required string Action { get; init; }

    public required string HttpMethod { get; init; }

    public required string Route { get; init; }

    public required string? Permission { get; init; }

    /// <summary>Full name of the response DTO type (element type if the response is a collection).</summary>
    public required string? ResponseType { get; init; }

    public required bool ResponseIsCollection { get; init; }

    public required IReadOnlyList<string> RouteParameters { get; init; }

    public string Id => $"{HttpMethod}:{Route}".Replace('/', '_').Replace('{', '_').Replace('}', '_');
}

/// <summary>
/// Reflects (inspection-only, via <see cref="MetadataLoadContext"/>) over the built API assembly to
/// enumerate controller endpoints: HTTP method, composed route, required permission, and response DTO
/// type. Drives both the API golden dataset (baseline 2) and the DTO contract set (baseline 3).
/// </summary>
public sealed class EndpointCatalog : IDisposable
{
    private readonly MetadataLoadContext _mlc;

    public EndpointCatalog(string apiBinDir)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        // App bin first (so our copies win), then the .NET runtime and the ASP.NET Core shared
        // framework (Microsoft.AspNetCore.Mvc.Core etc. are not copied into a framework-dependent bin).
        var searchDirs = new List<string> { apiBinDir, runtimeDir };
        searchDirs.AddRange(FindAspNetCoreSharedDirs(runtimeDir));

        var paths = searchDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .GroupBy(Path.GetFileName)
            .Select(g => g.First())
            .ToList();

        _mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        ApiAssembly = _mlc.LoadFromAssemblyPath(Path.Combine(apiBinDir, "CompanyName.MyMeetings.API.dll"));

        // Eagerly load the application assemblies so DTO types (declared in module Application
        // assemblies) resolve by full name during schema generation.
        foreach (var dll in Directory.EnumerateFiles(apiBinDir, "CompanyName.MyMeetings.*.dll"))
        {
            try
            {
                _mlc.LoadFromAssemblyPath(dll);
            }
            catch (FileLoadException)
            {
                // Already loaded.
            }
        }
    }

    public Assembly ApiAssembly { get; }

    public MetadataLoadContext LoadContext => _mlc;

    public List<EndpointInfo> Enumerate()
    {
        var endpoints = new List<EndpointInfo>();

        foreach (var controller in ApiAssembly.GetTypes()
                     .Where(IsController)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var basePath = GetRouteTemplate(controller.GetCustomAttributesData()) ?? string.Empty;

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var attrs = method.GetCustomAttributesData();
                var httpAttr = attrs.FirstOrDefault(a => HttpMethodMap.ContainsKey(AttrName(a)));
                if (httpAttr is null)
                {
                    continue;
                }

                var httpMethod = HttpMethodMap[AttrName(httpAttr)];
                var actionTemplate = FirstStringArg(httpAttr);
                var controllerToken = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller.Name[..^"Controller".Length]
                    : controller.Name;
                var route = CombineRoute(
                    ReplaceTokens(basePath, controllerToken, method.Name) ?? string.Empty,
                    ReplaceTokens(actionTemplate, controllerToken, method.Name));
                var (responseType, isCollection) = GetResponseType(attrs);

                endpoints.Add(new EndpointInfo
                {
                    Controller = controller.Name,
                    Action = method.Name,
                    HttpMethod = httpMethod,
                    Route = route,
                    Permission = GetPermission(attrs),
                    ResponseType = responseType,
                    ResponseIsCollection = isCollection,
                    RouteParameters = ExtractRouteParams(route),
                });
            }
        }

        return endpoints
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.HttpMethod, StringComparer.Ordinal)
            .ToList();
    }

    public void Dispose() => _mlc.Dispose();

    private static IEnumerable<string> FindAspNetCoreSharedDirs(string netCoreRuntimeDir)
    {
        // netCoreRuntimeDir = <root>/shared/Microsoft.NETCore.App/<version>
        var versionDir = new DirectoryInfo(netCoreRuntimeDir);
        var netCoreAppDir = versionDir.Parent;
        var sharedDir = netCoreAppDir?.Parent;
        if (sharedDir is null)
        {
            yield break;
        }

        var aspNetRoot = Path.Combine(sharedDir.FullName, "Microsoft.AspNetCore.App");
        if (!Directory.Exists(aspNetRoot))
        {
            yield break;
        }

        // Prefer the version matching the NETCore.App version, else the highest available.
        var wanted = Path.Combine(aspNetRoot, versionDir.Name);
        if (Directory.Exists(wanted))
        {
            yield return wanted;
            yield break;
        }

        var best = Directory.EnumerateDirectories(aspNetRoot)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .LastOrDefault();
        if (best is not null)
        {
            yield return best;
        }
    }

    private static readonly Dictionary<string, string> HttpMethodMap = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpPatchAttribute"] = "PATCH",
        ["HttpDeleteAttribute"] = "DELETE",
    };

    private static bool IsController(Type t)
    {
        if (t.IsAbstract || !t.IsClass)
        {
            return false;
        }

        for (var b = t.BaseType; b is not null; b = b.BaseType)
        {
            if (b.Name is "ControllerBase" or "Controller")
            {
                return true;
            }
        }

        return false;
    }

    private static string AttrName(CustomAttributeData a) => a.AttributeType.Name;

    private static string? GetRouteTemplate(IEnumerable<CustomAttributeData> attrs)
    {
        var route = attrs.FirstOrDefault(a => a.AttributeType.Name == "RouteAttribute");
        return route is null ? null : FirstStringArg(route);
    }

    private static string? FirstStringArg(CustomAttributeData attr)
    {
        foreach (var arg in attr.ConstructorArguments)
        {
            if (arg.ArgumentType.FullName == "System.String")
            {
                return (string?)arg.Value;
            }
        }

        return null;
    }

    private static string? GetPermission(IEnumerable<CustomAttributeData> attrs)
    {
        var perm = attrs.FirstOrDefault(a => a.AttributeType.Name == "HasPermissionAttribute");
        return perm is null ? null : FirstStringArg(perm);
    }

    private static (string? Type, bool IsCollection) GetResponseType(IEnumerable<CustomAttributeData> attrs)
    {
        foreach (var attr in attrs.Where(a => a.AttributeType.Name == "ProducesResponseTypeAttribute"))
        {
            foreach (var arg in attr.ConstructorArguments)
            {
                if (arg.ArgumentType.FullName == "System.Type" && arg.Value is Type type)
                {
                    return UnwrapCollection(type);
                }
            }
        }

        return (null, false);
    }

    private static (string? Type, bool IsCollection) UnwrapCollection(Type type)
    {
        if (type.IsArray)
        {
            return (type.GetElementType()?.FullName, true);
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition().Name;
            if (def.StartsWith("List", StringComparison.Ordinal)
                || def.StartsWith("IEnumerable", StringComparison.Ordinal)
                || def.StartsWith("ICollection", StringComparison.Ordinal)
                || def.StartsWith("IReadOnlyList", StringComparison.Ordinal))
            {
                return (type.GetGenericArguments()[0].FullName, true);
            }
        }

        return (type.FullName, false);
    }

    private static string? ReplaceTokens(string? template, string controllerToken, string actionName)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        return template
            .Replace("[controller]", controllerToken, StringComparison.Ordinal)
            .Replace("[action]", actionName, StringComparison.Ordinal);
    }

    private static string CombineRoute(string basePath, string? actionTemplate)
    {
        var b = (basePath ?? string.Empty).Trim('/');
        var a = (actionTemplate ?? string.Empty).Trim('/');
        var combined = string.IsNullOrEmpty(a) ? b : $"{b}/{a}";
        return "/" + combined.Trim('/');
    }

    private static List<string> ExtractRouteParams(string route)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i < route.Length; i++)
        {
            if (route[i] == '{')
            {
                start = i + 1;
            }
            else if (route[i] == '}' && start >= 0)
            {
                var token = route[start..i];
                var colon = token.IndexOf(':');
                result.Add(colon >= 0 ? token[..colon] : token);
                start = -1;
            }
        }

        return result;
    }
}
