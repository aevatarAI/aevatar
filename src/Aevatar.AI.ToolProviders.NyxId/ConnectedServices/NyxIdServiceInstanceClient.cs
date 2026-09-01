using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal sealed record NyxIdServiceInstanceBinding(NyxIdServiceInstance Instance, string AccessToken);

public sealed class NyxIdServiceInstanceClient
{
    private readonly NyxIdApiClient _client;

    public NyxIdServiceInstanceClient(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal static bool IsCallerExecutable(NyxIdServiceInstance instance)
    {
        var readiness = instance.CallerExecutionReadiness;
        return instance.IsActive &&
               instance.CredentialAllowed &&
               readiness is not null &&
               readiness.Connected &&
               readiness.CredentialStatus == NyxIdServiceCredentialStatus.Active &&
               (!instance.HasNodeId || readiness.NodeStatus == NyxIdServiceNodeStatus.Online);
    }

    internal async Task<IReadOnlyList<NyxIdServiceInstanceBinding>> DiscoverAsync(
        string userToken,
        string? organizationToken,
        CancellationToken ct)
    {
        var candidates = new List<NyxIdServiceInstanceBinding>();
        var userResponse = await _client.ListServicesAsync(userToken, ct);
        EnsureDiscoverySucceeded(userResponse);
        candidates.AddRange(ParseBindings(
            userResponse,
            userToken,
            NyxIdServiceAccessTokenSource.User));
        if (!string.IsNullOrWhiteSpace(organizationToken) &&
            !string.Equals(userToken, organizationToken, StringComparison.Ordinal))
        {
            var organizationResponse = await _client.ListServicesAsync(organizationToken, ct);
            EnsureDiscoverySucceeded(organizationResponse);
            candidates.AddRange(ParseBindings(
                organizationResponse,
                organizationToken,
                NyxIdServiceAccessTokenSource.Organization));
        }

        var exact = new Dictionary<string, NyxIdServiceInstanceBinding>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var id = candidate.Instance.UserServiceId;
            if (conflicts.Contains(id))
                continue;
            if (!exact.TryGetValue(id, out var existing))
            {
                exact.Add(id, candidate);
                continue;
            }
            if (SameAuthority(existing.Instance, candidate.Instance) &&
                string.Equals(existing.AccessToken, candidate.AccessToken, StringComparison.Ordinal))
            {
                continue;
            }

            exact.Remove(id);
            conflicts.Add(id);
        }

        return exact.Values.OrderBy(static binding => binding.Instance.UserServiceId, StringComparer.Ordinal).ToArray();
    }

    private static void EnsureDiscoverySucceeded(string response)
    {
        if (IsErrorResponse(response))
            throw new InvalidOperationException("connected_service_inventory_unavailable");
    }

    internal async Task<NyxIdServiceInstanceBinding?> RevalidateAsync(
        NyxIdServiceInstanceBinding candidate,
        CancellationToken ct)
    {
        var response = await _client.GetServiceAsync(
            candidate.AccessToken,
            candidate.Instance.UserServiceId,
            ct);
        var current = ParseSingleBinding(
            response,
            candidate.AccessToken,
            candidate.Instance.AccessTokenSource);
        if (current is null ||
            !current.Instance.IsActive ||
            !current.Instance.CredentialAllowed ||
            !SameAuthority(candidate.Instance, current.Instance))
        {
            return null;
        }

        return current;
    }

    internal async Task<NyxIdServiceMutationResult> UpdateAsync(
        NyxIdServiceInstanceBinding candidate,
        NyxIdServiceUpdateRequest request,
        CancellationToken ct)
    {
        var current = await RevalidateAsync(candidate, ct);
        if (current is null)
            return MutationResult(candidate.Instance.UserServiceId, Error("identity_revalidation_failed"));
        var body = new Dictionary<string, object?>();
        if (request.HasLabel)
            body["label"] = request.Label;
        if (request.HasEndpointUrl)
            body["endpoint_url"] = request.EndpointUrl;
        if (request.HasOpenapiSpecUrl)
            body["openapi_spec_url"] = request.OpenapiSpecUrl;
        if (request.HasIsActive)
            body["is_active"] = request.IsActive;
        var response = await _client.UpdateServiceAsync(
            current.AccessToken,
            current.Instance.UserServiceId,
            JsonSerializer.Serialize(body),
            ct);
        return MutationResult(current.Instance.UserServiceId, response);
    }

