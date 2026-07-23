using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompanyName.MyMeetings.Parity;

/// <summary>
/// Canonical JSON serialization (recursively sorted object keys, no insignificant whitespace)
/// and SHA-256 hashing helpers. Every artifact is hashed over its canonical byte representation
/// so that identical logical content always yields an identical hash regardless of source ordering.
/// </summary>
public static class Canonical
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // Keep output stable/portable; we escape nothing beyond JSON requirements.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize a JsonNode to canonical (sorted-key, minified) JSON text.</summary>
    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteCanonical(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Serialize an arbitrary object graph (via System.Text.Json) into canonical JSON.</summary>
    public static string SerializeObject(object? value)
    {
        var node = value is null ? null : JsonSerializer.SerializeToNode(value);
        return Serialize(node);
    }

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>SHA-256 over the concatenation (newline separated) of an ordered list of child hashes.</summary>
    public static string RollupSha256(IEnumerable<string> childHashesInStableOrder)
    {
        var ordered = childHashesInStableOrder.OrderBy(h => h, StringComparer.Ordinal);
        return Sha256Hex(string.Join('\n', ordered));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteCanonical(writer, kvp.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray arr:
                writer.WriteStartArray();
                foreach (var item in arr)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                value.WriteTo(writer);
                break;
        }
    }
}
