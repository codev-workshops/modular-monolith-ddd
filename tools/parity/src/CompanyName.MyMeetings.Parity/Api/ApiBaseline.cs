using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.MyMeetings.Parity.Db;
using CompanyName.MyMeetings.Parity.Reflection;
using Microsoft.Data.SqlClient;

namespace CompanyName.MyMeetings.Parity.Api;

/// <summary>
/// Baseline 2 — API golden dataset. Authenticates (OAuth2 resource-owner password grant) as both a
/// Member and an Administrator against the DEBUG-built API (where <c>HasPermissionAuthorizationHandler</c>
/// enforces real permissions), then for every reflected endpoint records the authorization matrix
/// (role × endpoint → expected authorized) and, for GET endpoints, the canonicalized response body and
/// its SHA-256. Runtime GUIDs/timestamps in bodies are normalized to the same stable tokens the DB
/// baseline uses (via <see cref="FixtureSnapshot"/>), so bodies are reproducible across fixture reruns.
///
/// Command endpoints (POST/PUT/PATCH/DELETE) are intentionally NOT executed: doing so would mutate the
/// deterministic fixture after the DB baseline is captured and make repeated captures diverge. Their
/// authorization expectation is still frozen (from the role→permission mapping), so the full matrix is
/// captured; GET endpoints additionally verify that matrix against live 200/403 responses.
/// </summary>
public sealed class ApiBaseline
{
    private static readonly (string Role, string Login, string Password)[] Roles =
    {
        (FixtureConstants.MemberRole, FixtureConstants.MemberLogin, FixtureConstants.MemberPassword),
        (FixtureConstants.AdminRole, FixtureConstants.AdminLogin, FixtureConstants.AdminPassword),
    };

    private readonly ParityOptions _options;

    public ApiBaseline(ParityOptions options) => _options = options;