    internal async Task<NyxIdServiceMutationResult> RouteAsync(
        NyxIdServiceInstanceBinding candidate,
        NyxIdServiceRouteRequest request,
        CancellationToken ct)
    {
        var current = await RevalidateAsync(candidate, ct);
        if (current is null)
            return MutationResult(candidate.Instance.UserServiceId, Error("identity_revalidation_failed"));
        var nodeId = request.RouteCase switch
        {
            NyxIdServiceRouteRequest.RouteOneofCase.Direct when request.Direct => string.Empty,
            NyxIdServiceRouteRequest.RouteOneofCase.NodeId when !string.IsNullOrWhiteSpace(request.NodeId) =>
                request.NodeId,
            _ => null,
        };
        if (nodeId is null)
            return MutationResult(candidate.Instance.UserServiceId, Error("invalid_route"));
        var response = await _client.UpdateServiceRouteAsync(
            current.AccessToken,
            current.Instance.UserServiceId,
            JsonSerializer.Serialize(new { node_id = nodeId }),
            ct);
        return MutationResult(current.Instance.UserServiceId, response);
    }

    internal async Task<NyxIdServiceDeleteResult> DeleteAsync(
        NyxIdServiceInstanceBinding candidate,
        CancellationToken ct)
    {
        var current = await RevalidateAsync(candidate, ct);
        if (current is null)
        {
            return new NyxIdServiceDeleteResult
            {
                UserServiceId = candidate.Instance.UserServiceId,
                ResponseJson = Error("identity_revalidation_failed"),
            };
        }
        var response = await _client.DeleteServiceAsync(current.AccessToken, current.Instance.UserServiceId, ct);
        return new NyxIdServiceDeleteResult
        {
            UserServiceId = current.Instance.UserServiceId,
            Deleted = !IsErrorResponse(response),
            ResponseJson = response,
        };
    }

    private static NyxIdServiceMutationResult MutationResult(string userServiceId, string response) => new()
    {
        UserServiceId = userServiceId,
        Accepted = !IsErrorResponse(response),
        ResponseJson = response,
    };

