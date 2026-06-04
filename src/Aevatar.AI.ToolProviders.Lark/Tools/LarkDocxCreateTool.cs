using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkDocxCreateTool : AgentToolBase<LarkDocxCreateTool.Parameters>
{
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
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var title = parameters.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "title is required." });
        if (title.Length > 200)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "title exceeds the maximum of 200 characters." });

        var markdownText = parameters.MarkdownText;
        if (string.IsNullOrWhiteSpace(markdownText))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "markdown_text is required." });

        if (!TryNormalizeVisibility(parameters.Visibility, out var visibility, out var visibilityError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = visibilityError });

        if (!TryResolveReceiveTarget(parameters, out var receiveId, out var receiveIdType, out var targetError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = targetError });

        var createResponse = await _client.CreateDocxDocumentAsync(
            token,
            new LarkDocxCreateRequest(title),
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
            token,
            new LarkDocxAppendBlocksRequest(createResult.DocumentId, markdownText),
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
            token,
            new LarkDrivePermissionRequest(
                createResult.DocumentToken,
                visibility,
                receiveId,
                receiveIdType),
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

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            document_token = createResult.DocumentToken,
            document_url = documentUrl,
            visibility_applied = true,
            visibility = visibility == LarkDocxVisibility.Editable ? "editable" : "readable",
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
            receiveId = AgentToolRequestContext.TryGetExternalMetadata("channel.lark.receive_id")?.Trim();
        if (string.IsNullOrWhiteSpace(receiveIdType))
            receiveIdType = AgentToolRequestContext.TryGetExternalMetadata("channel.lark.receive_id_type")?.Trim().ToLowerInvariant();

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

    public sealed class Parameters
    {
        public string? Title { get; set; }
        public string? MarkdownText { get; set; }
        public string? Visibility { get; set; }
        public string? ReceiveId { get; set; }
        public string? ReceiveIdType { get; set; }
    }
}
