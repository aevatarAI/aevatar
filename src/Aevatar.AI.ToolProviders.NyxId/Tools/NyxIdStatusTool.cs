using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to show NyxID account overview (user, services, API keys, nodes).</summary>
public sealed class NyxIdStatusTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdStatusTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_status";

    public string Description =>
        "Get a comprehensive account overview combining user profile, connected services, API keys, and nodes in one call.";

    public string ParametersSchema => """{"type":"object","properties":{}}""";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var userTask = _client.GetCurrentUserAsync(token, ct);
        var servicesTask = _client.ListServicesAsync(token, ct);
        var apiKeysTask = _client.ListApiKeysAsync(token, ct);
        var nodesTask = _client.ListNodesAsync(token, ct);

        await Task.WhenAll(userTask, servicesTask, apiKeysTask, nodesTask);

        return JsonSerializer.Serialize(new
        {
            user = JsonDocument.Parse(await userTask).RootElement,
            services = JsonDocument.Parse(await servicesTask).RootElement,
            api_keys = JsonDocument.Parse(await apiKeysTask).RootElement,
            nodes = JsonDocument.Parse(await nodesTask).RootElement,
        });
    }
}
