using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Foundation.Abstractions.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    public const string AccessDeniedFailureCode = "nyxid_catalog_access_denied";
    public const string EmptyCatalogFailureCode = "nyxid_catalog_empty";
    public const string InvalidCatalogFailureCode = "nyxid_catalog_invalid";
    private const int FreshnessMinutes = 15;

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdAuthorizationCatalogQueryPort? _queryPort;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPort(
        INyxIdAuthorizationCatalogCommandPort commandPort,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger,
        INyxIdAuthorizationCatalogQueryPort? queryPort = null)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queryPort = queryPort;
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
        if (!string.Equals(
                owner.Authority?.Trim(),
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
        if (string.IsNullOrWhiteSpace(owner.OwnerSubject))
            throw new InvalidOperationException("NyxID authorization catalog owner subject is required.");

        var normalizedOwner = owner.Clone();
        normalizedOwner.Authority = NyxIdAuthorizationAuthorities.NyxId;
        normalizedOwner.OwnerSubject = owner.OwnerSubject.Trim();
        var now = _timeProvider.GetUtcNow();
        await _commandPort.ActivateAsync(normalizedOwner, now, ct);
        var lifecycleFence = await ResolveLifecycleFenceAsync(normalizedOwner, ct);

        NyxIdAuthorizationCatalogRefreshResult failureResult;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildNyxIdUri("/api/v1/keys"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await _commandPort.RecordRefreshFailureAsync(
                    normalizedOwner,
                    now,
                    AccessDeniedFailureCode,
                    ct);
                return new NyxIdAuthorizationCatalogRefreshResult(
                    NyxIdAuthorizationCatalogRefreshStatus.AccessDenied,
                    AccessDeniedFailureCode);
            }

            if ((int)response.StatusCode is < 200 or > 299)
            {
                var failureCode = $"nyxid_catalog_http_{(int)response.StatusCode}";
                _logger.LogWarning(
                    "NyxID authorization catalog endpoint returned {StatusCode}: {Body}",
                    response.StatusCode,
                    ScrubForLog(body));
                await _commandPort.RecordRefreshFailureAsync(normalizedOwner, now, failureCode, ct);
                return new NyxIdAuthorizationCatalogRefreshResult(
                    NyxIdAuthorizationCatalogRefreshStatus.Failed,
                    failureCode);
            }

            var services = ParseServices(body);
            if (services.Count == 0)
            {
                await _commandPort.RecordRefreshFailureAsync(
                    normalizedOwner,
                    now,
                    EmptyCatalogFailureCode,
                    ct);
                return new NyxIdAuthorizationCatalogRefreshResult(
                    NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                    EmptyCatalogFailureCode);
            }

            var contentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                normalizedOwner,
                services);
            await _commandPort.ObserveAsync(
                new NyxIdAuthorizationCatalogObservation(
                    normalizedOwner,
                    now,
                    now.AddMinutes(FreshnessMinutes),
                    contentDigest,
                    contentDigest,
                    services,
                    lifecycleFence),
                ct);
            return NyxIdAuthorizationCatalogRefreshResult.Observed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID authorization catalog response was not parseable.");
            failureResult = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Failed,
                InvalidCatalogFailureCode);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "NyxID authorization catalog response was invalid.");
            failureResult = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Failed,
                InvalidCatalogFailureCode);
        }

        await _commandPort.RecordRefreshFailureAsync(
            normalizedOwner,
            now,
            failureResult.FailureCode,
            ct);
        return failureResult;
    }

    private async Task<long> ResolveLifecycleFenceAsync(
        AuthorizationOwnerIdentity owner,
        CancellationToken ct)
    {
        if (_queryPort is null)
            return 0;

        var snapshot = await _queryPort.GetAsync(owner, ct);
        return snapshot?.LifecycleFence ?? 0;
    }

    private Uri BuildNyxIdUri(string path)
    {
        var authority = _configuration["Aevatar:NyxId:Authority"] ??
                        _configuration["NyxId:Authority"] ??
                        _configuration["NyxID:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException("NyxID authority is not configured.");
        return new Uri($"{authority.Trim().TrimEnd('/')}{path}", UriKind.Absolute);
    }

    private static IReadOnlyList<NyxIdAuthorizationServiceEvidence> ParseServices(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var keys = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : TryGetProperty(root, "keys", "services", "items") is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray()
                : throw new InvalidOperationException("NyxID authorization catalog response must contain a keys array.");

        var services = new List<NyxIdAuthorizationServiceEvidence>();
        foreach (var item in keys)
        {
            var service = TryParseService(item);
            if (service is not null)
                services.Add(service);
        }

        return services
            .GroupBy(static service => service.UserServiceId.Trim(), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static service => service.UserServiceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static NyxIdAuthorizationServiceEvidence? TryParseService(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        if (TryReadBool(item, "is_active", "isActive") == false)
            return null;

        var serviceId = ReadOptionalString(item, "id", "service_id", "serviceId", "user_service_id", "userServiceId");
        var serviceSlug = ReadOptionalString(item, "catalog_service_slug", "catalogServiceSlug", "slug");
        if (serviceId is null || serviceSlug is null)
            return null;

        var displayName =
            ReadOptionalString(item, "catalog_service_name", "catalogServiceName", "name", "label") ??
            serviceSlug;
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = serviceId,
            ServiceSlug = serviceSlug,
            DisplayName = displayName,
            Access = IsPermitted(item)
                ? NyxIdAuthorizationAccess.Permitted
                : NyxIdAuthorizationAccess.Denied,
        };

        var nodeId = ReadOptionalString(item, "node_id", "nodeId");
        if (nodeId is null)
        {
            service.NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired;
            return service;
        }

        service.NodeGrantRequirement = AuthorizationGrantRequirement.Required;
        service.Nodes.Add(new NyxIdAuthorizationNodeEvidence
        {
            NodeId = nodeId,
            DisplayName = ReadOptionalString(item, "node_name", "nodeName", "node_label", "nodeLabel") ?? nodeId,
            Role = NyxIdNodeRole.Primary,
            EdgeKind = NyxIdNodeEdgeKind.UserServicePrimary,
            RoutePriority = TryReadInt(item, "node_priority", "nodePriority") ?? 0,
        });
        return service;
    }

    private static bool IsPermitted(JsonElement item)
    {
        if (TryReadBool(item, "connected") == false ||
            TryReadBool(item, "requires_connection", "requiresConnection") == true)
        {
            return false;
        }

        var status = ReadOptionalString(item, "status");
        return status is null ||
               string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement? TryGetProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
                return value;
        }

        return null;
    }

    private static string? ReadOptionalString(JsonElement element, params string[] names)
    {
        if (TryGetProperty(element, names) is not { } value)
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.False
            ? value.ToString()
            : null;
    }

    private static bool? TryReadBool(JsonElement element, params string[] names)
    {
        if (TryGetProperty(element, names) is not { } value)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? TryReadInt(JsonElement element, params string[] names)
    {
        if (TryGetProperty(element, names) is not { } value)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string ScrubForLog(string body)
    {
        var scrubbed = SecretScrubber.Scrub(body);
        return scrubbed.Length > 500 ? scrubbed[..500] : scrubbed;
    }
}
