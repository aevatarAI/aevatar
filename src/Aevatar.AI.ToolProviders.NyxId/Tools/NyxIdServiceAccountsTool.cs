using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads NyxID service-account metadata without exposing names or secret material.</summary>
public sealed class NyxIdServiceAccountsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    internal const int AssistantPageSize = 20;
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdServiceAccountsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_service_accounts";

    public string Description =>
        "List service accounts or show one service account from NyxID. Returns identifiers, access scope, status, and timestamps only.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "show"], "default": "list" },
            "sa_id": { "type": "string" },
            "org_id": { "type": "string" },
            "page": { "type": "integer", "minimum": 1, "default": 1 }
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
        if (!HasValidShape(args))
            return InvalidArgumentsJson;

        var action = args.Str("action") ?? "list";
        if (!HasValidActionArguments(args, action))
            return InvalidArgumentsJson;

        var serviceAccountId = args.Str("sa_id");
        if (action == "show" && string.IsNullOrWhiteSpace(serviceAccountId))
            return "{\"error\":\"'sa_id' is required for show\"}";

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var page = args.Int("page") ?? 1;
        var response = action == "show"
            ? await _client.GetServiceAccountAsync(token, serviceAccountId!, ct).ConfigureAwait(false)
            : await _client.ListServiceAccountsAsync(
                    token,
                    args.Str("org_id"),
                    page,
                    AssistantPageSize,
                    ct)
                .ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectServiceAccounts(
            response,
            action == "list",
            page);
    }

    private static bool HasValidShape(ToolArgs args) =>
        !args.HasParseError &&
        args.HasOnly("action", "sa_id", "org_id", "page") &&
        (!args.Has("action") || args.IsString("action")) &&
        (!args.Has("sa_id") || args.IsString("sa_id")) &&
        (!args.Has("org_id") || args.IsString("org_id")) &&
        (!args.Has("page") || args.Element("page")?.ValueKind == JsonValueKind.Number);

    private static bool HasValidActionArguments(ToolArgs args, string action) =>
        action is "list" or "show" &&
        (action != "list" || !args.Has("sa_id")) &&
        (action != "show" || !args.Has("org_id") && !args.Has("page")) &&
        (!args.Has("org_id") || !string.IsNullOrWhiteSpace(args.Str("org_id"))) &&
        (!args.Has("page") || args.Int("page") is > 0);
}
