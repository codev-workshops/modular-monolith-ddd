using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// Deterministic serialization + hashing helpers. All parity hashes are SHA-256
/// over canonical JSON so that captured baselines are stable and comparable
/// regardless of machine, culture or run order.
/// </summary>
internal static class Canonical
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serialize a value to indented JSON (used for human-readable manifests).</summary>
    public static string ToJson(object value) => JsonSerializer.Serialize(value, WriteOptions);

    /// <summary>
    /// Produce a canonical string form of an arbitrary JSON-like tree with object
    /// keys sorted lexicographically, then SHA-256 hash it. Used for the API
    /// dimension where response shape/order must not affect the hash.
    /// </summary>
    public static string HashJsonText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return Sha256(sb.ToString());
    }

    /// <summary>Hash any object by first rendering it to canonical (key-sorted) JSON.</summary>
    public static string HashObject(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return HashJsonText(json);
    }

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(JsonSerializer.Serialize(props[i].Name));
                    sb.Append(':');
                    WriteCanonical(props[i].Value, sb);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteCanonical(item, sb);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                sb.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;
            default:
                sb.Append("null");
                break;
        }
    }
}
