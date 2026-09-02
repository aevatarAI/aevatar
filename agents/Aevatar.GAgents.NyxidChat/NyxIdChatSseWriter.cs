using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AGUI.Contracts;
using Aevatar.Foundation.Abstractions.Tools;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class NyxIdChatSseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpResponse _response;
    private bool _started;

    public NyxIdChatSseWriter(HttpResponse response)
    {
        _response = response;
    }

    public bool Started => _started;

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        _started = true;
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-store";
        _response.Headers.Pragma = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no";
        await _response.StartAsync(ct);
    }

    public async ValueTask WriteFrameAsync(object frame, CancellationToken ct = default)
    {
        await WriteFrameAsync(frame, sequence: 0, ct);
    }

    public async ValueTask WriteFrameAsync(object frame, long sequence, CancellationToken ct = default)
    {
        await StartAsync(ct);
        var node = JsonSerializer.SerializeToNode(frame, JsonOptions) as JsonObject
                   ?? throw new InvalidOperationException("SSE frame must serialize to a JSON object.");
        if (sequence > 0)
            node["sequence"] = sequence;
        var json = node.ToJsonString(JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }

    public ValueTask WriteRunStartedAsync(string actorId, string turnId, CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "RUN_STARTED",
            actorId,
            turnId,
            runStarted = new { threadId = actorId, runId = turnId },
        }, ct);

    public ValueTask WriteKeepAliveAsync(string actorId, string turnId, CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "CUSTOM",
            custom = new
            {
                name = "aevatar.nyxid_chat.keepalive",
                payload = new
                {
                    actorId,
                    turnId,
                    status = "running",
                },
            },
        }, ct);

    public ValueTask WriteTextDeltaAsync(string delta, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_CONTENT", textMessageContent = new { delta } }, sequence, ct);

    public ValueTask WriteTextStartAsync(string messageId, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_START", textMessageStart = new { messageId, role = "assistant" } }, sequence, ct);

    public ValueTask WriteTextEndAsync(string messageId, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TEXT_MESSAGE_END", textMessageEnd = new { messageId } }, sequence, ct);

    public ValueTask WriteRunFinishedAsync(
        string turnId,
        RunCompletionStatus status,
        long sequence,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "RUN_FINISHED",
            turnId,
            runFinished = new
            {
                runId = turnId,
                status = status == RunCompletionStatus.Blocked ? "blocked" : "completed",
            },
        }, sequence, ct);

    public ValueTask WriteAuthorizationRequiredAsync(
        NyxIdAuthorizationRequiredEvent blocker,
        long sequence,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "CUSTOM",
            custom = new
            {
                name = "nyxid.authorization.required",
                payload = new
                {
                    userServiceId = blocker.HasUserServiceId ? blocker.UserServiceId : null,
                    serviceSlug = blocker.ServiceSlug,
                    serviceLabel = blocker.HasServiceLabel ? blocker.ServiceLabel : null,
                    resourceUri = blocker.HasResourceUri ? blocker.ResourceUri : null,
                    reasonCode = blocker.ReasonCode,
                    safeMessage = blocker.SafeMessage,
                },
            },
        }, sequence, ct);

    public ValueTask WriteUsageAsync(
        bool available,
        int promptTokens,
        int completionTokens,
        int totalTokens,
        string? model,
        long sequence,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "USAGE",
            usage = new
            {
                available,
                promptTokens,
                completionTokens,
                totalTokens,
                model = string.IsNullOrWhiteSpace(model) ? null : model,
            },
        }, sequence, ct);

    public ValueTask WriteToolCallStartAsync(
        string toolName,
        string callId,
        ToolPresentationDescriptor? presentation,
        long sequence,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "TOOL_CALL_START",
            toolCallStart = new
            {
                toolName,
                toolCallId = callId,
                presentation = BuildPresentationPayload(presentation, toolName),
            },
        }, sequence, ct);

    public ValueTask WriteToolCallEndAsync(string callId, string result, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new { type = "TOOL_CALL_END", toolCallEnd = new { toolCallId = callId, result } }, sequence, ct);

    public ValueTask WriteRunErrorAsync(string turnId, string code, string message, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "RUN_ERROR",
            turnId,
            runError = new { runId = turnId, code, message },
        }, sequence, ct);

    public ValueTask WriteMediaContentAsync(
        Aevatar.AI.Abstractions.MediaContentEvent evt,
        long sequence,
        CancellationToken ct)
    {
        if (evt.Part == null) return ValueTask.CompletedTask;
        return WriteFrameAsync(new
        {
            type = "MEDIA_CONTENT",
            mediaContent = new
            {
                kind = evt.Part?.Kind switch
                {
                    Aevatar.AI.Abstractions.ChatContentPartKind.Image => "image",
                    Aevatar.AI.Abstractions.ChatContentPartKind.Audio => "audio",
                    Aevatar.AI.Abstractions.ChatContentPartKind.Video => "video",
                    Aevatar.AI.Abstractions.ChatContentPartKind.Text => "text",
                    _ => "unknown",
                },
                dataBase64 = string.IsNullOrEmpty(evt.Part?.DataBase64) ? null : evt.Part.DataBase64,
                mediaType = string.IsNullOrEmpty(evt.Part?.MediaType) ? null : evt.Part.MediaType,
                uri = string.IsNullOrEmpty(evt.Part?.Uri) ? null : evt.Part.Uri,
                name = string.IsNullOrEmpty(evt.Part?.Name) ? null : evt.Part.Name,
                text = string.IsNullOrEmpty(evt.Part?.Text) ? null : evt.Part.Text,
            }
        }, sequence, ct);
    }

    public ValueTask WriteReasoningAsync(string delta, long sequence, CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "CUSTOM",
            custom = new
            {
                name = "aevatar.llm.reasoning",
                payload = new { delta },
            },
        }, sequence, ct);

    public ValueTask WriteTypedCustomEventAsync(
        string name,
        IMessage payload,
        long sequence,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);
        var node = JsonNode.Parse(JsonFormatter.Default.Format(payload))
                   ?? throw new InvalidOperationException("Typed custom payload must serialize to JSON.");
        NormalizeNyxIdEnumValues(node);
        return WriteFrameAsync(new
        {
            type = "CUSTOM",
            custom = new { name, payload = node },
        }, sequence, ct);
    }

    public ValueTask WriteToolApprovalRequestAsync(
        string requestId, string toolName, string toolCallId,
        string argumentsJson, bool isDestructive, int timeoutSeconds,
        long sequence,
        CancellationToken ct) =>
        WriteFrameAsync(new
        {
            type = "TOOL_APPROVAL_REQUEST",
            toolApprovalRequest = new
            {
                requestId,
                toolName,
                toolCallId,
                argumentsJson,
                isDestructive,
                timeoutSeconds,
            }
        }, sequence, ct);

    private static void NormalizeNyxIdEnumValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonValue value &&
                        value.TryGetValue<string>(out var text) &&
                        TryNormalizeNyxIdEnumValue(text, out var normalized))
                    {
                        obj[property.Key] = normalized;
                    }
                    else if (property.Value is not null)
                    {
                        NormalizeNyxIdEnumValues(property.Value);
                    }
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value &&
                        value.TryGetValue<string>(out var text) &&
                        TryNormalizeNyxIdEnumValue(text, out var normalized))
                    {
                        array[index] = normalized;
                    }
                    else if (array[index] is not null)
                    {
                        NormalizeNyxIdEnumValues(array[index]!);
                    }
                }
                break;
        }
    }

    private static bool TryNormalizeNyxIdEnumValue(string value, out string normalized)
    {
        string[] prefixes =
        [
            "NYX_ID_CHAT_CONTINUATION_ADMISSION_STATUS_",
            "NYX_ID_CHAT_STEP_CONTROL_KIND_",
            "NYX_ID_CHAT_ACTION_DISPOSITION_",
            "NYX_ID_CHAT_OPERATION_PHASE_",
            "NYX_ID_CHAT_EFFECT_EVIDENCE_",
            "NYX_ID_CHAT_TRANSITION_OUTCOME_",
            "NYX_ID_CHAT_CONTINUATION_KIND_",
            "NYX_ID_CHAT_CONTROL_OUTCOME_",
            "NYX_ID_ASSISTANT_ACTION_RISK_",
            "NYX_ID_ASSISTANT_ACTION_TIER_",
            "NYX_ID_ASSISTANT_ACTION_KIND_",
            "NYX_ID_CHAT_CONTROL_KIND_",
            "NYX_ID_CHAT_TURN_STATUS_",
            "NYX_ID_CHAT_TASK_STATUS_",
            "NYX_ID_CHAT_STEP_STATUS_",
            "NYX_ID_CHAT_STEP_KIND_",
            "NYX_ID_CHAT_ATTENTION_KIND_",
            "NYX_ID_CHAT_APPROVAL_REVERSIBILITY_",
            "NYX_ID_CHAT_NEEDS_YOU_RESOLUTION_OUTCOME_",
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            normalized = value[prefix.Length..].ToLowerInvariant();
            return true;
        }

        normalized = value;
        return false;
    }

    private static object BuildPresentationPayload(
        ToolPresentationDescriptor? presentation,
        string invocationName)
    {
        var descriptor = Aevatar.AI.Abstractions.ToolProviders.ToolPresentationDescriptors.Snapshot(
            presentation,
            invocationName);
        return new
        {
            invocationName = descriptor.InvocationName,
            displayName = descriptor.DisplayName,
            description = descriptor.Description,
            kind = descriptor.Kind switch
            {
                ToolPresentationKind.BuiltIn => "builtIn",
                ToolPresentationKind.NyxIdOperation => "nyxIdOperation",
                ToolPresentationKind.Mcp => "mcp",
                ToolPresentationKind.Skill => "skill",
                _ => "generic",
            },
            availability = descriptor.Availability == ToolAvailability.Unavailable
                ? "unavailable"
                : "available",
            unavailableReason = string.IsNullOrWhiteSpace(descriptor.UnavailableReason)
                ? null
                : descriptor.UnavailableReason,
            iconUrl = string.IsNullOrWhiteSpace(descriptor.IconUrl) ? null : descriptor.IconUrl,
            sourceRef = BuildSourceRefPayload(descriptor),
        };
    }

    private static object? BuildSourceRefPayload(ToolPresentationDescriptor descriptor) =>
        descriptor.SourceRefCase switch
        {
            ToolPresentationDescriptor.SourceRefOneofCase.BuiltIn => new
            {
                type = "builtIn",
                builtIn = new { toolId = descriptor.BuiltIn.ToolId },
            },
            ToolPresentationDescriptor.SourceRefOneofCase.NyxIdOperation => new
            {
                type = "nyxIdOperation",
                nyxIdOperation = new
                {
                    connectedServiceId = descriptor.NyxIdOperation.ConnectedServiceId,
                    serviceSlug = descriptor.NyxIdOperation.ServiceSlug,
                    catalogServiceSlug = descriptor.NyxIdOperation.CatalogServiceSlug,
                    connectionLabel = descriptor.NyxIdOperation.ConnectionLabel,
                    connectorDisplayName = descriptor.NyxIdOperation.ConnectorDisplayName,
                    operationId = descriptor.NyxIdOperation.OperationId,
                    httpMethod = descriptor.NyxIdOperation.HttpMethod,
                    pathTemplate = descriptor.NyxIdOperation.PathTemplate,
                },
            },
            ToolPresentationDescriptor.SourceRefOneofCase.Mcp => new
            {
                type = "mcp",
                mcp = new
                {
                    serverName = descriptor.Mcp.ServerName,
                    toolName = descriptor.Mcp.ToolName,
                },
            },
            ToolPresentationDescriptor.SourceRefOneofCase.Skill => new
            {
                type = "skill",
                skill = new
                {
                    skillName = descriptor.Skill.SkillName,
                    source = descriptor.Skill.Source,
                },
            },
            _ => null,
        };
}
