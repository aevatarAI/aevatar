// Resolve provider instance name to endpoint/model/apiKey (API key never exposed in public DTO).

using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.Tools.Config;

static class LLMProviderResolver
{
    public static ResolvedProvider Resolve(ISecretsStore secrets, string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            if (secrets.TryGet("LLMProviders:Default", out var def) && !string.IsNullOrWhiteSpace(def))
                name = def.Trim();
            else
                name = "default";
        }

        var providerTypeSource = "missing";
        var providerType = string.Empty;
        var providerTypePath = $"LLMProviders:Providers:{name}:ProviderType";
        if (secrets.TryGet(providerTypePath, out var ptFromSecrets) && !string.IsNullOrWhiteSpace(ptFromSecrets))
        {
            providerTypeSource = "secret";
            providerType = ptFromSecrets.Trim();
        }
        else if (ProviderProfiles.TryInferProviderTypeFromInstanceName(name, out var inferred))
        {
            providerTypeSource = "inferred";
            providerType = inferred;
        }
        else
            providerType = name;

        var profile = ProviderProfiles.Get(providerType);
        var apiKeyPath = $"LLMProviders:Providers:{name}:ApiKey";
        var endpointPath = $"LLMProviders:Providers:{name}:Endpoint";
        var modelPath = $"LLMProviders:Providers:{name}:Model";

        var apiKeyConfigured = secrets.TryGet(apiKeyPath, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey);
        apiKey = apiKeyConfigured ? apiKey : string.Empty;

        var endpointSource = "missing";
        var endpoint = string.Empty;
        if (secrets.TryGet(endpointPath, out var endpointFromSecrets) && !string.IsNullOrWhiteSpace(endpointFromSecrets))
        {
            endpointSource = "secret";
            endpoint = endpointFromSecrets.Trim();
        }
        else if (string.Equals(providerType, "nyxid", StringComparison.OrdinalIgnoreCase))
        {
            var nyxIdEndpoint = TryResolveNyxIdGatewayEndpointFromConfigJson();
            if (!string.IsNullOrWhiteSpace(nyxIdEndpoint))
            {
                endpointSource = "config";
                endpoint = nyxIdEndpoint;
            }
        }
        else if (!string.IsNullOrWhiteSpace(profile.DefaultEndpoint))
        {
            endpointSource = "default";
            endpoint = profile.DefaultEndpoint.Trim();
        }

        var modelSource = "missing";
        var model = string.Empty;
        if (secrets.TryGet(modelPath, out var modelFromSecrets) && !string.IsNullOrWhiteSpace(modelFromSecrets))
        {
            modelSource = "secret";
            model = modelFromSecrets.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(profile.DefaultModel))
        {
            modelSource = "default";
            model = profile.DefaultModel.Trim();
        }

        var pub = new ResolvedProviderPublic(
            ProviderName: name,
            ProviderType: providerType,
            ProviderTypeSource: providerTypeSource,
            DisplayName: profile.DisplayName,
            Kind: profile.Kind.ToString(),
            ApiKeyConfigured: apiKeyConfigured,
            Endpoint: endpoint,
            EndpointSource: endpointSource,
            Model: model,
            ModelSource: modelSource);

        return new ResolvedProvider(
            ProviderName: name,
            ProviderType: providerType,
            ProviderTypeSource: providerTypeSource,
            DisplayName: profile.DisplayName,
            Kind: profile.Kind,
            Endpoint: endpoint,
            EndpointSource: endpointSource,
            Model: model,
            ModelSource: modelSource,
            ApiKeyConfigured: apiKeyConfigured,
            ApiKey: apiKey ?? string.Empty,
            Public: pub);
    }

    private static string? TryResolveNyxIdGatewayEndpointFromConfigJson()
    {
        try
        {
            if (!File.Exists(AevatarPaths.ConfigJson))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(AevatarPaths.ConfigJson));
            if (TryGetString(doc.RootElement, out var configuredEndpoint, "Aevatar", "NyxId", "GatewayEndpoint") &&
                !string.IsNullOrWhiteSpace(configuredEndpoint))
                return NormalizeNyxIdGatewayEndpoint(configuredEndpoint);

            if (TryGetString(doc.RootElement, out configuredEndpoint, "Cli", "App", "NyxId", "GatewayEndpoint") &&
                !string.IsNullOrWhiteSpace(configuredEndpoint))
                return NormalizeNyxIdGatewayEndpoint(configuredEndpoint);

            if (!TryGetString(doc.RootElement, out var authority, "Cli", "App", "NyxId", "Authority") &&
                !TryGetString(doc.RootElement, out authority, "Aevatar", "NyxId", "Authority"))
                return null;

            return NormalizeNyxIdGatewayEndpoint(authority);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeNyxIdGatewayEndpoint(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        var absolute = uri.ToString().TrimEnd('/');
        if (absolute.EndsWith("/api/v1/llm/gateway/v1", StringComparison.OrdinalIgnoreCase))
            return absolute;

        return $"{absolute}/api/v1/llm/gateway/v1";
    }

    private static bool TryGetString(JsonElement element, out string value, params string[] path)
    {
        value = string.Empty;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !TryGetProperty(current, segment, out current))
                return false;
        }

        if (current.ValueKind != JsonValueKind.String)
            return false;

        value = current.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
