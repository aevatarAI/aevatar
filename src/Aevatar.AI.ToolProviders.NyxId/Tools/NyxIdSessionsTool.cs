using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to list NyxID active sessions.</summary>
public sealed class NyxIdSessionsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdSessionsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_sessions";

    public string Description =>
        "List the user's active NyxID sessions, showing device info, IP address, and expiration.";

    public string ParametersSchema =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError || !args.HasOnly())
            return InvalidArgumentsJson;

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        return await _client.ListSessionsAsync(token, ct);
    }
}
