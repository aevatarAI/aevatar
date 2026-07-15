using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Discovers the caller's NyxID connected services at request time and registers any
/// operation that is explicitly marked <c>x-aevatar-tool</c> as an individual
/// <see cref="IAgentTool"/>. NyxID stays the single source of truth: services and specs are
/// read live from NyxID's proxy/spec surfaces on every discovery, nothing is cached as a
/// process-local catalog, and execution always goes back through the NyxID proxy.
/// </summary>
/// <remarks>
/// The per-request NyxID access token is read from <see cref="AgentToolRequestContext"/>,
/// which the tool-set boundary populates before discovery. Without a configured NyxID base
/// URL or an access token, no dynamic tools are exposed.
/// </remarks>
public sealed class NyxIdConnectedServiceToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdConnectedServiceToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        ILogger<NyxIdConnectedServiceToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdConnectedServiceToolSource>.Instance;
    }

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return [];

        var userToken = AgentToolRequestContext.AccessToken;
        if (string.IsNullOrWhiteSpace(userToken))
        {
            _logger.LogDebug("NyxID connected-service tools skipped: no access token in request context");
            return [];
        }

        var orgToken = AgentToolRequestContext.OrganizationToken;

        try
        {
            var services = await DiscoverServicesAsync(userToken, orgToken, ct);
            if (services.Count == 0)
                return [];

            var specs = await Task.WhenAll(services.Select(service =>
                FetchOperationsAsync(service, userToken, orgToken, ct)));

            return MaterializeTools(services, specs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service tool discovery failed");
            return [];
        }
    }

    private IReadOnlyList<IAgentTool> MaterializeTools(
        IReadOnlyList<ConnectedServiceRef> services,
        IReadOnlyList<ConnectedServiceToolOperation>[] specs)
    {
        var tools = new List<IAgentTool>();
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            var operations = specs[i]
                .OrderBy(op => op.PathTemplate, StringComparer.Ordinal)
                .ThenBy(op => op.Method, StringComparer.Ordinal);

            foreach (var operation in operations)
            {
                var name = ConnectedServiceToolNaming.Build(
                    service.Slug,
                    operation.Marker?.Name ?? operation.OperationId);
                var identity = $"{service.Slug}:{operation.Method} {operation.PathTemplate}";

                if (byName.TryGetValue(name, out var existing))
                {
                    // Observable failure: two operations collapse to the same tool name. Keep the
                    // first (deterministic order) and drop the rest so the LLM never sees an
                    // ambiguous duplicate.
                    _logger.LogWarning(
                        "NyxID connected-service tool name conflict on '{Name}': keeping {Existing}, dropping {Dropped}",
                        name, existing, identity);
                    continue;
                }

                byName[name] = identity;
                tools.Add(new ConnectedServiceProxyTool(
                    _client,
                    name,
                    service.Slug,
                    operation,
                    service.PreferOrgToken,
                    _logger));
            }
        }

        _logger.LogInformation(
            "NyxID connected-service tools registered ({Count} tools across {Services} services)",
            tools.Count, services.Count);

        return tools;
    }

    private async Task<IReadOnlyList<ConnectedServiceToolOperation>> FetchOperationsAsync(
        ConnectedServiceRef service,
        string userToken,
        string? orgToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(service.ServiceId))
        {
            _logger.LogDebug("NyxID service '{Slug}' has no service id; cannot fetch proxy-aware spec", service.Slug);
            return [];
        }

        var token = service.PreferOrgToken && !string.IsNullOrWhiteSpace(orgToken) ? orgToken! : userToken;
        var specJson = await _client.GetProxyServiceOpenApiAsync(token, service.ServiceId, ct);
        if (LooksLikeErrorEnvelope(specJson))
        {
            _logger.LogDebug("NyxID spec fetch for '{Slug}' returned an error envelope", service.Slug);
            return [];
        }

        return OpenApiToolSpecParser.Parse(specJson).AdmittedOperations().ToArray();
    }

    private async Task<IReadOnlyList<ConnectedServiceRef>> DiscoverServicesAsync(
        string userToken,
        string? orgToken,
        CancellationToken ct)
    {
        var merged = new List<ConnectedServiceRef>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in ParseServices(await _client.DiscoverProxyServicesAsync(userToken, ct), preferOrgToken: false))
        {
            if (seenSlugs.Add(service.Slug))
                merged.Add(service);
        }

        if (!string.IsNullOrWhiteSpace(orgToken) && orgToken != userToken)
        {
            foreach (var service in ParseServices(await _client.DiscoverProxyServicesAsync(orgToken, ct), preferOrgToken: true))
            {
                if (seenSlugs.Add(service.Slug))
                    merged.Add(service);
            }
        }

        return merged;
    }

    internal static IReadOnlyList<ConnectedServiceRef> ParseServices(string? json, bool preferOrgToken)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var results = new List<ConnectedServiceRef>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in EnumerateServiceItems(doc.RootElement))
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var slug = ReadString(item, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                var serviceId = ReadString(item, "id") ?? ReadString(item, "service_id");
                results.Add(new ConnectedServiceRef(slug!, serviceId, preferOrgToken));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return results;
    }

    private static IEnumerable<JsonElement> EnumerateServiceItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "custom_services", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in items.EnumerateArray())
                yield return item;
        }
    }

    private static string? ReadString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool LooksLikeErrorEnvelope(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            return doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal sealed record ConnectedServiceRef(string Slug, string? ServiceId, bool PreferOrgToken);
}
