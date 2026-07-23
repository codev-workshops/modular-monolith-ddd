using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompanyName.MyMeetings.Parity.Contracts;

/// <summary>
/// Builds JSON Schemas from inspection-only DTO types. Property names are produced with the exact
/// System.Text.Json camelCase policy the API uses; nested DTOs are emitted as their own schema files
/// and referenced by relative <c>$ref</c>.
/// </summary>
public sealed class JsonSchemaGenerator
{
    private const string DtoNamespacePrefix = "CompanyName.MyMeetings.";

    private readonly MetadataLoadContext _mlc;
    private readonly Assembly _apiAssembly;
    private readonly Dictionary<string, Type> _dtoTypesByName = new(StringComparer.Ordinal);

    public JsonSchemaGenerator(MetadataLoadContext mlc, Assembly apiAssembly)
    {
        _mlc = mlc;
        _apiAssembly = apiAssembly;
    }

    public Dictionary<string, JsonObject> Generate(IEnumerable<string> rootTypeFullNames)
    {
        var pending = new Queue<Type>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var full in rootTypeFullNames)
        {
            var type = ResolveType(full);
            // Only DTO object types get schemas; primitive/Guid command results are skipped here.
            if (type is not null && IsDto(type) && seen.Add(type.Name))
            {
                pending.Enqueue(type);
            }
        }

        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            var nested = new List<Type>();
            schemas[type.Name] = BuildObjectSchema(type, nested);

            foreach (var n in nested)
            {
                if (seen.Add(n.Name))
                {
                    pending.Enqueue(n);
                }
            }
        }

        return schemas;
    }

    private JsonObject BuildObjectSchema(Type type, List<Type> nested)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var (schema, isNullable) = BuildPropertySchema(prop.PropertyType, nested);
            properties[jsonName] = schema;
            if (!isNullable)
            {
                required.Add(jsonName);
            }
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = type.Name,
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    private (JsonNode Schema, bool IsNullable) BuildPropertySchema(Type type, List<Type> nested)
    {
        var (underlying, nullableValueType) = UnwrapNullable(type);

        // Collections.
        var element = GetEnumerableElement(underlying);
        if (element is not null)
        {
            var (itemSchema, _) = BuildPropertySchema(element, nested);
            var arr = new JsonObject { ["type"] = "array", ["items"] = itemSchema };
            return (arr, true); // arrays are reference types -> nullable
        }

        // Nested DTOs -> own schema file, referenced.
        if (IsDto(underlying))
        {
            nested.Add(underlying);
            return (new JsonObject { ["$ref"] = underlying.Name + ".schema.json" }, true);
        }

        var (jsonType, format) = MapScalar(underlying);
        var node = new JsonObject();
        var isReference = !underlying.IsValueType;
        var isNullable = nullableValueType || isReference;

        if (isNullable)
        {
            node["type"] = new JsonArray { jsonType, "null" };
        }
        else
        {
            node["type"] = jsonType;
        }

        if (format is not null)
        {
            node["format"] = format;
        }

        if (underlying.IsEnum)
        {
            // System.Text.Json serializes enums numerically by default.
            node["x-enum"] = underlying.Name;
        }

        return (node, isNullable);
    }

    private static (string JsonType, string? Format) MapScalar(Type t)
    {
        if (t.IsEnum)
        {
            return ("integer", null);
        }

        return t.FullName switch
        {
            "System.String" => ("string", null),
            "System.Char" => ("string", null),
            "System.Guid" => ("string", "uuid"),
            "System.Boolean" => ("boolean", null),
            "System.DateTime" => ("string", "date-time"),
            "System.DateTimeOffset" => ("string", "date-time"),
            "System.DateOnly" => ("string", "date"),
            "System.TimeSpan" => ("string", "duration"),
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" => ("integer", null),
            "System.Decimal" or "System.Double" or "System.Single" => ("number", null),
            _ => ("string", null),
        };
    }

    private static (Type Underlying, bool WasNullableValueType) UnwrapNullable(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name == "Nullable`1")
        {
            return (type.GetGenericArguments()[0], true);
        }

        return (type, false);
    }

    private static Type? GetEnumerableElement(Type type)
    {
        if (type.FullName == "System.String")
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition().Name;
            if (def.StartsWith("List", StringComparison.Ordinal)
                || def.StartsWith("IList", StringComparison.Ordinal)
                || def.StartsWith("IEnumerable", StringComparison.Ordinal)
                || def.StartsWith("ICollection", StringComparison.Ordinal)
                || def.StartsWith("IReadOnlyList", StringComparison.Ordinal)
                || def.StartsWith("IReadOnlyCollection", StringComparison.Ordinal))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static bool IsDto(Type type) =>
        type.IsClass
        && type.FullName is not null
        && type.FullName.StartsWith(DtoNamespacePrefix, StringComparison.Ordinal)
        && type.FullName != "System.String";

    private Type? ResolveType(string fullName)
    {
        if (_dtoTypesByName.TryGetValue(fullName, out var cached))
        {
            return cached;
        }

        var type = _apiAssembly.GetType(fullName);
        if (type is null)
        {
            foreach (var asm in _mlc.GetAssemblies())
            {
                type = asm.GetType(fullName);
                if (type is not null)
                {
                    break;
                }
            }
        }

        if (type is not null)
        {
            _dtoTypesByName[fullName] = type;
        }

        return type;
    }
}
