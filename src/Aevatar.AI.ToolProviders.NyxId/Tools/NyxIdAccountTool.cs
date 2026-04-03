using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to view current NyxID user profile and account status.</summary>
public sealed class NyxIdAccountTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdAccountTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_account";

    public string Description =>
        "Get the current NyxID user's profile information including name, email, and account status.";

    public string ParametersSchema => """{"type":"object","properties":{}}""";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        return await _client.GetCurrentUserAsync(token, ct);
    }
}