    private static bool IsErrorResponse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<NyxIdServiceInstanceBinding> ParseBindings(
        string? json,
        string token,
        NyxIdServiceAccessTokenSource tokenSource)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return EnumerateItems(document.RootElement)
                .Select(item => ParseBinding(item, token, tokenSource))
                .Where(static binding => binding is not null)
                .Select(static binding => binding!)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static NyxIdServiceInstanceBinding? ParseSingleBinding(
        string? json,
        string token,
        NyxIdServiceAccessTokenSource tokenSource)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "key", "service", "data" })
                {
                    if (root.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object)
                        root = nested;
                }
            }
            return ParseBinding(root, token, tokenSource);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NyxIdServiceInstanceBinding? ParseBinding(
        JsonElement item,
        string token,
        NyxIdServiceAccessTokenSource tokenSource)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var id = ReadString(item, "user_service_id") ?? ReadString(item, "id");
        var slug = ReadString(item, "service_slug") ?? ReadString(item, "slug");
        var catalogId = ReadString(item, "catalog_service_id") ?? ReadString(item, "service_id");
        var catalogSlug = ReadString(item, "catalog_service_slug");
        var active = ReadBool(item, "is_active");
        if (!TryReadNodeId(item, out var nodeId) ||
            !TryReadCallerExecutionReadiness(item, nodeId, out var callerExecutionReadiness))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(id) ||
            (string.IsNullOrWhiteSpace(catalogId) && string.IsNullOrWhiteSpace(slug)) ||
            !active.HasValue ||
            !TryReadCredentialSource(item, out var credentialSource, out var credentialAllowed))
        {
            return null;
        }

        var routeConstraint = new NyxIdProxyRouteConstraint();
        if (!string.IsNullOrWhiteSpace(catalogId))
            routeConstraint.CatalogServiceId = catalogId;
        else
            routeConstraint.ServiceSlug = slug;
        var instance = new NyxIdServiceInstance
        {
            UserServiceId = id,
            DisplaySlug = slug ?? string.Empty,
            Label = ReadString(item, "label") ?? ReadString(item, "name") ?? slug ?? string.Empty,
            EndpointUrl = ReadString(item, "endpoint_url") ?? ReadString(item, "base_url") ?? string.Empty,
            EndpointId = ReadString(item, "endpoint_id") ?? string.Empty,
            IsActive = active.Value,
            CredentialSource = credentialSource,
            AccessTokenSource = tokenSource,
            RouteConstraint = routeConstraint,
            CredentialAllowed = credentialAllowed,
            CallerExecutionReadiness = callerExecutionReadiness,
        };
        if (!string.IsNullOrWhiteSpace(catalogId))
            instance.CatalogServiceId = catalogId;
        if (!string.IsNullOrWhiteSpace(catalogSlug))
            instance.CatalogServiceSlug = catalogSlug;
        var openApiSpecUrl = ReadString(item, "openapi_spec_url") ?? ReadString(item, "openapi_url");
        if (!string.IsNullOrWhiteSpace(openApiSpecUrl))
            instance.OpenapiSpecUrl = openApiSpecUrl;
        if (nodeId is not null)
            instance.NodeId = nodeId;
        return new NyxIdServiceInstanceBinding(instance, token);
    }

    private static bool TryReadNodeId(JsonElement item, out string? nodeId)
    {
        nodeId = null;
        if (!item.TryGetProperty("node_id", out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var candidate = value.GetString();
        if (string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        nodeId = candidate;
        return true;
    }

    private static bool TryReadCallerExecutionReadiness(
        JsonElement item,
        string? nodeId,
        out NyxIdServiceCallerExecutionReadiness readiness)
    {
        readiness = new NyxIdServiceCallerExecutionReadiness();
        if (ReadBool(item, "connected") is not { } connected ||
            !TryParseCredentialStatus(ReadString(item, "status"), out var credentialStatus) ||
            !TryParseNodeStatus(item, nodeId, out var nodeStatus))
        {
            return false;
        }

        readiness.CredentialStatus = credentialStatus;
        readiness.Connected = connected;
        readiness.NodeStatus = nodeStatus;
        return true;
    }

    private static bool TryParseCredentialStatus(
        string? value,
        out NyxIdServiceCredentialStatus status)
    {
        status = value switch
        {
            "active" => NyxIdServiceCredentialStatus.Active,
            "expired" => NyxIdServiceCredentialStatus.Expired,
            "revoked" => NyxIdServiceCredentialStatus.Revoked,
            "failed" => NyxIdServiceCredentialStatus.Failed,
            "refresh_failed" => NyxIdServiceCredentialStatus.RefreshFailed,
            "pending_auth" => NyxIdServiceCredentialStatus.PendingAuthorization,
            _ => NyxIdServiceCredentialStatus.Unspecified,
        };
        return status != NyxIdServiceCredentialStatus.Unspecified;
    }

    private static bool TryParseNodeStatus(
        JsonElement item,
        string? nodeId,
        out NyxIdServiceNodeStatus status)
    {
        status = NyxIdServiceNodeStatus.Unspecified;
        if (nodeId is null)
        {
            if (item.TryGetProperty("node_status", out _))
                return false;
            status = NyxIdServiceNodeStatus.NotBound;
            return true;
        }

        status = ReadString(item, "node_status") switch
        {
            "online" => NyxIdServiceNodeStatus.Online,
            "offline" => NyxIdServiceNodeStatus.Offline,
            "draining" => NyxIdServiceNodeStatus.Draining,
            "unknown" => NyxIdServiceNodeStatus.Unknown,
            "inaccessible" => NyxIdServiceNodeStatus.Inaccessible,
            _ => NyxIdServiceNodeStatus.Unspecified,
        };
        return status != NyxIdServiceNodeStatus.Unspecified;
    }

    private static bool TryReadCredentialSource(
        JsonElement item,
        out NyxIdServiceCredentialSource credentialSource,
        out bool credentialAllowed)
    {
        credentialSource = NyxIdServiceCredentialSource.Unspecified;
        credentialAllowed = false;
        if (!item.TryGetProperty("credential_source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        switch (ReadString(source, "type"))
        {
            case "personal":
                credentialSource = NyxIdServiceCredentialSource.Personal;
                credentialAllowed = true;
                return true;
            case "org" when ReadBool(source, "allowed") is { } allowed:
                credentialSource = NyxIdServiceCredentialSource.Organization;
                credentialAllowed = allowed;
                return true;
            default:
                return false;
        }
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();
        if (root.ValueKind != JsonValueKind.Object)
            return [];
        foreach (var property in new[] { "keys", "services", "data" })
        {
            if (root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array)
                return items.EnumerateArray().ToArray();
        }
        return [];
    }

    private static bool SameAuthority(NyxIdServiceInstance left, NyxIdServiceInstance right) =>
        string.Equals(left.UserServiceId, right.UserServiceId, StringComparison.Ordinal) &&
        left.CredentialSource == right.CredentialSource &&
        left.AccessTokenSource == right.AccessTokenSource &&
        left.IsActive == right.IsActive &&
        left.CredentialAllowed == right.CredentialAllowed &&
        string.Equals(left.CatalogServiceId, right.CatalogServiceId, StringComparison.Ordinal) &&
        string.Equals(left.CatalogServiceSlug, right.CatalogServiceSlug, StringComparison.Ordinal) &&
        string.Equals(left.DisplaySlug, right.DisplaySlug, StringComparison.Ordinal) &&
        string.Equals(left.EndpointId, right.EndpointId, StringComparison.Ordinal) &&
        string.Equals(left.EndpointUrl, right.EndpointUrl, StringComparison.Ordinal) &&
        string.Equals(left.OpenapiSpecUrl, right.OpenapiSpecUrl, StringComparison.Ordinal) &&
        string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal) &&
        Equals(left.CallerExecutionReadiness, right.CallerExecutionReadiness) &&
        Equals(left.RouteConstraint, right.RouteConstraint);

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    internal static string Error(string code) => JsonSerializer.Serialize(new { error = code });
}
