using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Reads safe durable-operation grant receipts for one NyxID API key.</summary>
public sealed class NyxIdDurableGrantsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string MissingTokenJson =
        "{\"error\":\"No source-readable NyxID user bearer is available.\"}";
    private const string InvalidArgumentsJson = "{\"error\":\"invalid_arguments\"}";

    private readonly NyxIdApiClient _client;

    public NyxIdDurableGrantsTool(NyxIdApiClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Name => "nyxid_durable_grants";

    public string Description =>
        "List safe durable-operation grant receipts for one NyxID API key. Never returns key material or constrained values.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key_id": { "type": "string" },
            "include_revoked": { "type": "boolean", "default": false }
          },
          "required": ["key_id"],
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
            !args.HasOnly("key_id", "include_revoked") ||
            !args.IsString("key_id") ||
            args.Has("include_revoked") && !args.IsBoolean("include_revoked") ||
            args.Has("include_revoked") && !args.Bool("include_revoked").HasValue)
        {
            return InvalidArgumentsJson;
        }

        var rawApiKeyId = args.Str("key_id");
        var apiKeyId = rawApiKeyId?.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return "{\"error\":\"'key_id' is required\"}";
        if (!string.Equals(rawApiKeyId, apiKeyId, StringComparison.Ordinal) ||
            apiKeyId.Length > 256 ||
            apiKeyId.Any(char.IsControl))
        {
            return InvalidArgumentsJson;
        }

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(token))
            return MissingTokenJson;

        var response = await _client.ListDurableGrantsAsync(
            token,
            apiKeyId,
            args.Bool("include_revoked") ?? false,
            ct).ConfigureAwait(false);
        return NyxIdAssistantReadResponseProjector.ProjectDurableGrants(response, apiKeyId);
    }
}