    public async Task<List<ApiGoldenEntry>> GenerateAsync(string outputRoot, IReadOnlyList<EndpointInfo> endpoints)
    {
        var bodiesDir = Path.Combine(outputRoot, "bodies");
        Directory.CreateDirectory(bodiesDir);

        var snapshot = FixtureSnapshot.Capture(_options.ConnectionString);
        var tokenizer = snapshot.Tokenizer;

        using var connection = new SqlConnection(_options.ConnectionString);
        connection.Open();
        var rolePermissions = LoadRolePermissions(connection);
        var routeValues = LoadRouteValues(connection);
        var priceListQuery = LoadPriceListQuery(connection);

        using var http = new HttpClient { BaseAddress = new Uri(_options.ApiBaseUrl) };
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, login, password) in Roles)
        {
            tokens[role] = await AcquireTokenAsync(http, login, password);
        }

        var entries = new List<ApiGoldenEntry>();
        foreach (var endpoint in endpoints.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            foreach (var (role, _, _) in Roles)
            {
                entries.Add(await CaptureAsync(
                    http, endpoint, role, tokens[role], rolePermissions[role],
                    routeValues, priceListQuery, tokenizer, bodiesDir));
            }
        }

        entries = entries
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Role, StringComparer.Ordinal)
            .ToList();

        var goldenJson = Canonical.SerializeObject(entries);
        File.WriteAllText(Path.Combine(outputRoot, "golden.json"), goldenJson);
        return entries;
    }

    private async Task<ApiGoldenEntry> CaptureAsync(
        HttpClient http,
        EndpointInfo endpoint,
        string role,
        string token,
        IReadOnlySet<string> rolePerms,
        IReadOnlyDictionary<string, Guid> routeValues,
        IReadOnlyList<(string Key, string Value)> priceListQuery,
        GuidTokenizer tokenizer,
        string bodiesDir)
    {
        var authorized = endpoint.Permission is null || rolePerms.Contains(endpoint.Permission);
        var responseType = ShortTypeName(endpoint.ResponseType, endpoint.ResponseIsCollection);

        // Only GET endpoints are executed; commands are recorded (matrix only) but not run.
        if (endpoint.HttpMethod != "GET")
        {
            return new ApiGoldenEntry
            {
                Id = endpoint.Id,
                Method = endpoint.HttpMethod,
                Route = endpoint.Route,
                Role = role,
                Permission = endpoint.Permission,
                Authorized = authorized,
                Executed = false,
                Status = null,
                ResponseType = responseType,
                RequestBody = null,
                BodySha256 = null,
                BodyPath = null,
                Note = "command-not-executed (would mutate the fixture); authorization frozen from role→permission mapping",
            };
        }

        if (!TryBuildUrl(endpoint, routeValues, priceListQuery, tokenizer, out var url, out var displayRoute))
        {
            return new ApiGoldenEntry
            {
                Id = endpoint.Id,
                Method = endpoint.HttpMethod,
                Route = endpoint.Route,
                Role = role,
                Permission = endpoint.Permission,
                Authorized = authorized,
                Executed = false,
                Status = null,
                ResponseType = responseType,
                RequestBody = null,
                BodySha256 = null,
                BodyPath = null,
                Note = "unresolved-route-parameters (no deterministic fixture value)",
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        var normalized = NormalizeBody(raw, tokenizer);
        var roleShort = role.ToLowerInvariant();
        var bodyFile = $"{endpoint.Id}__{roleShort}.json";

        // The emails endpoint returns the async email log (app.Emails), which the live API's background
        // outbox processor keeps mutating with non-deterministic timing (mirrors the transient
        // inbox/outbox tables excluded from the DB baseline). Capture its status + a reference copy, but
        // do not hash the body — otherwise the golden root would drift run-to-run.
        if (HasVolatileBody(endpoint))
        {
            var volatileDir = Path.Combine(Path.GetDirectoryName(bodiesDir)!, "volatile-bodies");
            Directory.CreateDirectory(volatileDir);
            File.WriteAllText(Path.Combine(volatileDir, bodyFile), normalized, new UTF8Encoding(false));

            return new ApiGoldenEntry
            {
                Id = endpoint.Id,
                Method = endpoint.HttpMethod,
                Route = displayRoute,
                Role = role,
                Permission = endpoint.Permission,
                Authorized = authorized,
                Executed = true,
                Status = (int)response.StatusCode,
                ResponseType = responseType,
                RequestBody = null,
                BodySha256 = null,
                BodyPath = $"api/volatile-bodies/{bodyFile}",
                Note = "volatile-body-not-hashed (async email log; timing-dependent)",
            };
        }

        File.WriteAllText(Path.Combine(bodiesDir, bodyFile), normalized, new UTF8Encoding(false));

        return new ApiGoldenEntry
        {
            Id = endpoint.Id,
            Method = endpoint.HttpMethod,
            Route = displayRoute,
            Role = role,
            Permission = endpoint.Permission,
            Authorized = authorized,
            Executed = true,
            Status = (int)response.StatusCode,
            ResponseType = responseType,
            RequestBody = null,
            BodySha256 = Canonical.Sha256Hex(normalized),
            BodyPath = $"api/bodies/{bodyFile}",
            Note = string.Empty,
        };
    }

    private static bool HasVolatileBody(EndpointInfo endpoint) =>
        endpoint.Route.EndsWith("/userAccess/emails", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> AcquireTokenAsync(HttpClient http, string login, string password)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = FixtureConstants.OAuthClientId,
            ["client_secret"] = FixtureConstants.OAuthClientSecret,
            ["scope"] = FixtureConstants.OAuthScope,
            ["username"] = login,
            ["password"] = password,
        });

        using var response = await http.PostAsync("/connect/token", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"token acquisition failed for {login}: {(int)response.StatusCode} {body}");
        }

        var token = JsonNode.Parse(body)?["access_token"]?.GetValue<string>();
        return token ?? throw new InvalidOperationException($"no access_token in response for {login}: {body}");
    }

    private bool TryBuildUrl(
        EndpointInfo endpoint,
        IReadOnlyDictionary<string, Guid> routeValues,
        IReadOnlyList<(string Key, string Value)> priceListQuery,
        GuidTokenizer tokenizer,
        out string url,
        out string displayRoute)
    {
        var actual = endpoint.Route;
        var display = endpoint.Route;
        foreach (var param in endpoint.RouteParameters)
        {
            if (!routeValues.TryGetValue(param, out var value))
            {
                url = string.Empty;
                displayRoute = string.Empty;
                return false;
            }

            actual = actual.Replace("{" + param + "}", value.ToString(), StringComparison.Ordinal);
            display = display.Replace(
                "{" + param + "}",
                tokenizer.TryGet(value, out var token) ? token : value.ToString(),
                StringComparison.Ordinal);
        }

        // GET api/payments/priceListItems takes a [FromQuery] filter; fill it from a fixture row so the
        // response is non-empty and deterministic. Other query params (page/perPage) are optional.
        if (endpoint.Route.EndsWith("priceListItems", StringComparison.OrdinalIgnoreCase) && priceListQuery.Count > 0)
        {
            var query = string.Join('&', priceListQuery.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            actual += "?" + query;
            display += "?" + query;
        }

        url = actual;
        displayRoute = display;
        return true;
    }

    private static string NormalizeBody(string raw, GuidTokenizer tokenizer)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            // Non-JSON payload (e.g. a plain-text error) — normalize the text verbatim.
            return VolatileText.Normalize(raw, tokenizer);
        }

        // Scrub the per-request ProblemDetails traceId BEFORE tokenizing (its 32-hex trace-id would
        // otherwise be captured as a bogus GUID token and its 16-hex span-id would vary every run).
        ScrubVolatileFields(parsed);

        var withTokens = VolatileText.Normalize(parsed?.ToJsonString() ?? string.Empty, tokenizer);
        try
        {
            // Sort array elements by canonical content: the read-model queries backing these endpoints
            // do not guarantee ordering, so element order varies run-to-run. Normalizing to a canonical
            // set captures the content invariant deterministically.
            return Canonical.Serialize(SortArrays(JsonNode.Parse(withTokens)));
        }
        catch (JsonException)
        {
            return withTokens;
        }
    }

    private static JsonNode? SortArrays(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var newObj = new JsonObject();
                foreach (var kvp in obj)
                {
                    newObj[kvp.Key] = SortArrays(kvp.Value?.DeepClone());
                }

                return newObj;
            case JsonArray arr:
                var items = arr
                    .Select(e => SortArrays(e?.DeepClone()))
                    .OrderBy(Canonical.Serialize, StringComparer.Ordinal)
                    .ToList();
                var newArr = new JsonArray();
                foreach (var item in items)
                {
                    newArr.Add(item);
                }

                return newArr;
            default:
                return node?.DeepClone();
        }
    }

    private static void ScrubVolatileFields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    if (string.Equals(key, "traceId", StringComparison.OrdinalIgnoreCase))
                    {
                        obj[key] = "#TRACE#";
                    }
                    else
                    {
                        ScrubVolatileFields(obj[key]);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    ScrubVolatileFields(item);
                }

                break;
        }
    }

    private static string? ShortTypeName(string? fullName, bool isCollection)
    {
        if (fullName is null)
        {
            return null;
        }

        var simple = fullName.Contains('.') ? fullName[(fullName.LastIndexOf('.') + 1)..] : fullName;
        return isCollection ? simple + "[]" : simple;
    }

    private static Dictionary<string, HashSet<string>> LoadRolePermissions(SqlConnection connection)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var cmd = new SqlCommand("SELECT RoleCode, PermissionCode FROM users.RolesToPermissions", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var role = reader.GetString(0);
            var permission = reader.GetString(1);
            if (!result.TryGetValue(role, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[role] = set;
            }

            set.Add(permission);
        }

        foreach (var (role, _, _) in Roles)
        {
            result.TryAdd(role, new HashSet<string>(StringComparer.Ordinal));
        }

        return result;
    }

    private static Dictionary<string, Guid> LoadRouteValues(SqlConnection connection)
    {
        var values = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        AddScalarGuid(connection, values, "meetingId", "SELECT TOP 1 Id FROM meetings.Meetings ORDER BY Id");
        AddScalarGuid(connection, values, "meetingGroupId", "SELECT TOP 1 Id FROM meetings.MeetingGroups ORDER BY Id");
        AddScalarGuid(connection, values, "meetingGroupProposalId", "SELECT TOP 1 Id FROM meetings.MeetingGroupProposals ORDER BY Id");
        AddScalarGuid(connection, values, "priceListItemId", "SELECT TOP 1 Id FROM payments.PriceListItems ORDER BY Id");
        return values;
    }

    private static void AddScalarGuid(SqlConnection connection, Dictionary<string, Guid> values, string key, string sql)
    {
        using var cmd = new SqlCommand(sql, connection);
        var result = cmd.ExecuteScalar();
        if (result is Guid guid)
        {
            values[key] = guid;
        }
    }

    private static List<(string Key, string Value)> LoadPriceListQuery(SqlConnection connection)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP 1 CountryCode, CategoryCode, SubscriptionPeriodCode FROM payments.PriceListItems " +
            "ORDER BY CountryCode, CategoryCode, SubscriptionPeriodCode",
            connection);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new List<(string, string)>();
        }

        return new List<(string, string)>
        {
            ("countryCode", reader.GetString(0)),
            ("categoryCode", reader.GetString(1)),
            ("periodTypeCode", reader.GetString(2)),
        };
    }
}
