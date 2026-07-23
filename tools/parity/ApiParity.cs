using System.Text.Json;

namespace CompanyName.MyMeetings.Tools.Parity;

/// <summary>
/// Replays a fixed "golden dataset" of read requests against a running host and
/// hashes each normalized response (status code + body with volatile fields
/// redacted). The same request set run against the monolith and against an
/// extracted service must yield identical hashes for that module's endpoints.
/// </summary>
internal static class ApiParity
{
    public static async Task<List<ParityEntry>> CaptureAsync(string baseUrl, string configPath)
    {
        var config = LoadConfig(configPath);

        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var bearer = Environment.GetEnvironmentVariable("PARITY_API_BEARER");
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        var entries = new List<ParityEntry>();
        foreach (var request in config.Requests)
        {
            var method = new HttpMethod(request.Method ?? "GET");
            using var message = new HttpRequestMessage(method, request.Path.TrimStart('/'));
            using var response = await client.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();

            var normalizedBody = NormalizeBody(body, config.RedactFields);

            var model = new
            {
                request = new { request.Method, request.Path },
                status = (int)response.StatusCode,
                contentType = response.Content.Headers.ContentType?.MediaType,
                body = normalizedBody,
            };

            entries.Add(new ParityEntry
            {
                Key = $"{request.Method} {request.Path}",
                Module = request.Module,
                Hash = Canonical.HashObject(model),
                Details = model,
            });
        }

        return entries;
    }

    private static object? NormalizeBody(string body, IReadOnlyCollection<string> redactFields)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return Redact(doc.RootElement, redactFields);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static object? Redact(JsonElement element, IReadOnlyCollection<string> redactFields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    if (redactFields.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        map[prop.Name] = "<redacted>";
                        continue;
                    }

                    map[prop.Name] = Redact(prop.Value, redactFields);
                }

                return map;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(e => Redact(e, redactFields)).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return JsonSerializer.Deserialize<object>(element.GetRawText());
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static ApiConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"API parity config not found: {path}");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ApiConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return config ?? new ApiConfig();
    }

    private sealed class ApiConfig
    {
        public List<ApiRequest> Requests { get; set; } = new();

        public List<string> RedactFields { get; set; } = new();
    }

    private sealed class ApiRequest
    {
        public string Module { get; set; } = string.Empty;

        public string? Method { get; set; } = "GET";

        public string Path { get; set; } = string.Empty;
    }
}
