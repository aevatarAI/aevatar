using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage user's connected services in NyxID.</summary>
public sealed class NyxIdServicesTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdServicesTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_services";

    public string Description =>
        "Manage the user's connected services in NyxID. " +
        "Use 'list' to see all connected services, 'show' for details, " +
        "'create' to add a new service with an API key/token, " +
        "or 'delete' to remove a service connection.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "delete"],
              "description": "Action: 'list' all services, 'show' details, 'create' a new service, or 'delete' a service"
            },
            "id": {
              "type": "string",
              "description": "Service ID (required for 'show' and 'delete')"
            },
            "service_slug": {
              "type": "string",
              "description": "Catalog service slug (for 'create', e.g. 'telegram-bot', 'openai')"
            },
            "credential": {
              "type": "string",
              "description": "API key or token value (for 'create')"
            },
            "label": {
              "type": "string",
              "description": "User-friendly label for the service (for 'create')"
            },
            "endpoint_url": {
              "type": "string",
              "description": "Custom endpoint URL for self-hosted services (for 'create', e.g. OpenClaw gateway URL)"
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string action = "list";
        string? id = null;
        string? serviceSlug = null;
        string? credential = null;
        string? label = null;
        string? endpointUrl = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("service_slug", out var ss))
                serviceSlug = ss.GetString();
            if (doc.RootElement.TryGetProperty("credential", out var c))
                credential = c.GetString();
            if (doc.RootElement.TryGetProperty("label", out var l))
                label = l.GetString();
            if (doc.RootElement.TryGetProperty("endpoint_url", out var eu))
                endpointUrl = eu.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetServiceAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteServiceAsync(token, id, ct),
            "create" when !string.IsNullOrWhiteSpace(serviceSlug) && !string.IsNullOrWhiteSpace(credential) =>
                await CreateServiceAsync(token, serviceSlug, credential, label, endpointUrl, ct),

            "show" or "delete" => "Error: 'id' is required for this action.",
            "create" => "Error: 'service_slug' and 'credential' are required for create.",
            _ => await _client.ListServicesAsync(token, ct),
        };
    }

    private async Task<string> CreateServiceAsync(
        string token, string serviceSlug, string credential, string? label, string? endpointUrl,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["service_slug"] = serviceSlug,
            ["credential"] = credential,
            ["label"] = label ?? serviceSlug,
        };

        if (!string.IsNullOrWhiteSpace(endpointUrl))
            payload["endpoint_url"] = endpointUrl;

        return await _client.CreateServiceAsync(token, JsonSerializer.Serialize(payload), ct);
    }
}
