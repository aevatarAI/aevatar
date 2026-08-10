using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads NyxID service pool metadata.</summary>
public sealed class NyxIdServicePoolsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdServicePoolsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_service_pools";

    public string Description => "List service pools or show one service pool from NyxID.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "show"], "default": "list" },
            "id": { "type": "string" },
            "org_id": { "type": "string" }
          },
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
            !args.HasOnly("action", "id", "org_id") ||
            args.Has("action") && !args.IsString("action") ||
            args.Has("id") && !args.IsString("id") ||
            args.Has("org_id") && !args.IsString("org_id"))
        {
            return InvalidArgumentsJson;
        }

        var action = args.Str("action")?.Trim() ?? "list";
        if (action is not ("list" or "show") ||
            action == "list" && args.Has("id") ||
            action == "show" && args.Has("org_id"))
        {
            return InvalidArgumentsJson;
        }

        var id = args.Str("id")?.Trim();
        if (action == "show" && string.IsNullOrWhiteSpace(id))
            return "{\"error\":\"'id' is required for show\"}";

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var response = action == "show"
            ? await _client.GetServicePoolAsync(token, id!, ct).ConfigureAwait(false)
            : await _client.ListServicePoolsAsync(token, args.Str("org_id"), ct).ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectServicePools(response, action == "list");
    }
}
