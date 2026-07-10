using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkNyxClient : ILarkNyxClient
{
    internal const int MaxDocxBlockTextLength = 2_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LarkToolOptions _options;
    private readonly NyxIdApiClient _nyxClient;

    public LarkNyxClient(LarkToolOptions options, NyxIdApiClient nyxClient)
    {
        _options = options;
        _nyxClient = nyxClient;
    }

    public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
    {
        var path =
            $"open-apis/im/v1/messages?receive_id_type={Uri.EscapeDataString(request.TargetType)}";
        var body = new Dictionary<string, object?>
        {
            ["receive_id"] = request.TargetId,
            ["msg_type"] = request.MessageType,
            ["content"] = request.ContentJson,
        };

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            path,
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["msg_type"] = request.MessageType,
            ["content"] = request.ContentJson,
        };

        if (request.ReplyInThread)
            body["reply_in_thread"] = true;
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}/reply",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["reaction_type"] = new Dictionary<string, object?>
            {
                ["emoji_type"] = request.EmojiType,
            },
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}/reactions",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct)
    {
        var queryParts = new List<string>
        {
            $"page_size={request.PageSize}",
        };
        if (!string.IsNullOrWhiteSpace(request.EmojiType))
            queryParts.Add($"reaction_type={Uri.EscapeDataString(request.EmojiType.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.PageToken))
            queryParts.Add($"page_token={Uri.EscapeDataString(request.PageToken.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.UserIdType))
            queryParts.Add($"user_id_type={Uri.EscapeDataString(request.UserIdType.Trim())}");

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}/reactions?{string.Join("&", queryParts)}",
            "GET",
            body: null,
            extraHeaders: null,
            ct);
    }

    public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct)
    {
        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}/reactions/{Uri.EscapeDataString(request.ReactionId)}",
            "DELETE",
            body: null,
            extraHeaders: null,
            ct);
    }

    public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct)
    {
        var parts = new List<string>
        {
            "card_msg_content_type=raw_card_content",
        };
        foreach (var messageId in request.MessageIds)
            parts.Add($"message_ids={Uri.EscapeDataString(messageId)}");

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/mget?{string.Join("&", parts)}",
            "GET",
            body: null,
            extraHeaders: null,
            ct);
    }

    public async Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
        string token,
        LarkMessageResourceDownloadRequest request,
        CancellationToken ct)
    {
        ValidateMessageResourceRequest(request);
        var resourceType = request.Kind switch
        {
            LarkMessageResourceKind.Image => "image",
            LarkMessageResourceKind.File => "file",
            _ => throw new ArgumentException("Lark message resource kind must be image or file.", nameof(request)),
        };

        var response = await _nyxClient.ProxyGetBinaryResponseAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId.Trim())}/resources/{Uri.EscapeDataString(request.ResourceKey.Trim())}?type={resourceType}",
            extraHeaders: null,
            ct);

        return new LarkMessageResourceDownloadResult(
            response.Succeeded,
            response.Content,
            response.ContentType,
            response.FileName,
            response.Detail,
            response.HttpStatus);
    }

    public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct)
    {
        var query = $"page_size={request.PageSize}";
        if (!string.IsNullOrWhiteSpace(request.PageToken))
            query += $"&page_token={Uri.EscapeDataString(request.PageToken.Trim())}";

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Query))
            body["query"] = NormalizeChatSearchQuery(request.Query.Trim());

        var filter = new Dictionary<string, object?>();
        if (request.SearchTypes is { Count: > 0 })
            filter["search_types"] = request.SearchTypes;
        if (request.MemberIds is { Count: > 0 })
            filter["member_ids"] = request.MemberIds;
        if (request.IsManager)
            filter["is_manager"] = true;
        if (request.DisableSearchByUser)
            filter["disable_search_by_user"] = true;
        if (filter.Count > 0)
            body["filter"] = filter;

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v2/chats/search?{query}",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["valueRange"] = new Dictionary<string, object?>
            {
                ["range"] = request.Range,
                ["values"] = request.Rows,
            },
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/sheets/v2/spreadsheets/{Uri.EscapeDataString(request.SpreadsheetToken)}/values_append",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct)
    {
        // GET /approval/v4/tasks/query is the only documented user-task-list endpoint; user_id
        // and topic are both required. The shorter /approval/v4/tasks path does not exist and
        // Lark answers it with a misleading 99991663 "Invalid access token".
        var queryParts = new List<string>
        {
            $"user_id={Uri.EscapeDataString(request.UserId)}",
            $"topic={Uri.EscapeDataString(request.Topic)}",
            $"page_size={request.PageSize}",
        };
        if (!string.IsNullOrWhiteSpace(request.PageToken))
            queryParts.Add($"page_token={Uri.EscapeDataString(request.PageToken.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.UserIdType))
            queryParts.Add($"user_id_type={Uri.EscapeDataString(request.UserIdType.Trim())}");

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/approval/v4/tasks/query?{string.Join("&", queryParts)}",
            "GET",
            body: null,
            extraHeaders: null,
            ct);
    }

    public Task<string> GetApprovalInstanceAsync(string token, LarkApprovalInstanceGetRequest request, CancellationToken ct)
    {
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Locale))
            queryParts.Add($"locale={Uri.EscapeDataString(request.Locale.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.UserIdType))
            queryParts.Add($"user_id_type={Uri.EscapeDataString(request.UserIdType.Trim())}");

        var query = queryParts.Count == 0 ? string.Empty : $"?{string.Join("&", queryParts)}";
        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/approval/v4/instances/{Uri.EscapeDataString(request.InstanceCode)}{query}",
            "GET",
            body: null,
            extraHeaders: null,
            ct);
    }

    public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct)
    {
        // The documented action endpoints are tasks/approve, tasks/reject and tasks/transfer
        // (tasks/pass, tasks/refuse and tasks/forward do not exist on the Lark side). All three
        // take user_id_type as a query parameter and require approval_code in the body.
        var endpoint = request.Action switch
        {
            "approve" => "open-apis/approval/v4/tasks/approve",
            "reject" => "open-apis/approval/v4/tasks/reject",
            "transfer" => "open-apis/approval/v4/tasks/transfer",
            _ => throw new InvalidOperationException($"Unsupported approval action: {request.Action}"),
        };
        var path = string.IsNullOrWhiteSpace(request.UserIdType)
            ? endpoint
            : $"{endpoint}?user_id_type={Uri.EscapeDataString(request.UserIdType.Trim())}";

        var body = new Dictionary<string, object?>
        {
            ["approval_code"] = request.ApprovalCode,
            ["instance_code"] = request.InstanceCode,
            ["task_id"] = request.TaskId,
            ["user_id"] = request.UserId,
        };

        if (!string.IsNullOrWhiteSpace(request.Comment))
            body["comment"] = request.Comment.Trim();
        if (!string.IsNullOrWhiteSpace(request.FormJson))
            body["form"] = request.FormJson.Trim();
        if (!string.IsNullOrWhiteSpace(request.TransferUserId))
            body["transfer_user_id"] = request.TransferUserId.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            path,
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> CreateDocxDocumentAsync(string token, LarkDocxCreateRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = request.Title,
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            "open-apis/docx/v1/documents",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> AppendDocxTextBlocksAsync(string token, LarkDocxAppendBlocksRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["children"] = BuildTextBlocks(request.MarkdownText),
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/docx/v1/documents/{Uri.EscapeDataString(request.DocumentId)}/blocks/{Uri.EscapeDataString(request.DocumentId)}/children",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> SetDrivePermissionAsync(string token, LarkDrivePermissionRequest request, CancellationToken ct)
    {
        var linkShareEntity = request.Visibility == LarkDocxVisibility.Editable
            ? "tenant_editable"
            : "tenant_readable";

        Dictionary<string, object?> body;
        if (string.Equals(request.ObjType, "docx", StringComparison.OrdinalIgnoreCase))
        {
            body = new Dictionary<string, object?>
            {
                ["link_share_entity"] = linkShareEntity,
                ["external_access"] = false,
                ["security_entity"] = "anyone_can_view",
                ["comment_entity"] = "anyone_can_view",
                // Lark's share_entity uses a DISTINCT enum (anyone | same_tenant | only_full_access),
                // NOT the security/comment/copy "anyone_can_view" vocabulary. Sending "anyone_can_view"
                // here fails Lark param validation (400) and aborts the entire public-permission PATCH,
                // so even link_share_entity never applies and the doc stays private. "same_tenant"
                // matches the tenant_readable/editable link scope.
                ["share_entity"] = "same_tenant",
                ["copy_entity"] = "anyone_can_view",
            };
        }
        else
        {
            // Non-docx (e.g. bitable) public-permission PATCH: send ONLY the link-share scope. The
            // doc-specific security/comment/copy/share entities above are tuned for docx and are not
            // guaranteed valid for every obj type — sending them risks a 400 that aborts the whole
            // PATCH. This branch is the rare grant-failure fallback for lark_base_create; the exact
            // bitable /public body must be confirmed against a live call before relying on it broadly.
            body = new Dictionary<string, object?>
            {
                ["link_share_entity"] = linkShareEntity,
                ["external_access"] = false,
            };
        }

        if (!string.IsNullOrWhiteSpace(request.ReceiveId) &&
            !string.IsNullOrWhiteSpace(request.ReceiveIdType))
        {
            body["target"] = new Dictionary<string, object?>
            {
                ["receive_id"] = request.ReceiveId.Trim(),
                ["receive_id_type"] = request.ReceiveIdType.Trim(),
            };
        }

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/drive/v1/permissions/{Uri.EscapeDataString(request.DocumentToken)}/public?type={Uri.EscapeDataString(request.ObjType)}",
            "PATCH",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> CreateBitableAppAsync(string token, LarkBitableCreateRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Name,
        };
        if (!string.IsNullOrWhiteSpace(request.FolderToken))
            body["folder_token"] = request.FolderToken.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            "open-apis/bitable/v1/apps",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> GrantResourceMemberAsync(string token, LarkResourceMemberGrantRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["member_type"] = request.MemberType,
            ["member_id"] = request.MemberId,
            ["perm"] = request.Perm,
        };

        var path =
            $"open-apis/drive/v1/permissions/{Uri.EscapeDataString(request.Token)}/members" +
            $"?type={Uri.EscapeDataString(request.ObjType)}" +
            $"&need_notification={(request.NeedNotification ? "true" : "false")}";

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            path,
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct)
    {
        var formFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["file_name"] = request.FileName,
            ["parent_type"] = request.ParentType,
            ["parent_node"] = request.ParentNode,
            ["size"] = request.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(request.Checksum))
            formFields["checksum"] = request.Checksum.Trim();
        if (!string.IsNullOrWhiteSpace(request.Extra))
            formFields["extra"] = request.Extra.Trim();

        return _nyxClient.ProxyRequestMultipartAsync(
            token,
            _options.ProviderSlug,
            "open-apis/drive/v1/medias/upload_all",
            "POST",
            formFields,
            fileFieldName: "file",
            fileName: request.FileName,
            fileContentType: request.ContentType,
            fileContent: request.Content,
            extraHeaders: null,
            ct);
    }

    public Task<string> UploadApprovalFileAsync(string token, LarkApprovalFileUploadRequest request, CancellationToken ct)
    {
        var formFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = request.FileName,
            ["type"] = request.FileType,
        };

        // Approval attachments only upload through the legacy surface
        // /approval/openapi/v2/file/upload (no open-apis/ prefix; same host serves it);
        // open-apis/approval/v4/files/upload is not a real endpoint.
        return _nyxClient.ProxyRequestMultipartAsync(
            token,
            _options.ProviderSlug,
            "approval/openapi/v2/file/upload",
            "POST",
            formFields,
            fileFieldName: "content",
            fileName: request.FileName,
            fileContentType: request.ContentType,
            fileContent: request.Content,
            extraHeaders: null,
            ct);
    }

    private static void ValidateMessageResourceRequest(LarkMessageResourceDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MessageId))
            throw new ArgumentException("Lark message id is required.", nameof(request));
        if (!request.MessageId.Trim().StartsWith("om_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Lark message id must start with om_.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ResourceKey))
            throw new ArgumentException("Lark message resource key is required.", nameof(request));
        if (request.Kind is not (LarkMessageResourceKind.Image or LarkMessageResourceKind.File))
            throw new ArgumentException("Lark message resource kind must be image or file.", nameof(request));
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildTextBlocks(string markdownText)
    {
        var normalized = string.IsNullOrEmpty(markdownText) ? " " : markdownText;
        var blocks = new List<Dictionary<string, object?>>();
        foreach (var chunk in SplitBlockText(normalized))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["block_type"] = 2,
                ["text"] = new Dictionary<string, object?>
                {
                    ["elements"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["text_run"] = new Dictionary<string, object?>
                            {
                                ["content"] = chunk,
                            },
                        },
                    },
                },
            });
        }

        return blocks;
    }

    private static IEnumerable<string> SplitBlockText(string text)
    {
        for (var offset = 0; offset < text.Length; offset += MaxDocxBlockTextLength)
        {
            var length = Math.Min(MaxDocxBlockTextLength, text.Length - offset);
            yield return text.Substring(offset, length);
        }
    }

    internal static string NormalizeChatSearchQuery(string query)
    {
        if (!query.Contains('-', StringComparison.Ordinal))
            return query;

        try
        {
            query = JsonSerializer.Deserialize<string>($"\"{query.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
                ?? query;
        }
        catch (JsonException)
        {
            // Keep the original query if unquoting fails; the outer quoting below is the only required behavior.
        }

        return JsonSerializer.Serialize(query);
    }
}
