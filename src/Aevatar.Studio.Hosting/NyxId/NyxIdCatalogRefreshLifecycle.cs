using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdCatalogRefreshLifecycle(
    IHttpClientFactory httpClientFactory,
    INyxIdCatalogSnapshotCommandPort commandPort,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<NyxIdCatalogRefreshLifecycle> logger) : INyxIdCatalogRefreshLifecycle
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    public async Task RefreshPersonalAsync(string verifiedOwnerSubject, string bearerToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var authority = NyxIdAuthorityResolver.ResolveNyxIdAuthorityBase(configuration);
        if (string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException("NyxID authority is not configured.");
        var owner = new NyxIdCatalogOwnerIdentity
        {
            Authority = authority,
            OwnerKind = NyxIdCatalogOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        };
        var observedAt = timeProvider.GetUtcNow();
        try
        {
            using var servicesDocument = await GetAsync(owner.Authority, "/api/v1/user-services", bearerToken, ct);
            using var nodesDocument = await GetAsync(owner.Authority, "/api/v1/nodes", bearerToken, ct);
            var services = ParseServices(servicesDocument.RootElement, owner);
            var nodeBindings = await ReadNodeBindingsAsync(owner.Authority, bearerToken, nodesDocument.RootElement, ct);
            var grants = MapGrants(services, nodeBindings);
            await commandPort.ObserveAsync(new NyxIdCatalogObservation(
                owner.Clone(), observedAt, observedAt + Freshness, string.Empty, ComputeDigest(grants), grants), ct);
        }
        catch (NyxIdCatalogAccessException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await commandPort.InvalidateAsync(owner, observedAt, "nyxid_catalog_access_denied", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NyxID catalog refresh failed for owner kind {OwnerKind}.", owner.OwnerKind);
            await commandPort.RecordRefreshFailureAsync(owner, observedAt, "nyxid_catalog_refresh_failed", ct);
        }
    }

    private async Task<JsonDocument> GetAsync(string authority, string path, string bearerToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{authority.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new NyxIdCatalogAccessException(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<IReadOnlyList<NodeBinding>> ReadNodeBindingsAsync(
        string authority, string bearerToken, JsonElement root, CancellationToken ct)
    {
        var bindings = new List<NodeBinding>();
        foreach (var node in RequiredArray(root, "nodes").EnumerateArray())
        {
            var nodeId = RequiredString(node, "id");
            using var document = await GetAsync(
                authority, $"/api/v1/nodes/{Uri.EscapeDataString(nodeId)}/bindings", bearerToken, ct);
            foreach (var binding in RequiredArray(document.RootElement, "bindings").EnumerateArray())
            {
                if (binding.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                    continue;
                bindings.Add(new NodeBinding(
                    RequiredString(binding, "service_id"), nodeId,
                    binding.TryGetProperty("priority", out var priority) ? priority.GetInt32() : 0));
            }
        }
        return bindings;
    }

    private static IReadOnlyList<ServiceFact> ParseServices(JsonElement root, NyxIdCatalogOwnerIdentity owner)
    {
        var services = new List<ServiceFact>();
        foreach (var service in RequiredArray(root, "services").EnumerateArray())
        {
            if (service.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                continue;
            var source = service.GetProperty("credential_source");
            var expectedType = owner.OwnerKind == NyxIdCatalogOwnerKind.Organization ? "org" : "personal";
            if (!string.Equals(RequiredString(source, "type"), expectedType, StringComparison.Ordinal))
                continue;
            if (owner.OwnerKind == NyxIdCatalogOwnerKind.Organization &&
                !string.Equals(RequiredString(source, "org_id"), owner.OwnerSubject, StringComparison.Ordinal))
                continue;
            services.Add(new ServiceFact(
                RequiredString(service, "id"), RequiredString(service, "slug"),
                OptionalString(service, "label") ?? OptionalString(service, "catalog_service_name") ?? RequiredString(service, "slug"),
                OptionalString(service, "catalog_service_id"), OptionalString(service, "node_id")));
        }
        return services;
    }

    private static IReadOnlyList<NyxIdServiceGrant> MapGrants(
        IReadOnlyList<ServiceFact> services, IReadOnlyList<NodeBinding> bindings) =>
        services.Select(service =>
        {
            var matchingBindings = bindings
                .Where(binding => string.Equals(binding.CatalogServiceId, service.CatalogServiceId, StringComparison.Ordinal))
                .OrderBy(binding => binding.Priority)
                .ToArray();
            var grant = new NyxIdServiceGrant
            {
                UserServiceId = service.Id,
                ServiceSlug = service.Slug,
                DisplayName = service.DisplayName,
                NodeGrantsNotRequired = string.IsNullOrWhiteSpace(service.PrimaryNodeId) && matchingBindings.Length == 0,
            };
            var nodeIds = matchingBindings.Select(binding => binding.NodeId)
                .Prepend(service.PrimaryNodeId ?? string.Empty)
                .Where(static nodeId => !string.IsNullOrWhiteSpace(nodeId))
                .Distinct(StringComparer.Ordinal);
            grant.NodeGrants.Add(nodeIds.Select(nodeId => new NyxIdNodeGrant
            {
                NodeId = nodeId,
                Primary = string.Equals(nodeId, service.PrimaryNodeId, StringComparison.Ordinal),
            }));
            return grant;
        }).OrderBy(static grant => grant.UserServiceId, StringComparer.Ordinal).ToArray();

    private static string ComputeDigest(IReadOnlyList<NyxIdServiceGrant> grants)
    {
        var canonical = string.Join('\n', grants.Select(grant =>
            $"{grant.UserServiceId}|{grant.ServiceSlug}|{grant.NodeGrantsNotRequired}|{string.Join(',', grant.NodeGrants.Select(node => $"{node.NodeId}:{node.Primary}"))}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonElement RequiredArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw new InvalidOperationException($"NyxID catalog response is missing array '{name}'.");
    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidOperationException($"NyxID catalog response is missing '{name}'.");
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim() : null;

    private sealed record ServiceFact(string Id, string Slug, string DisplayName, string? CatalogServiceId, string? PrimaryNodeId);
    private sealed record NodeBinding(string CatalogServiceId, string NodeId, int Priority);
    private sealed class NyxIdCatalogAccessException(HttpStatusCode statusCode) : Exception
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
