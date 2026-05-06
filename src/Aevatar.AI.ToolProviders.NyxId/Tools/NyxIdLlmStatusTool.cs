using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to check NyxID-confirmed LLM-capable services and models.</summary>
public sealed class NyxIdLlmStatusTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdLlmStatusTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_llm_status";

    public string Description =>
        "Check NyxID-confirmed LLM-capable services, routes, and models.";

    public string ParametersSchema => """{"type":"object","properties":{}}""";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        return await _client.GetLlmServicesAsync(token, ct);
    }
}
