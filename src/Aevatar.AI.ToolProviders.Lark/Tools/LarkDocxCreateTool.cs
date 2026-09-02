using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkDocxCreateTool : AgentToolBase<LarkDocxCreateTool.Parameters>
{
    private const string ChannelDeliveryAddressIdMetadataKey = "channel.delivery.address_id";
    private const string ChannelDeliveryAddressTypeMetadataKey = "channel.delivery.address_type";

    private static readonly HashSet<string> AllowedVisibility =
    [
        "readable",
        "editable",
    ];

    private static readonly HashSet<string> AllowedReceiveIdTypes =
    [
        "chat_id",
        "open_id",
        "union_id",
        "user_id",
    ];

    private readonly ILarkNyxClient _client;

    public LarkDocxCreateTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_docx_create";

    public override string Description =>
        "Create a Lark cloud document through Nyx-backed bot transport, append the complete markdown text, apply sharing visibility, and return the document link.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        if (!TryBuildRequest(parameters, out var request, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        return await CreateAppendAndShareAsync(request, ct);
    }

    private static bool TryBuildRequest(
        Parameters parameters,
        out ValidatedDocxCreateRequest request,
        out string? error)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        request = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "No NyxID access token available. User must be authenticated.";
            return false;
        }

        var title = parameters.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            error = "title is required.";
            return false;
        }

        if (title.Length > 200)
        {
            error = "title exceeds the maximum of 200 characters.";
            return false;
        }

        var markdownText = parameters.MarkdownText;
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            error = "markdown_text is required.";
            return false;
        }

        if (!TryNormalizeVisibility(parameters.Visibility, out var visibility, out error))
            return false;

        if (!TryResolveReceiveTarget(parameters, out var receiveId, out var receiveIdType, out error))
            return false;

        request = new ValidatedDocxCreateRequest(token, title, markdownText, visibility, receiveId, receiveIdType);
        error = null;
        return true;
    }

    private async Task<string> CreateAppendAndShareAsync(ValidatedDocxCreateRequest request, CancellationToken ct)
    {
        var createResponse = await _client.CreateDocxDocumentAsync(
            request.Token,
            new LarkDocxCreateRequest(request.Title),
            ct);
        if (LarkProxyResponseParser.TryParseError(createResponse, out var createError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = createError });

        var createResult = LarkProxyResponseParser.ParseDocxCreateSuccess(createResponse);
        if (string.IsNullOrWhiteSpace(createResult.DocumentToken) ||
            string.IsNullOrWhiteSpace(createResult.DocumentId))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "docx_create_missing_token",
            });
        }

        var appendResponse = await _client.AppendDocxTextBlocksAsync(
            request.Token,
            new LarkDocxAppendBlocksRequest(createResult.DocumentId, request.MarkdownText),
            ct);
        if (LarkProxyResponseParser.TryParseError(appendResponse, out var appendError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                document_token = createResult.DocumentToken,
                document_url = createResult.DocumentUrl,
                error = appendError,
            });
        }

        var permissionResponse = await _client.SetDrivePermissionAsync(
            request.Token,
            new LarkDrivePermissionRequest(
                createResult.DocumentToken,
                request.Visibility,
                request.ReceiveId,
                request.ReceiveIdType),
            ct);
        if (LarkProxyResponseParser.TryParseError(permissionResponse, out var permissionError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                document_token = createResult.DocumentToken,
                document_url = createResult.DocumentUrl,
                visibility_applied = false,
                error = permissionError,
            });
        }

        var permissionResult = LarkProxyResponseParser.ParseDrivePermissionSuccess(permissionResponse);
        var documentUrl = createResult.DocumentUrl ?? permissionResult.ShareUrl;
        if (string.IsNullOrWhiteSpace(documentUrl))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                document_token = createResult.DocumentToken,
                visibility_applied = true,
                error = "docx_create_missing_url",
            });
        }

        // Additionally grant the requester (channel sender) full access while KEEPING the tenant link
        // applied above, so a doc shared to a group stays group-readable AND the requester gets edit
        // access. Sender-grant failure is reported but does NOT fail the op (the tenant link is live).
        var senderGranted = false;
        string? senderGrantError = null;
        var sender = AgentToolRequestContext.ChannelSenderId?.Trim();
        if (!string.IsNullOrWhiteSpace(sender))
        {
            var senderGrantResponse = await _client.GrantResourceMemberAsync(
                request.Token,
                new LarkResourceMemberGrantRequest(
                    Token: createResult.DocumentToken,
                    ObjType: "docx",
                    MemberId: sender,
                    MemberType: "openid",
                    Perm: "full_access"),
                ct);
            if (LarkProxyResponseParser.TryParseError(senderGrantResponse, out var senderError))
                senderGrantError = senderError;
            else
                senderGranted = true;
        }

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            document_token = createResult.DocumentToken,
            document_url = documentUrl,
            visibility_applied = true,
            visibility = request.Visibility == LarkDocxVisibility.Editable ? "editable" : "readable",
            granted_to_sender = senderGranted,
            sender_grant_error = senderGrantError,
        });
    }

    private static bool TryNormalizeVisibility(
        string? raw,
        out LarkDocxVisibility visibility,
        out string? error)
    {
        var normalized = string.IsNullOrWhiteSpace(raw)
            ? "readable"
            : raw.Trim().ToLowerInvariant();
        if (!AllowedVisibility.Contains(normalized))
        {
            visibility = LarkDocxVisibility.Readable;
            error = "visibility must be one of: readable, editable";
            return false;
        }

        visibility = normalized == "editable" ? LarkDocxVisibility.Editable : LarkDocxVisibility.Readable;
        error = null;
        return true;
    }

    private static bool TryResolveReceiveTarget(
        Parameters parameters,
        out string? receiveId,
        out string? receiveIdType,
        out string? error)
    {
        receiveId = parameters.ReceiveId?.Trim();
        receiveIdType = parameters.ReceiveIdType?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(receiveId))
            receiveId = AgentToolRequestContext.TryGetExternalMetadata(ChannelDeliveryAddressIdMetadataKey)?.Trim();
        if (string.IsNullOrWhiteSpace(receiveIdType))
            receiveIdType = AgentToolRequestContext.TryGetExternalMetadata(ChannelDeliveryAddressTypeMetadataKey)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(receiveId) && string.IsNullOrWhiteSpace(receiveIdType))
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(receiveId) || string.IsNullOrWhiteSpace(receiveIdType))
        {
            error = "receive_id and receive_id_type must be provided together.";
            return false;
        }

        if (!AllowedReceiveIdTypes.Contains(receiveIdType))
        {
            error = "receive_id_type must be one of: chat_id, open_id, union_id, user_id";
            return false;
        }

        error = null;
        return true;
    }

    private readonly record struct ValidatedDocxCreateRequest(
        string Token,
        string Title,
        string MarkdownText,
        LarkDocxVisibility Visibility,
        string? ReceiveId,
        string? ReceiveIdType);

    public sealed class Parameters
    {
        public string? Title { get; set; }
        public string? MarkdownText { get; set; }
        public string? Visibility { get; set; }
        public string? ReceiveId { get; set; }
        public string? ReceiveIdType { get; set; }
    }
}
