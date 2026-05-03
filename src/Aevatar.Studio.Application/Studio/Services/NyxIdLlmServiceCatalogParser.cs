using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public static class NyxIdLlmServiceCatalogParser
{
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
