using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads the caller's OAuth broker bindings through the user-scoped list contract.</summary>
public sealed class NyxIdOAuthBindingsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdOAuthBindingsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_oauth_bindings";

    public string Description =>
        "List OAuth broker bindings or show one binding by its exact 64-character lowercase binding hash.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "show"], "default": "list" },
            "binding_hash": { "type": "string", "pattern": "^[0-9a-f]{64}$" }
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
            !args.HasOnly("action", "binding_hash") ||
            args.Has("action") && !args.IsString("action") ||
            args.Has("binding_hash") && !args.IsString("binding_hash"))
        {
            return InvalidArgumentsJson;
        }

        var action = args.Str("action") ?? "list";
        if (action is not ("list" or "show") ||
            action == "list" && args.Has("binding_hash"))
        {
            return InvalidArgumentsJson;
        }

        var bindingHash = args.Str("binding_hash");
        if (action == "show" && string.IsNullOrEmpty(bindingHash))
            return "{\"error\":\"'binding_hash' is required for show\"}";
        if (action == "show" &&
            !NyxIdAssistantReadResponseProjector.IsExactOAuthBindingSelector(bindingHash!))
        {
            return InvalidArgumentsJson;
        }

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var response = await _client.ListOAuthBrokerBindingsAsync(token, ct).ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectOAuthBindings(
            response,
            action == "show" ? bindingHash : null);
    }
}
