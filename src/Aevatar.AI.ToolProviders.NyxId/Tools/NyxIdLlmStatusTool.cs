using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to check available LLM providers and models via NyxID gateway.</summary>
public sealed class NyxIdLlmStatusTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdLlmStatusTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_llm_status";

    public string Description =>
        "Check available LLM providers and models through the NyxID LLM gateway. " +
        "Shows which providers are configured and what models are available.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        return await _client.GetLlmStatusAsync(token, ct);
    }
}
