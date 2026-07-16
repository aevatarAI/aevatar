using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdAuthorizationCatalogQueryPort _queryPort;
    private readonly NyxIdAuthorizationCatalogRefreshOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPort(
        IHttpClientFactory httpClientFactory,
        INyxIdAuthorizationCatalogCommandPort commandPort,
        INyxIdAuthorizationCatalogQueryPort queryPort,
        IOptions<NyxIdAuthorizationCatalogRefreshOptions> options,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        return RefreshAsync(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        }, bearerToken, ct);
    }

    public async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var endpointBaseUrl = Normalize(_options.EndpointBaseUrl) ??
                              throw new InvalidOperationException("NyxID authorization catalog endpoint is not configured.");
        if (!string.Equals(
                Normalize(owner.Authority),
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NyxID authorization catalog owner authority is not supported.");
        }
        if (owner.OwnerKind != AuthorizationOwnerKind.Personal)
        {
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported,
                "nyxid_catalog_organization_owner_not_supported");
        }

        var refreshStartedAt = _timeProvider.GetUtcNow();
        try
        {
            var first = await ReadCatalogAsync(endpointBaseUrl, owner, bearerToken, ct);
            var second = await ReadCatalogAsync(endpointBaseUrl, owner, bearerToken, ct);
            if (!string.Equals(first.ContentDigest, second.ContentDigest, StringComparison.Ordinal) ||
                !first.RequestedNodeIds.SequenceEqual(second.RequestedNodeIds, StringComparer.Ordinal))
            {
                throw new NyxIdAuthorizationCatalogUnstableException();
            }

            var observedAt = _timeProvider.GetUtcNow();
            var freshness = _options.Freshness > TimeSpan.Zero
                ? _options.Freshness
                : TimeSpan.FromMinutes(15);
            var observation = new NyxIdAuthorizationCatalogObservation(
                owner.Clone(),
                observedAt,
                observedAt + freshness,
                string.Empty,
                second.ContentDigest,
                second.Services);
            await _commandPort.ObserveAsync(observation, ct);
            if (await WaitUntilObservedAsync(observation, ct))
                return NyxIdAuthorizationCatalogRefreshResult.Observed;

            const string failureCode = "nyxid_catalog_observation_timeout";
            await _commandPort.RecordRefreshFailureAsync(owner, _timeProvider.GetUtcNow(), failureCode, ct);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut,
                failureCode);
        }
        catch (NyxIdAuthorizationCatalogAccessException ex)
            when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _commandPort.InvalidateAsync(owner, refreshStartedAt, "nyxid_catalog_access_denied", ct);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.AccessDenied,
                "nyxid_catalog_access_denied");
        }
        catch (NyxIdAuthorizationCatalogUnstableException)
        {
            const string failureCode = "nyxid_catalog_unstable";
            await _commandPort.RecordRefreshFailureAsync(owner, refreshStartedAt, failureCode, ct);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                failureCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "NyxID authorization catalog refresh failed for owner kind {OwnerKind}.",
                owner.OwnerKind);
            await _commandPort.RecordRefreshFailureAsync(
                owner,
                refreshStartedAt,
                "nyxid_catalog_refresh_failed",
                ct);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Failed,
                "nyxid_catalog_refresh_failed");
        }
    }

    private async Task<CatalogRead> ReadCatalogAsync(
        string endpointBaseUrl,
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct)
    {
        using var servicesDocument = await GetAsync(endpointBaseUrl, "/api/v1/user-services", bearerToken, ct);
        using var nodesDocument = await GetAsync(endpointBaseUrl, "/api/v1/nodes", bearerToken, ct);
        var nodes = ParseNodes(nodesDocument.RootElement, owner);
        var bindings = await ReadBindingsAsync(endpointBaseUrl, bearerToken, nodes, ct);
        var services = ParseServices(servicesDocument.RootElement, owner, nodes, bindings);
        return new CatalogRead(
            services,
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            nodes.Ordered.Select(static node => node.Id).ToArray());
    }

    private async Task<bool> WaitUntilObservedAsync(
        NyxIdAuthorizationCatalogObservation observation,
        CancellationToken ct)
    {
        var timeout = _options.ObservationTimeout > TimeSpan.Zero
            ? _options.ObservationTimeout
            : TimeSpan.Zero;
        var pollInterval = _options.ObservationPollInterval > TimeSpan.Zero
            ? _options.ObservationPollInterval
            : TimeSpan.FromMilliseconds(50);
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            var snapshot = await _queryPort.GetAsync(observation.Owner, ct);
            if (snapshot is
                {
                    StateVersion: > 0,
                    Invalidated: false,
                } &&
                string.Equals(snapshot.ContentDigest, observation.ContentDigest, StringComparison.Ordinal) &&
                snapshot.ObservedAtUtc >= observation.ObservedAtUtc &&
                snapshot.FreshUntilUtc >= observation.FreshUntilUtc)
            {
                return true;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
                return false;

            await Task.Delay(pollInterval, _timeProvider, ct);
        }
    }

    private async Task<JsonDocument> GetAsync(
        string authority,
        string path,
        string bearerToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{authority.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new NyxIdAuthorizationCatalogAccessException(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<IReadOnlyList<NodeBindingFact>> ReadBindingsAsync(
        string authority,
        string bearerToken,
        NodeCatalog nodes,
        CancellationToken ct)
    {
        var bindings = new List<NodeBindingFact>();
        foreach (var node in nodes.Ordered)
        {
            using var document = await GetAsync(
                authority,
                $"/api/v1/nodes/{Uri.EscapeDataString(node.Id)}/bindings",
                bearerToken,
                ct);
            foreach (var binding in RequiredArray(document.RootElement, "bindings").EnumerateArray())
            {
                if (binding.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                    continue;
                bindings.Add(new NodeBindingFact(
                    RequiredString(binding, "id"),
                    node.Id,
                    RequiredString(binding, "service_id"),
                    RequiredInt32(binding, "priority")));
            }
        }
        return bindings
            .OrderBy(static binding => binding.Priority)
            .ThenBy(static binding => binding.NodeId, StringComparer.Ordinal)
            .ThenBy(static binding => binding.BindingId, StringComparer.Ordinal)
            .ToArray();
    }

    private static NodeCatalog ParseNodes(
        JsonElement root,
        AuthorizationOwnerIdentity owner)
    {
        var ordered = new List<NodeFact>();
        var byId = new Dictionary<string, NodeFact>(StringComparer.Ordinal);
        foreach (var node in RequiredArray(root, "nodes").EnumerateArray())
        {
            var id = RequiredString(node, "id");
            var ownerElement = RequiredObject(node, "owner");
            var expectedKind = owner.OwnerKind == AuthorizationOwnerKind.Organization ? "org" : "user";
            if (!string.Equals(RequiredString(ownerElement, "kind"), expectedKind, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(ownerElement, "id"), owner.OwnerSubject, StringComparison.Ordinal))
            {
                continue;
            }
            var fact = new NodeFact(id, OptionalString(node, "name") ?? id);
            if (!byId.TryAdd(id, fact))
                throw new InvalidOperationException($"NyxID returned duplicate node identity '{id}'.");
            ordered.Add(fact);
        }
        ordered.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return new NodeCatalog(ordered, byId);
    }

    private static IReadOnlyList<NyxIdAuthorizationServiceEvidence> ParseServices(
        JsonElement root,
        AuthorizationOwnerIdentity owner,
        NodeCatalog nodes,
        IReadOnlyList<NodeBindingFact> bindings)
    {
        var result = new List<NyxIdAuthorizationServiceEvidence>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in RequiredArray(root, "services").EnumerateArray())
        {
            if (service.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                continue;
            if (!TryResolveAccess(service, owner, out var access))
                continue;

            var serviceId = RequiredString(service, "id");
            if (!serviceIds.Add(serviceId))
                throw new InvalidOperationException($"NyxID returned duplicate user service identity '{serviceId}'.");
            var catalogServiceId = OptionalString(service, "catalog_service_id");
            var primaryNodeId = OptionalString(service, "node_id");
            var matchingBindings = catalogServiceId == null
                ? []
                : bindings.Where(binding =>
                        string.Equals(binding.CatalogServiceId, catalogServiceId, StringComparison.Ordinal))
                    .ToArray();

            var edges = ResolveNodeEdges(primaryNodeId, matchingBindings);
            var evidence = new NyxIdAuthorizationServiceEvidence
            {
                UserServiceId = serviceId,
                ServiceSlug = RequiredString(service, "slug"),
                DisplayName = OptionalString(service, "label") ??
                              OptionalString(service, "catalog_service_name") ??
                              RequiredString(service, "slug"),
                Access = access,
                NodeGrantRequirement = edges.Count == 0
                    ? AuthorizationGrantRequirement.NotRequired
                    : AuthorizationGrantRequirement.Required,
            };
            foreach (var edge in edges)
            {
                if (!nodes.ById.TryGetValue(edge.NodeId, out var node))
                {
                    throw new InvalidOperationException(
                        $"NyxID service '{serviceId}' references node '{edge.NodeId}' outside the exact owner topology.");
                }
                evidence.Nodes.Add(new NyxIdAuthorizationNodeEvidence
                {
                    NodeId = node.Id,
                    DisplayName = node.DisplayName,
                    Role = edge.Role,
                    EdgeKind = edge.EdgeKind,
                    BindingId = edge.BindingId,
                    RoutePriority = edge.RoutePriority,
                });
            }
            result.Add(evidence);
        }

        return result
            .OrderBy(static service => service.UserServiceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<NodeEdgeFact> ResolveNodeEdges(
        string? explicitPrimaryNodeId,
        IReadOnlyList<NodeBindingFact> bindings)
    {
        foreach (var priorityGroup in bindings.GroupBy(static binding => binding.Priority))
        {
            if (priorityGroup.Select(static binding => binding.NodeId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new InvalidOperationException(
                    $"NyxID node bindings at priority {priorityGroup.Key} do not publish a deterministic node order.");
            }
        }

        var primaryNodeId = explicitPrimaryNodeId;
        if (primaryNodeId == null)
        {
            if (bindings.Count == 0)
                return [];
            var lowestPriority = bindings.Min(static binding => binding.Priority);
            var primaryCandidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings.Where(binding => binding.Priority == lowestPriority))
                primaryCandidates.Add(binding.NodeId);
            if (primaryCandidates.Count != 1)
                throw new InvalidOperationException("NyxID node binding topology does not identify one primary node.");
            primaryNodeId = primaryCandidates.Single();
        }

        var edges = new List<NodeEdgeFact>();
        if (explicitPrimaryNodeId != null)
        {
            edges.Add(new NodeEdgeFact(
                explicitPrimaryNodeId,
                NyxIdNodeRole.Primary,
                NyxIdNodeEdgeKind.UserServicePrimary,
                string.Empty,
                0));
        }
        foreach (var binding in bindings)
        {
            edges.Add(new NodeEdgeFact(
                binding.NodeId,
                string.Equals(binding.NodeId, primaryNodeId, StringComparison.Ordinal)
                    ? NyxIdNodeRole.Primary
                    : NyxIdNodeRole.Fallback,
                NyxIdNodeEdgeKind.NodeBinding,
                binding.BindingId,
                binding.Priority));
        }
        return edges;
    }

    private static bool TryResolveAccess(
        JsonElement service,
        AuthorizationOwnerIdentity owner,
        out NyxIdAuthorizationAccess access)
    {
        access = NyxIdAuthorizationAccess.Unspecified;
        var source = RequiredObject(service, "credential_source");
        var sourceType = RequiredString(source, "type");
        if (owner.OwnerKind == AuthorizationOwnerKind.Personal)
        {
            if (!string.Equals(sourceType, "personal", StringComparison.Ordinal))
                return false;
            access = NyxIdAuthorizationAccess.Permitted;
            return true;
        }

        if (!string.Equals(sourceType, "org", StringComparison.Ordinal) ||
            !string.Equals(RequiredString(source, "org_id"), owner.OwnerSubject, StringComparison.Ordinal))
        {
            return false;
        }
        access = RequiredBoolean(source, "allowed")
            ? NyxIdAuthorizationAccess.Permitted
            : NyxIdAuthorizationAccess.ViewOnly;
        return true;
    }

    private static JsonElement RequiredArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidOperationException($"NyxID catalog response is missing array '{name}'.");

    private static JsonElement RequiredObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException($"NyxID catalog response is missing object '{name}'.");

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ??
        throw new InvalidOperationException($"NyxID catalog response is missing string '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int RequiredInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidOperationException($"NyxID catalog response is missing integer '{name}'.");

    private static bool RequiredBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidOperationException($"NyxID catalog response is missing boolean '{name}'.");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record NodeFact(string Id, string DisplayName);
    private sealed record NodeCatalog(
        IReadOnlyList<NodeFact> Ordered,
        IReadOnlyDictionary<string, NodeFact> ById);
    private sealed record NodeBindingFact(
        string BindingId,
        string NodeId,
        string CatalogServiceId,
        int Priority);
    private sealed record NodeEdgeFact(
        string NodeId,
        NyxIdNodeRole Role,
        NyxIdNodeEdgeKind EdgeKind,
        string BindingId,
        int RoutePriority);
    private sealed record CatalogRead(
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> Services,
        string ContentDigest,
        IReadOnlyList<string> RequestedNodeIds);

    private sealed class NyxIdAuthorizationCatalogUnstableException : Exception;

    private sealed class NyxIdAuthorizationCatalogAccessException(HttpStatusCode statusCode) : Exception
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
