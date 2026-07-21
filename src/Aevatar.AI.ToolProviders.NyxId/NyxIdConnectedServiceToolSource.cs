using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Foundation.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Discovers the caller's NyxID connected services at request time and registers any
/// operation that is explicitly marked <c>x-aevatar-tool</c> as an individual
/// <see cref="IAgentTool"/>. NyxID stays the single source of truth: connected instances come
/// from /keys, definitions come from /catalog, and specs are read live from the proxy-aware
/// OpenAPI surface. Nothing is cached as a process-local catalog, and execution always goes
/// back through the NyxID proxy.
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

        var userToken = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(userToken))
        {
            _logger.LogDebug("NyxID connected-service tools skipped: no access token in request context");
            return [];
        }

        var orgToken = AgentToolRequestContext.NyxIdOrgToken;

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
                    BuildPresentation(service, operation, name),
                    _logger));
            }
        }

        _logger.LogInformation(
            "NyxID connected-service tools registered ({Count} tools across {Services} services)",
            tools.Count, services.Count);

        return tools;
    }

    private static ToolPresentationDescriptor BuildPresentation(
        ConnectedServiceRef service,
        ConnectedServiceToolOperation operation,
        string invocationName)
    {
        var operationDisplayName = FirstNonEmpty(
            operation.Summary,
            operation.Marker?.Description,
            operation.Marker?.Name,
            operation.OperationId);
        var connectorDisplayName = FirstNonEmpty(
            service.ConnectorDisplayName,
            service.ConnectionLabel,
            service.CatalogServiceSlug,
            service.Slug);
        var displayName = string.IsNullOrWhiteSpace(operationDisplayName)
            ? connectorDisplayName
            : $"{connectorDisplayName} - {operationDisplayName}";
        var description = FirstNonEmpty(service.Description, operation.Marker?.Description, operation.Summary);

        return new ToolPresentationDescriptor
        {
            InvocationName = invocationName,
            DisplayName = displayName,
            Description = description,
            Kind = ToolPresentationKind.NyxIdOperation,
            Availability = ToolAvailability.Available,
            IconUrl = service.IconUrl,
            NyxIdOperation = new NyxIdOperationRef
            {
                ConnectedServiceId = service.ServiceId,
                ServiceSlug = service.Slug,
                CatalogServiceSlug = service.CatalogServiceSlug,
                ConnectionLabel = service.ConnectionLabel,
                ConnectorDisplayName = connectorDisplayName,
                OperationId = operation.OperationId,
                HttpMethod = operation.Method,
                PathTemplate = operation.PathTemplate,
            },
        };
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
        var seenServiceIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var service in await DiscoverServicesForTokenAsync(userToken, preferOrgToken: false, ct))
            if (seenServiceIds.Add(service.ServiceId))
                merged.Add(service);

        if (!string.IsNullOrWhiteSpace(orgToken) && orgToken != userToken)
        {
            foreach (var service in await DiscoverServicesForTokenAsync(orgToken, preferOrgToken: true, ct))
                if (seenServiceIds.Add(service.ServiceId))
                    merged.Add(service);
        }

        return merged;
    }

    private async Task<IReadOnlyList<ConnectedServiceRef>> DiscoverServicesForTokenAsync(
        string token,
        bool preferOrgToken,
        CancellationToken ct)
    {
        var keysTask = _client.ListServicesAsync(token, ct);
        var catalogTask = _client.ListCatalogAsync(token, ct);
        await Task.WhenAll(keysTask, catalogTask).ConfigureAwait(false);

        var keys = ParseJson<NyxIdConnectedServiceListDto>(await keysTask.ConfigureAwait(false))?.Keys ?? [];
        var catalogEntries = ParseJson<NyxIdCatalogListDto>(await catalogTask.ConfigureAwait(false))?.Entries ?? [];
        var catalogBySlug = catalogEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Slug))
            .GroupBy(static entry => entry.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var results = new List<ConnectedServiceRef>();
        foreach (var key in keys)
        {
            if (!IsExecutableConnectedService(key) ||
                string.IsNullOrWhiteSpace(key.Id) ||
                string.IsNullOrWhiteSpace(key.Slug))
            {
                continue;
            }

            NyxIdCatalogEntryDto? catalog = null;
            if (!string.IsNullOrWhiteSpace(key.CatalogServiceSlug))
                catalogBySlug.TryGetValue(key.CatalogServiceSlug, out catalog);

            results.Add(new ConnectedServiceRef(
                key.Slug.Trim(),
                key.Id.Trim(),
                key.CatalogServiceSlug?.Trim() ?? string.Empty,
                FirstNonEmpty(key.Label, key.Name, key.Slug),
                FirstNonEmpty(catalog?.Name, key.CatalogServiceName, key.Label, key.Name, key.Slug),
                FirstNonEmpty(catalog?.Description, key.Description),
                catalog?.IconUrl?.Trim() ?? string.Empty,
                preferOrgToken));
        }

        return results;
    }

    private static bool IsExecutableConnectedService(NyxIdConnectedServiceDto service)
    {
        if (!service.Connected || !service.IsActive ||
            service.Allowed == false || service.CredentialSource?.Allowed == false)
        {
            return false;
        }

        return string.Equals(service.Status, "active", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(service.Status, "ready", StringComparison.OrdinalIgnoreCase);
    }

    private static T? ParseJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();

        return string.Empty;
    }

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

    private sealed record ConnectedServiceRef(
        string Slug,
        string ServiceId,
        string CatalogServiceSlug,
        string ConnectionLabel,
        string ConnectorDisplayName,
        string Description,
        string IconUrl,
        bool PreferOrgToken);
}
