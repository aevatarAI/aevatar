using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdConnectLinksTool : INyxIdBuiltInTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly NyxIdClosedActionParser<NyxIdConnectLinksAction> ActionParser = new(
    [
        new("create", NyxIdConnectLinksAction.Create, new(true, false, false)),
        new("show", NyxIdConnectLinksAction.Show, new(false, true, false)),
        new("cancel", NyxIdConnectLinksAction.Cancel, new(true, false, true)),
    ],
    defaultActionName: "create");

    private readonly NyxIdApiClient _client;

    public NyxIdConnectLinksTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_connect_links";

    public string Description =>
        "Create, inspect, or cancel single-use NyxID service connect links. " +
        "Use this when a channel/default-skill flow needs a browser URL for the user to connect a service.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
              "description": "Action to perform (default: create)"
            },
            "service_slug": {
              "type": "string",
              "description": "NyxID catalog service slug for create"
            },
            "label": {
              "type": "string",
              "description": "Optional connected-service label for create"
            },
            "requested_by": {
              "type": "string",
              "description": "Optional human-readable requester label for create"
            },
            "callback_url": {
              "type": "string",
              "description": "Optional absolute callback URL for create"
            },
            "expires_in": {
              "type": "integer",
              "description": "Optional link lifetime in seconds for create"
            },
            "target_org_id": {
              "type": "string",
              "description": "Optional NyxID organization id that should own the connected service"
            },
            "id": {
              "type": "string",
              "description": "Connect-link id for show or cancel"
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        NyxIdManagedToolReceiptFactory.TryCreate(
            callId,
            toolName,
            resultJson,
            NyxIdApiClient.TryRedactConnectUrl);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return """{"error":"No NyxID access token available. User must be authenticated or the channel registration must provide an agent key."}""";
        }

        var parsed = ActionParser.Parse(argumentsJson);
        if (!parsed.IsValid)
            return NyxIdClosedActionParser<NyxIdConnectLinksAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return JsonSerializer.Serialize(new { error = "invalid_arguments", message = args.ParseError }, SerializerOptions);

        var id = args.Str("id");
        return parsed.Action switch
        {
            NyxIdConnectLinksAction.Create => await CreateAsync(token, args, ct),
            NyxIdConnectLinksAction.Show when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetConnectLinkAsync(token, id, ct),
            NyxIdConnectLinksAction.Cancel when !string.IsNullOrWhiteSpace(id) =>
                await _client.CancelConnectLinkAsync(token, id, ct),
            NyxIdConnectLinksAction.Show or NyxIdConnectLinksAction.Cancel =>
                JsonSerializer.Serialize(new { error = "invalid_arguments", message = "'id' is required." }, SerializerOptions),
            _ => NyxIdClosedActionParser<NyxIdConnectLinksAction>.InvalidActionJson,
        };
    }

    private async Task<string> CreateAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var serviceSlug = args.Str("service_slug") ?? args.Str("slug");
        if (string.IsNullOrWhiteSpace(serviceSlug))
        {
            return JsonSerializer.Serialize(
                new { error = "invalid_arguments", message = "'service_slug' is required." },
                SerializerOptions);
        }

        var payload = new ConnectLinkCreatePayload
        {
            ServiceSlug = serviceSlug.Trim(),
            Label = NormalizeOptional(args.Str("label")),
            RequestedBy = NormalizeOptional(args.Str("requested_by")),
            CallbackUrl = NormalizeOptional(args.Str("callback_url")),
            ExpiresIn = args.Int("expires_in"),
            TargetOrgId = NormalizeOptional(args.Str("target_org_id") ?? args.Str("org")),
        };

        return await _client.CreateConnectLinkAsync(
            token,
            JsonSerializer.Serialize(payload, SerializerOptions),
            ct);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ConnectLinkCreatePayload
    {
        [JsonPropertyName("service_slug")]
        public string ServiceSlug { get; init; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("requested_by")]
        public string? RequestedBy { get; init; }

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        [JsonPropertyName("target_org_id")]
        public string? TargetOrgId { get; init; }
    }
}

internal enum NyxIdConnectLinksAction
{
    Create,
    Show,
    Cancel,
}
