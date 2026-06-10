using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public static class NyxIdLlmServiceCatalogParser
{
    private const string ReadyStatus = "ready";

    public static NyxIdLlmServicesResult ParseServicesResult(string response)
    {
        using var document = ParseSuccessDocument(response);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return new NyxIdLlmServicesResult(ParseServicesArray(root), null);

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM services response must be a JSON object or array.");

        if (TryGetProperty(root, "providers") is { ValueKind: JsonValueKind.Array } providers)
            return ParseLegacyStatus(root, providers);

        var servicesElement = TryGetProperty(root, "services", "items");
        var services = servicesElement?.ValueKind == JsonValueKind.Array
            ? ParseServicesArray(servicesElement.Value)
            : [];
        var setupHint = TryGetProperty(root, "setup_hint", "setupHint") is { } hint
            ? ParseSetupHint(hint)
            : null;

        return new NyxIdLlmServicesResult(services, setupHint);
    }

    public static NyxIdLlmServicesResult MergeProxyRouteCandidates(
        NyxIdLlmServicesResult result,
        string proxyServicesResponse)
    {
        ArgumentNullException.ThrowIfNull(result);
        return MergeRouteCandidates(result, ParseProxyRouteCandidates(proxyServicesResponse));
    }

    public static NyxIdLlmServicesResult MergeUserKeyRouteCandidates(
        NyxIdLlmServicesResult result,
        string userKeysResponse)
    {
        ArgumentNullException.ThrowIfNull(result);
        return MergeRouteCandidates(result, ParseUserKeyRouteCandidates(userKeysResponse));
    }

    private static NyxIdLlmServicesResult MergeRouteCandidates(
        NyxIdLlmServicesResult result,
        IReadOnlyList<NyxIdLlmService> candidates)
    {
        if (candidates.Count == 0)
            return result;

        var merged = result.Services.ToList();
        foreach (var candidate in candidates)
        {
            var duplicateIndex = FindMatchingServiceIndex(merged, candidate);
            if (duplicateIndex >= 0)
            {
                if (ShouldPreferService(candidate, merged[duplicateIndex]))
                    merged[duplicateIndex] = candidate;

                continue;
            }

            merged.Add(candidate);
        }

        return result with { Services = merged };
    }

    public static IReadOnlyList<NyxIdLlmService> ParseProxyRouteCandidates(string response)
    {
        using var document = ParseSuccessDocument(response);
        var services = new List<NyxIdLlmService>();
        foreach (var item in EnumerateProxyServiceEntries(document.RootElement))
        {
            var service = TryParseProxyRouteCandidate(item);
            if (service is not null)
                services.Add(service);
        }

        return services;
    }

    /// <summary>
    /// Parses the NyxID unified key list (<c>GET /api/v1/keys</c>) into LLM route
    /// candidates. A key is the per-user credential binding for a service, so an
    /// active key is authoritative evidence the route is usable by this user -
    /// unlike <c>/api/v1/proxy/services</c>, whose <c>connected</c> flag only
    /// reflects the legacy connections store.
    /// </summary>
    public static IReadOnlyList<NyxIdLlmService> ParseUserKeyRouteCandidates(string response)
    {
        using var document = ParseSuccessDocument(response);
        var services = new List<NyxIdLlmService>();
        foreach (var item in EnumerateUserKeyEntries(document.RootElement))
        {
            var service = TryParseUserKeyRouteCandidate(item);
            if (service is not null)
                services.Add(service);
        }

        return services;
    }

    public static NyxIdLlmService ParseProvisionedService(string response)
    {
        using var document = ParseSuccessDocument(response);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "service") is { } service)
        {
            return ParseService(service);
        }

        return ParseService(root);
    }

    private static IEnumerable<JsonElement> EnumerateProxyServiceEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "custom_services", "customServices", "items", "data" })
        {
            if (TryGetProperty(root, propertyName) is not { ValueKind: JsonValueKind.Array } array)
                continue;

            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    private static IEnumerable<JsonElement> EnumerateUserKeyEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (TryGetProperty(root, "keys") is { ValueKind: JsonValueKind.Array } keys)
        {
            foreach (var item in keys.EnumerateArray())
                yield return item;
        }
    }

    private static NyxIdLlmService? TryParseUserKeyRouteCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var slug = ReadOptionalString(element, "catalog_service_slug", "catalogServiceSlug", "slug");
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        // Unified keys also cover ssh/node bindings; only http services can act as LLM routes.
        var serviceType = ReadOptionalString(element, "service_type", "serviceType");
        if (serviceType is not null && !string.Equals(serviceType.Trim(), "http", StringComparison.OrdinalIgnoreCase))
            return null;

        var displayName = ReadOptionalString(element, "catalog_service_name", "catalogServiceName", "label") ?? slug;
        if (!LooksLikeUserKeyLlmRouteCandidate(element, slug, displayName))
            return null;

        var routeValue = NormalizeProxyRouteValue(value: null, slug);
        if (string.IsNullOrWhiteSpace(routeValue))
            return null;

        var status = ResolveUserKeyStatus(element);
        var allowed = string.Equals(status, ReadyStatus, StringComparison.OrdinalIgnoreCase) &&
                      ReadAllowedOverride(element) != false;
        return new NyxIdLlmService(
            UserServiceId: ReadOptionalString(element, "id") ?? slug,
            ServiceSlug: slug.Trim(),
            DisplayName: displayName.Trim(),
            RouteValue: routeValue,
            DefaultModel: null,
            Models: [],
            Status: status,
            Source: NyxIdLlmProviderSource.UserService,
            Allowed: allowed,
            Description: null);
    }

    private static bool LooksLikeUserKeyLlmRouteCandidate(JsonElement element, string slug, string displayName)
    {
        var signals = new[]
        {
            slug,
            displayName,
            ReadOptionalString(element, "label"),
            ReadOptionalString(element, "catalog_service_name", "catalogServiceName"),
            ReadOptionalString(element, "endpoint_url", "endpointUrl"),
            ReadOptionalString(element, "openapi_spec_url", "openapiSpecUrl"),
        };

        if (signals.Any(ContainsNegativeLlmRouteSignal))
            return false;

        if (signals.Any(ContainsStrongLlmRouteSignal))
            return true;

        return signals
            .SelectMany(EnumerateWeakLlmRouteSignals)
            .Distinct(StringComparer.Ordinal)
            .Count() >= 2;
    }

    private static string ResolveUserKeyStatus(JsonElement element)
    {
        if (ReadOptionalBool(element, "is_active", "isActive") == false)
            return "inactive";

        var status = ReadOptionalString(element, "status")?.Trim();
        return string.IsNullOrWhiteSpace(status) || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            ? ReadyStatus
            : status;
    }

    private static NyxIdLlmService? TryParseProxyRouteCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var slug = ReadOptionalString(
            element,
            "slug",
            "service_slug",
            "serviceSlug",
            "provider_slug",
            "providerSlug");
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var displayName = ReadOptionalString(
            element,
            "display_name",
            "displayName",
            "name",
            "service_name",
            "serviceName",
            "provider_name",
            "providerName")
            ?? slug;

        if (!LooksLikeLlmRouteCandidate(element, slug, displayName))
            return null;

        var routeValue = NormalizeProxyRouteValue(
            ReadOptionalString(
                element,
                "proxy_url_slug",
                "proxyUrlSlug",
                "proxy_url",
                "proxyUrl",
                "route_value",
                "routeValue"),
            slug);
        if (string.IsNullOrWhiteSpace(routeValue))
            return null;

        var status = ResolveProxyStatus(element);
        var explicitAllowed = ReadAllowedOverride(element);
        var models = ReadStringArray(element, "models", "available_models", "availableModels");
        return new NyxIdLlmService(
            UserServiceId: ReadOptionalString(
                    element,
                    "user_service_id",
                    "userServiceId",
                    "service_id",
                    "serviceId",
                    "id")
                ?? slug,
            ServiceSlug: slug.Trim(),
            DisplayName: displayName.Trim(),
            RouteValue: routeValue,
            DefaultModel: ReadOptionalString(element, "default_model", "defaultModel"),
            Models: models,
            Status: status,
            Source: NyxIdLlmProviderSource.ProxyService,
            Allowed: explicitAllowed ?? string.Equals(status, ReadyStatus, StringComparison.OrdinalIgnoreCase),
            Description: ReadOptionalString(element, "description"));
    }

    public static string NormalizeProvisionEndpointId(string provisionEndpointId)
    {
        var candidate = provisionEndpointId.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("LLM preset provisioning endpoint is invalid.");
        }

        var normalized = candidate.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("LLM preset provisioning endpoint is invalid.");

        return normalized;
    }

    private static JsonDocument ParseSuccessDocument(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("NyxID returned an empty LLM services response.");

        var document = JsonDocument.Parse(response);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out var errorProp) &&
            errorProp.ValueKind == JsonValueKind.True)
        {
            var status = TryReadInt(document.RootElement, "status");
            var body = ReadOptionalString(document.RootElement, "body");
            var message = ReadOptionalString(document.RootElement, "message");
            document.Dispose();
            throw new InvalidOperationException(
                $"NyxID LLM services request failed: status={status?.ToString() ?? "unknown"}"
                + (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={body}")
                + (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}"));
        }

        return document;
    }

    private static NyxIdLlmServicesResult ParseLegacyStatus(JsonElement root, JsonElement providers)
    {
        var supportedModels = ReadStringArray(root, "supported_models", "supportedModels");
        var modelsByProvider = ReadStringMapArray(root, "models_by_provider", "modelsByProvider");
        var services = new List<NyxIdLlmService>();

        foreach (var provider in providers.EnumerateArray())
        {
            if (provider.ValueKind != JsonValueKind.Object)
                continue;

            var slug = ReadRequiredString(provider, "provider_slug", "providerSlug");
            var status = ReadOptionalString(provider, "status") ?? "unknown";
            var models = modelsByProvider.TryGetValue(slug, out var providerModels) && providerModels.Count > 0
                ? providerModels
                : supportedModels;
            var routeValue = ResolveLegacyRouteValue(provider, slug);

            services.Add(new NyxIdLlmService(
                UserServiceId: slug,
                ServiceSlug: slug,
                DisplayName: ReadOptionalString(provider, "provider_name", "providerName") ?? slug,
                RouteValue: routeValue,
                DefaultModel: models.FirstOrDefault(),
                Models: models,
                Status: status,
                Source: ReadOptionalString(provider, "source") ?? NyxIdLlmProviderSource.GatewayProvider,
                Allowed: string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase),
                Description: ReadOptionalString(provider, "description")));
        }

        return new NyxIdLlmServicesResult(services, null);
    }

    private static string ResolveLegacyRouteValue(JsonElement provider, string slug)
    {
        var proxyUrl = ReadOptionalString(provider, "proxy_url", "proxyUrl");
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            var trimmed = proxyUrl.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
                return trimmed;

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
                !string.IsNullOrWhiteSpace(absolute.AbsolutePath))
            {
                return absolute.PathAndQuery;
            }
        }

        return $"/api/v1/llm/{slug}/v1";
    }

    private static IReadOnlyList<NyxIdLlmService> ParseServicesArray(JsonElement element)
    {
        var services = new List<NyxIdLlmService>();
        foreach (var item in element.EnumerateArray())
            services.Add(ParseService(item));
        return services;
    }

    private static NyxIdLlmService ParseService(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM service entry must be a JSON object.");

        var userServiceId = ReadRequiredString(element, "user_service_id", "userServiceId", "service_id", "serviceId");
        var serviceSlug = ReadRequiredString(element, "service_slug", "serviceSlug");
        var displayName = ReadOptionalString(element, "display_name", "displayName", "service_name", "serviceName")
            ?? serviceSlug;
        var routeValue = ReadRequiredString(element, "route_value", "routeValue", "proxy_url", "proxyUrl");
        var models = ReadStringArray(element, "models", "available_models", "availableModels");

        return new NyxIdLlmService(
            UserServiceId: userServiceId,
            ServiceSlug: serviceSlug,
            DisplayName: displayName,
            RouteValue: routeValue,
            DefaultModel: ReadOptionalString(element, "default_model", "defaultModel"),
            Models: models,
            Status: ReadOptionalString(element, "status") ?? "unknown",
            Source: ReadOptionalString(element, "source") ?? NyxIdLlmProviderSource.UserService,
            Allowed: ReadOptionalBool(element, "allowed") ?? false,
            Description: ReadOptionalString(element, "description"));
    }

    private static UserLlmSetupHint? ParseSetupHint(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM setup_hint must be a JSON object.");

        var setupUrl = ReadOptionalString(element, "setup_url", "setupUrl") ?? string.Empty;
        var presets = new List<UserLlmPreset>();
        if (TryGetProperty(element, "presets") is { ValueKind: JsonValueKind.Array } presetsProp)
        {
            foreach (var presetElement in presetsProp.EnumerateArray())
                presets.Add(ParsePreset(presetElement));
        }

        return new UserLlmSetupHint(setupUrl, presets);
    }

    private static UserLlmPreset ParsePreset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM preset entry must be a JSON object.");

        var id = ReadRequiredString(element, "id");
        return new UserLlmPreset(
            Id: id,
            Title: ReadOptionalString(element, "title") ?? id,
            Description: ReadOptionalString(element, "description") ?? string.Empty,
            Activation: ParseActivation(element));
    }

    private static UserLlmPresetActivation ParseActivation(JsonElement preset)
    {
        var activationElement = TryGetProperty(preset, "activation") is { ValueKind: JsonValueKind.Object } nested
            ? nested
            : preset;

        var type = ReadOptionalString(activationElement, "type", "kind")
            ?? ReadOptionalString(preset, "activation_type", "activationType")
            ?? string.Empty;

        return type.Trim().ToLowerInvariant() switch
        {
            "use_existing_service" or "use-existing-service" or "existing" => new UseExistingService(
                ServiceId: ReadRequiredString(activationElement, "service_id", "serviceId"),
                RouteValue: ReadRequiredString(activationElement, "route_value", "routeValue"),
                DefaultModel: ReadOptionalString(activationElement, "default_model", "defaultModel")),
            "provision_then_use" or "provision-then-use" or "provision" => new ProvisionThenUse(
                NormalizeProvisionEndpointId(ReadRequiredString(
                    activationElement,
                    "provision_endpoint_id",
                    "provisionEndpointId"))),
            _ => throw new InvalidOperationException($"Unsupported NyxID LLM preset activation type '{type}'."),
        };
    }

    private static bool LooksLikeLlmRouteCandidate(JsonElement element, string slug, string displayName)
    {
        var signals = new[]
        {
            slug,
            displayName,
            ReadOptionalString(element, "service_category", "serviceCategory", "category"),
            ReadOptionalString(element, "description", "summary"),
            ReadOptionalString(element, "docs_url", "docsUrl"),
            ReadOptionalString(element, "openapi_url", "openapiUrl"),
        };

        if (signals.Any(ContainsNegativeLlmRouteSignal))
            return false;

        if (signals.Any(ContainsStrongLlmRouteSignal))
            return true;

        return signals
            .SelectMany(EnumerateWeakLlmRouteSignals)
            .Distinct(StringComparer.Ordinal)
            .Count() >= 2;
    }

    private static bool ContainsStrongLlmRouteSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("llm", StringComparison.Ordinal) ||
               normalized.Contains("chat/completions", StringComparison.Ordinal) ||
               normalized.Contains("chat completions", StringComparison.Ordinal) ||
               normalized.Contains("chat completion", StringComparison.Ordinal) ||
               normalized.Contains("completions api", StringComparison.Ordinal) ||
               normalized.Contains("large language model", StringComparison.Ordinal) ||
               normalized.Contains("language model", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateWeakLlmRouteSignals(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("openai", StringComparison.Ordinal))
            yield return "openai";
        if (normalized.Contains("gpt", StringComparison.Ordinal))
            yield return "gpt";
        if (normalized.Contains("claude", StringComparison.Ordinal))
            yield return "claude";
    }

    private static bool ContainsNegativeLlmRouteSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("not an llm", StringComparison.Ordinal) ||
               normalized.Contains("not a llm", StringComparison.Ordinal) ||
               normalized.Contains("not llm", StringComparison.Ordinal) ||
               normalized.Contains("non-llm", StringComparison.Ordinal) ||
               normalized.Contains("not a language model", StringComparison.Ordinal) ||
               normalized.Contains("not a large language model", StringComparison.Ordinal);
    }

    private static string? NormalizeProxyRouteValue(string? value, string slug)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = slug.Trim();

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
            normalized = Uri.UnescapeDataString(absolute.AbsolutePath);

        if (normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        normalized = StripRouteTemplateSuffix(normalized.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized;

        return normalized.Contains('/', StringComparison.Ordinal)
            ? "/" + normalized
            : $"/api/v1/proxy/s/{normalized}";
    }

    private static string StripRouteTemplateSuffix(string value)
    {
        var normalized = value.TrimEnd('/');
        var templateIndex = normalized.LastIndexOf("/{", StringComparison.Ordinal);
        if (templateIndex >= 0 && normalized.EndsWith("}", StringComparison.Ordinal))
            normalized = normalized[..templateIndex];

        if (normalized.EndsWith("/*", StringComparison.Ordinal))
            normalized = normalized[..^2];

        return normalized.TrimEnd('/');
    }

    private static string ResolveProxyStatus(JsonElement element)
    {
        var status = ReadOptionalString(element, "status");
        if (!string.IsNullOrWhiteSpace(status))
            return status.Trim();

        var connected = ReadOptionalBool(element, "connected") == true;
        var hasNodeBinding = ReadOptionalBool(element, "has_node_binding", "hasNodeBinding") == true;
        var requiresConnection = ReadOptionalBool(element, "requires_connection", "requiresConnection");
        return connected || hasNodeBinding || requiresConnection == false
            ? ReadyStatus
            : "not_connected";
    }

    private static int FindMatchingServiceIndex(IReadOnlyList<NyxIdLlmService> services, NyxIdLlmService candidate)
    {
        for (var index = 0; index < services.Count; index++)
        {
            if (ShareServiceKey(services[index], candidate))
                return index;
        }

        return -1;
    }

    private static bool ShareServiceKey(NyxIdLlmService left, NyxIdLlmService right) =>
        EqualIfPresent(left.RouteValue, right.RouteValue) ||
        EqualIfPresent(left.UserServiceId, right.UserServiceId) ||
        EqualIfPresent(left.ServiceSlug, right.ServiceSlug);

    private static bool EqualIfPresent(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ShouldPreferService(NyxIdLlmService candidate, NyxIdLlmService existing)
    {
        if (IsUserServiceSource(candidate) != IsUserServiceSource(existing))
            return IsUserServiceSource(candidate);

        var candidateRank = ServiceSelectabilityRank(candidate);
        var existingRank = ServiceSelectabilityRank(existing);
        if (candidateRank != existingRank)
            return candidateRank > existingRank;

        return ServiceSourceRank(candidate.Source) > ServiceSourceRank(existing.Source);
    }

    private static bool IsUserServiceSource(NyxIdLlmService service) =>
        string.Equals(
            UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
            UserLlmRouteSource.UserService,
            StringComparison.OrdinalIgnoreCase);

    private static int ServiceSelectabilityRank(NyxIdLlmService service)
    {
        var ready = string.Equals(service.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase);
        return (service.Allowed, ready) switch
        {
            (true, true) => 3,
            (true, false) => 2,
            (false, true) => 1,
            _ => 0,
        };
    }

    private static int ServiceSourceRank(string? source)
    {
        var normalized = UserLlmCatalogNormalization.NormalizeSource(source).ToWireValue();
        return normalized switch
        {
            UserLlmRouteSource.UserService => 3,
            UserLlmRouteSource.ProxyService => 2,
            UserLlmRouteSource.GatewayProvider => 1,
            _ => 0,
        };
    }

    private static JsonElement? TryGetProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property))
                return property;
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement element, params string[] propertyNames)
    {
        var value = ReadOptionalString(element, propertyNames);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"NyxID LLM response is missing required field '{propertyNames[0]}'.");
        return value.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool? ReadOptionalBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    private static bool? ReadAllowedOverride(JsonElement element)
    {
        var allowed = ReadOptionalBool(element, "allowed");
        if (allowed is not null)
            return allowed;

        if (TryGetProperty(element, "credential_source", "credentialSource") is { ValueKind: JsonValueKind.Object } source)
            return ReadOptionalBool(source, "allowed");

        return null;
    }

    private static int? TryReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStringMapArray(
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            return property.EnumerateObject()
                .ToDictionary(
                    item => item.Name,
                    item => (IReadOnlyList<string>)(item.Value.ValueKind == JsonValueKind.Array
                        ? item.Value.EnumerateArray()
                            .Where(model => model.ValueKind == JsonValueKind.String)
                            .Select(model => model.GetString())
                            .Where(model => !string.IsNullOrWhiteSpace(model))
                            .Select(model => model!.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : Array.Empty<string>()),
                    StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }
}
