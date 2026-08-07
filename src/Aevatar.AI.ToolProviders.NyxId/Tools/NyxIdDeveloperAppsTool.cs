using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads a caller-owned NyxID developer app catalog without exposing app secrets or URLs.</summary>
public sealed class NyxIdDeveloperAppsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdDeveloperAppsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_developer_apps";

    public string Description =>
        "List developer apps or show one developer app from NyxID. Never returns client secrets or callback URLs.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "show"], "default": "list" },
            "client_id": { "type": "string" },
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
            !args.HasOnly("action", "client_id", "org_id") ||
            args.Has("action") && !args.IsString("action") ||
            args.Has("client_id") && !args.IsString("client_id") ||
            args.Has("org_id") && !args.IsString("org_id"))
        {
            return InvalidArgumentsJson;
        }

        var action = args.Str("action") ?? "list";
        if (action is not ("list" or "show") ||
            action == "list" && args.Has("client_id") ||
            action == "show" && args.Has("org_id") ||
            args.Has("org_id") && string.IsNullOrWhiteSpace(args.Str("org_id")))
        {
            return InvalidArgumentsJson;
        }

        var clientId = args.Str("client_id");
        if (action == "show" && string.IsNullOrWhiteSpace(clientId))
            return "{\"error\":\"'client_id' is required for show\"}";

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var response = action == "show"
            ? await _client.GetDeveloperOAuthClientAsync(token, clientId!, ct).ConfigureAwait(false)
            : await _client.ListDeveloperOAuthClientsAsync(token, args.Str("org_id"), ct)
                .ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectDeveloperOAuthClients(
            response,
            action == "list");
    }
}
