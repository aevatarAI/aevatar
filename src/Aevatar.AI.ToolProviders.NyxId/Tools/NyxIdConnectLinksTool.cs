using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdConnectLinksTool :
    INyxIdBuiltInTool,
    IAgentToolLiveResultMapper,
    IAgentToolNyxIdCredentialRequirementOwner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly NyxIdClosedActionParser<NyxIdConnectLinksAction> ActionParser = new(
    [
        new("create", NyxIdConnectLinksAction.Create, new(true, false, false)),
        new("get", NyxIdConnectLinksAction.Get, new(false, true, false)),
        new("cancel", NyxIdConnectLinksAction.Cancel, new(true, false, true)),
    ],
    defaultActionName: "create");

    private readonly NyxIdApiClient _client;
    private readonly IChannelConnectLinkContinuationPort? _continuationPort;

    public NyxIdConnectLinksTool(NyxIdApiClient client, IChannelConnectLinkContinuationPort? continuationPort = null)
    {
        _client = client;
        _continuationPort = continuationPort;
    }

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
            "scopes": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional additional OAuth scopes for create"
            },
            "endpoint_url": {
              "type": "string",
              "description": "Optional HTTP(S) service URL prefill for create"
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
              "description": "Connect-link id for get or cancel"
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public AgentToolNyxIdCredentialRequirement NyxIdCredentialRequirement =>
        AgentToolNyxIdCredentialRequirement.SenderBearer;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var receipt = NyxIdManagedToolReceiptFactory.TryCreate(
            callId,
            toolName,
            resultJson,
            NyxIdApiClient.TryRedactConnectUrl);
        return receipt;
    }

    public string? ResolveLiveResultJson(
        string argumentsJson,
        string terminalResultJson,
        AgentToolReceipt receipt) =>
        receipt.Status == AgentToolReceiptStatus.Success ? terminalResultJson ?? string.Empty : null;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        var context = AgentToolRequestContext.Current;
        var isChannel = context?.Channel.Continuation is not null ||
                        !string.IsNullOrWhiteSpace(context?.Channel.BotRegistrationId);
        if (string.IsNullOrWhiteSpace(token) && !isChannel)
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
        if (parsed.Action == NyxIdConnectLinksAction.Create && isChannel)
            return await CreateChannelAsync(context!, args, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"sender_authorization_unavailable"}""";

        return parsed.Action switch
        {
            NyxIdConnectLinksAction.Create => await CreateAsync(token, args, ct),
            NyxIdConnectLinksAction.Get when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetConnectLinkAsync(token, id, ct),
            NyxIdConnectLinksAction.Cancel when !string.IsNullOrWhiteSpace(id) =>
                await _client.CancelConnectLinkAsync(token, id, ct),
            NyxIdConnectLinksAction.Get or NyxIdConnectLinksAction.Cancel =>
                JsonSerializer.Serialize(new { error = "invalid_arguments", message = "'id' is required." }, SerializerOptions),
            _ => NyxIdClosedActionParser<NyxIdConnectLinksAction>.InvalidActionJson,
        };
    }

    private async Task<string> CreateChannelAsync(AgentToolExecutionContext context, ToolArgs args, CancellationToken ct)
    {
        if (_continuationPort is null)
            return """{"error":"channel_continuation_unavailable"}""";

        var catalogSlug = NormalizeOptional(args.Str("service_slug") ?? args.Str("slug"));
        if (catalogSlug is null)
            return """{"error":"invalid_arguments","message":"'service_slug' is required."}""";

        // Channel routing, callback URL, and creator identity are runtime-owned. In particular,
        // a model-provided callback_url or target_org_id cannot redirect this continuation.
        var result = await _continuationPort.CreateAsync(context, new ChannelConnectLinkCreateRequest(
            catalogSlug,
            NormalizeOptional(args.Str("label")),
            NormalizeOptional(args.Str("requested_by")),
            args.Int("expires_in")), ct).ConfigureAwait(false);
        return result.Accepted && result.ErrorCode is null
            ? JsonSerializer.Serialize(new
            {
                status = "accepted",
                callback_id = result.CallbackId,
                message = "The connection request was accepted. The link will arrive in this conversation after it has been created. Do not invent a link or claim that the service is connected.",
            }, SerializerOptions)
            : JsonSerializer.Serialize(new { error = result.ErrorCode }, SerializerOptions);
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

        var scopes = ParseScopes(args, out var scopesError);
        if (scopesError is not null)
        {
            return JsonSerializer.Serialize(
                new { error = "invalid_arguments", message = scopesError },
                SerializerOptions);
        }

        var endpointUrl = NormalizeOptional(args.Str("endpoint_url"));
        if (endpointUrl is not null && !IsHttpUrl(endpointUrl))
        {
            return JsonSerializer.Serialize(
                new { error = "invalid_arguments", message = "'endpoint_url' must be an absolute HTTP or HTTPS URL." },
                SerializerOptions);
        }

        var payload = new ConnectLinkCreatePayload
        {
            ServiceSlug = serviceSlug.Trim(),
            Label = NormalizeOptional(args.Str("label")),
            RequestedBy = NormalizeOptional(args.Str("requested_by")),
            Scopes = scopes,
            EndpointUrl = endpointUrl,
            CallbackUrl = NormalizeOptional(args.Str("callback_url")),
            ExpiresIn = args.Int("expires_in"),
            TargetOrgId = NormalizeOptional(args.Str("target_org_id") ?? args.Str("org")),
        };

        var result = await _client.CreateConnectLinkAsync(
            token,
            JsonSerializer.Serialize(payload, SerializerOptions),
            ct);

        return ValidateCreateResult(result) ?? result;
    }

    private static string? ValidateCreateResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) ||
                !root.TryGetProperty("connect_url", out var connectUrl) ||
                connectUrl.ValueKind != JsonValueKind.String ||
                !IsHttpUrl(connectUrl.GetString()) ||
                !root.TryGetProperty("expires_at", out var expiresAt) ||
                expiresAt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(expiresAt.GetString()))
            {
                return InvalidNyxIdResponseJson;
            }
        }
        catch (JsonException)
        {
            return InvalidNyxIdResponseJson;
        }

        return null;
    }

    private static IReadOnlyList<string>? ParseScopes(ToolArgs args, out string? error)
    {
        error = null;
        var element = args.Element("scopes");
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            return null;

        if (element.Value.ValueKind != JsonValueKind.Array)
        {
            error = "'scopes' must be an array of strings.";
            return null;
        }

        var scopes = new List<string>();
        foreach (var scopeElement in element.Value.EnumerateArray())
        {
            if (scopeElement.ValueKind != JsonValueKind.String)
            {
                error = "'scopes' must be an array of strings.";
                return null;
            }

            var scope = scopeElement.GetString()?.Trim();
            if (!string.IsNullOrEmpty(scope))
                scopes.Add(scope);
        }

        return scopes.Count == 0 ? null : scopes;
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string InvalidNyxIdResponseJson =
        """{"error":"invalid_nyxid_response","message":"The NyxID response was invalid."}""";

    private sealed class ConnectLinkCreatePayload
    {
        [JsonPropertyName("service_slug")]
        public string ServiceSlug { get; init; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("requested_by")]
        public string? RequestedBy { get; init; }

        [JsonPropertyName("scopes")]
        public IReadOnlyList<string>? Scopes { get; init; }

        [JsonPropertyName("endpoint_url")]
        public string? EndpointUrl { get; init; }

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
    Get,
    Cancel,
}
