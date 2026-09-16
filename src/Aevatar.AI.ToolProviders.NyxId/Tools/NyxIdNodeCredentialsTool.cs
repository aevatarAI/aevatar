using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads pending credential delivery metadata for a NyxID node.</summary>
public sealed class NyxIdNodeCredentialsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdNodeCredentialsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_node_credentials";

    public string Description =>
        "List pending credential delivery metadata for one NyxID node. Never returns key material.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list"], "default": "list" },
            "node_id": { "type": "string" },
            "include_history": { "type": "boolean" }
          },
          "required": ["node_id"],
          "additionalProperties": false
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool IsReadOnly => true;

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError ||
            !args.HasOnly("action", "node_id", "include_history") ||
            args.Has("action") && !args.IsString("action") ||
            !args.IsString("node_id") ||
            args.Has("include_history") && !args.IsBoolean("include_history") ||
            !string.Equals(args.Str("action") ?? "list", "list", StringComparison.Ordinal) ||
            args.Has("include_history") && !args.Bool("include_history").HasValue)
        {
            return InvalidArgumentsJson;
        }

        var nodeId = args.Str("node_id")?.Trim();
        if (string.IsNullOrWhiteSpace(nodeId))
            return "{\"error\":\"'node_id' is required for list\"}";

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var response = await _client.ListPendingNodeCredentialsAsync(
            token,
            nodeId,
            args.Bool("include_history"),
            ct).ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectPendingNodeCredentials(response);
    }
}
