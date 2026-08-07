using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to view current NyxID user profile and account status.</summary>
public sealed class NyxIdAccountTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdAccountTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_account";

    public string Description =>
        "Get the current NyxID user's profile information including name, email, and account status.";

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

        return await _client.GetCurrentUserAsync(token, ct);
    }
}
