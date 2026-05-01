using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class NyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    private readonly NyxIdApiClient _nyxClient;

    public NyxIdLlmServiceCatalogClient(NyxIdApiClient nyxClient)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var response = await _nyxClient.GetLlmServicesAsync(accessToken, ct).ConfigureAwait(false);
        return ParseServicesResult(response);
    }

    public async Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        var result = await GetServicesAsync(query, accessToken, ct).ConfigureAwait(false);
        return result.SetupHint ?? new UserLlmSetupHint(string.Empty, []);
    }

    public async Task<NyxIdLlmService> ProvisionAsync(
        UserLlmSelectionContext context,
        string accessToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionEndpointId);

        var response = await _nyxClient
            .ProvisionLlmServiceAsync(accessToken, provisionEndpointId, ct)
            .ConfigureAwait(false);
        return ParseProvisionedService(response);
    }

    internal static NyxIdLlmServicesResult ParseServicesResult(string response)
    {
        using var document = ParseSuccessDocument(response);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new NyxIdLlmServicesResult(ParseServicesArray(root), null);
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM services response must be a JSON object or array.");

        var servicesElement = root.TryGetProperty("services", out var servicesProp)
            ? servicesProp
            : root.TryGetProperty("items", out var itemsProp)
                ? itemsProp
                : default;

        var services = servicesElement.ValueKind == JsonValueKind.Array
            ? ParseServicesArray(servicesElement)
            : [];
        var setupHint = root.TryGetProperty("setup_hint", out var hintProp)
            ? ParseSetupHint(hintProp)
            : null;

        return new NyxIdLlmServicesResult(services, setupHint);
    }

    internal static NyxIdLlmService ParseProvisionedService(string response)
    {
        using var document = ParseSuccessDocument(response);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("service", out var serviceProp))
        {
            return ParseService(serviceProp);
        }

        return ParseService(root);
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

        var userServiceId = ReadRequiredString(element, "user_service_id");
        var serviceSlug = ReadRequiredString(element, "service_slug");
        var displayName = ReadOptionalString(element, "display_name")
            ?? ReadOptionalString(element, "service_name")
            ?? serviceSlug;
        var routeValue = ReadRequiredString(element, "route_value");
        var models = ReadStringArray(element, "models");

        return new NyxIdLlmService(
            UserServiceId: userServiceId,
            ServiceSlug: serviceSlug,
            DisplayName: displayName,
            RouteValue: routeValue,
            DefaultModel: ReadOptionalString(element, "default_model"),
            Models: models,
            Status: ReadOptionalString(element, "status") ?? "unknown",
            Source: ReadOptionalString(element, "source") ?? "user",
            Allowed: ReadOptionalBool(element, "allowed") ?? false,
            Description: ReadOptionalString(element, "description"));
    }

    private static UserLlmSetupHint? ParseSetupHint(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM setup_hint must be a JSON object.");

        var setupUrl = ReadOptionalString(element, "setup_url") ?? string.Empty;
        var presets = new List<UserLlmPreset>();
        if (element.TryGetProperty("presets", out var presetsProp) &&
            presetsProp.ValueKind == JsonValueKind.Array)
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

        return new UserLlmPreset(
            Id: ReadRequiredString(element, "id"),
            Title: ReadOptionalString(element, "title") ?? ReadRequiredString(element, "id"),
            Description: ReadOptionalString(element, "description") ?? string.Empty,
            Activation: ParseActivation(element));
    }

    private static UserLlmPresetActivation ParseActivation(JsonElement preset)
    {
        var activationElement = preset.TryGetProperty("activation", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : preset;

        var type = ReadOptionalString(activationElement, "type")
            ?? ReadOptionalString(activationElement, "kind")
            ?? ReadOptionalString(preset, "activation_type")
            ?? string.Empty;
        var normalized = type.Trim().ToLowerInvariant();

        return normalized switch
        {
            "use_existing_service" or "use-existing-service" or "existing" => new UseExistingService(
                ServiceId: ReadRequiredString(activationElement, "service_id"),
                RouteValue: ReadRequiredString(activationElement, "route_value"),
                DefaultModel: ReadOptionalString(activationElement, "default_model")),
            "provision_then_use" or "provision-then-use" or "provision" => new ProvisionThenUse(
                ReadRequiredString(activationElement, "provision_endpoint_id")),
            _ => throw new InvalidOperationException($"Unsupported NyxID LLM preset activation type '{type}'."),
        };
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"NyxID LLM response is missing required field '{propertyName}'.");
        return value.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadOptionalBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static int? TryReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
            return [];

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
