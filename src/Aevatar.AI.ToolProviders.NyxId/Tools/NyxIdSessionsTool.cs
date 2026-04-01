using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to list NyxID active sessions.</summary>
public sealed class NyxIdSessionsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdSessionsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_sessions";

    public string Description =>
        "List the user's active NyxID sessions, showing device info, IP address, and expiration.";

    public string ParametersSchema => """{"type":"object","properties":{}}""";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        return await _client.ListSessionsAsync(token, ct);
    }
}
