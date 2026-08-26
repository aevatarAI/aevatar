using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AGUI.Contracts;
using Aevatar.Capabilities;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private static readonly JsonSerializerOptions PublicChatJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IEndpointRouteBuilder MapNyxIdChatPublicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/chat/conversations", HandlePublicListConversationsAsync).WithTags("Chat");
        app.MapGet("/api/chat/conversations/{conversationId}", HandlePublicGetConversationAsync).WithTags("Chat");
        app.MapGet("/api/chat/conversations/{conversationId}/state", HandlePublicGetStateAsync)
            .WithTags("Chat")
            .Produces<NyxIdChatConversationStateResponse>(StatusCodes.Status200OK)
            .Produces<NyxIdChatConversationStateNotFoundResponse>(StatusCodes.Status404NotFound);
        app.MapDelete("/api/chat/conversations/{conversationId}", HandlePublicDeleteConversationAsync).WithTags("Chat");
        return app;
    }

    public static async Task HandlePublicChatAsync(
        HttpContext http,
        JsonElement body,
        CancellationToken ct = default)
    {
        if (!TryGetPublicScope(http, out var scopeId))
        {
            await WritePublicErrorAsync(http, StatusCodes.Status401Unauthorized, "AUTHENTICATED_SCOPE_REQUIRED",
                "A single authenticated scope claim is required.", ct);
            return;
        }

        try
        {
            if (!body.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "ASSISTANT_TYPE_REQUIRED",
                    "An Assistant request type is required.", ct);
                return;
            }

            switch (typeElement.GetString()?.Trim())
            {
                case "text":
                case "action.continue":
                    await HandlePublicStreamAsync(http, scopeId, body, ct);
                    break;
                case "approval.resolve":
                    await ExecutePublicResultAsync(
                        http,
                        await HandlePublicApprovalAsync(http, scopeId, body, ct));
                    break;
                case "input.resolve":
                    await ExecutePublicResultAsync(
                        http,
                        await HandlePublicInputAsync(http, scopeId, body, ct));
                    break;
                case "task.stop":
                    await ExecutePublicResultAsync(http, await HandlePublicStopAsync(http, scopeId, body, ct));
                    break;
                case "task.steer":
                    await ExecutePublicResultAsync(http, await HandlePublicSteeringAsync(http, scopeId, body, ct));
                    break;
                case "step.retry":
                    await ExecutePublicResultAsync(http, await HandlePublicRetryAsync(http, scopeId, body, ct));
                    break;
                case "step.skip":
                    await ExecutePublicResultAsync(http, await HandlePublicSkipAsync(http, scopeId, body, ct));
                    break;
                default:
                    await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "ASSISTANT_TYPE_UNSUPPORTED",
                        "The Assistant request type is unsupported.", ct);
                    break;
            }
        }
        catch (JsonException)
        {
            await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "ASSISTANT_REQUEST_INVALID",
                "The Assistant request body is invalid.", ct);
        }
    }

    internal static async Task<IResult> HandlePublicListConversationsAsync(
        HttpContext http,
        int? pageSize,
        string? cursor,
        [FromServices] IChatHistoryQueryPort queryPort,
        [FromServices] INyxIdChatConversationStateQueryPort stateQueryPort,
        CancellationToken ct)
    {
        if (!TryGetPublicScope(http, out var scopeId))
            return PublicUnauthorized();

        var page = await queryPort.GetIndexAsync(
            new ChatHistoryIndexPageRequest(scopeId, pageSize ?? 50, cursor),
            ct);
        var conversations = page.Conversations
            .Where(static conversation => string.Equals(
                conversation.ServiceKind,
                NyxIdChatServiceDefaults.GAgentKind,
                StringComparison.Ordinal))
            .ToList();
        var attention = await stateQueryPort.GetAttentionSummariesAsync(
            scopeId,
            conversations.Select(static conversation => conversation.Id).ToArray(),
            ct);
        return Results.Ok(new ChatHistoryIndexPage(
            conversations.Select(conversation =>
            {
                if (!attention.TryGetValue(conversation.Id, out var summary))
                    return conversation;
                return conversation with
                {
                    TaskStatus = summary.TaskStatus,
                    AttentionKind = summary.AttentionKind,
                    AttentionSince = summary.AttentionSince,
                    ActiveStepSummary = summary.ActiveStepSummary,
                    StateVersion = summary.StateVersion,
                    ContextAttachments = summary.ContextAttachments,
                };
            }).ToList(),
            page.NextCursor));
    }

    private static async Task<IResult> HandlePublicGetConversationAsync(
        HttpContext http,
        string conversationId,
        [FromServices] IChatHistoryQueryPort queryPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        CancellationToken ct)
    {
        if (!TryGetPublicScope(http, out var scopeId))
            return PublicUnauthorized();
        if (!TryValidateControlIdentity(conversationId, out var normalizedConversationId))
            return Results.NotFound();

        var admission = await AuthorizeConversationAsync(
            admissionPort,
            scopeId,
            normalizedConversationId,
            ScopeResourceOperation.Use,
            ct);
        if (admission is not null)
            return admission;

        var messages = await queryPort.GetMessagesAsync(scopeId, normalizedConversationId, ct);
        return messages.Status == ChatHistoryConversationResultStatus.NotFound
            ? Results.NotFound()
            : Results.Ok(new
            {
                messages.Messages,
                // Durable trajectory ledger for the stored turns. Studio rebuilds its
                // per-turn trace containers from these facts instead of inferring them.
                messages.Operations,
                messages.StateVersion,
                ProjectionStatus = messages.ProjectionStatus == ChatHistoryConversationProjectionStatus.Pending
                    ? "pending"
                    : "current",
            });
    }

    private static async Task<IResult> HandlePublicGetStateAsync(
        HttpContext http,
        string conversationId,
        [FromServices] INyxIdChatConversationStateQueryPort stateQueryPort,
        CancellationToken ct)
    {
        if (!TryGetPublicScope(http, out var scopeId))
            return PublicUnauthorized();
        return await HandleGetStateAsync(
            http,
            scopeId,
            conversationId,
            stateQueryPort,
            ct);
    }

    private static async Task<IResult> HandlePublicDeleteConversationAsync(
        HttpContext http,
        string conversationId,
        [FromServices] NyxIdChatLifecycleFacade lifecycleFacade,
        CancellationToken ct)
    {
        if (!TryGetPublicScope(http, out var scopeId))
            return PublicUnauthorized();
        if (!TryValidateControlIdentity(conversationId, out var normalizedConversationId))
            return Results.NotFound();

        var receipt = await lifecycleFacade.DeleteConversationAsync(scopeId, normalizedConversationId, ct);
        var stateUrl = $"/api/chat/conversations/{Uri.EscapeDataString(normalizedConversationId)}/state";
        return receipt.Status switch
        {
            NyxIdChatConversationDeleteStatus.Accepted => Results.Accepted(stateUrl, new
            {
                status = "accepted",
                stateUrl,
            }),
            NyxIdChatConversationDeleteStatus.NotFound => Results.NotFound(new { error = "Conversation not found" }),
            NyxIdChatConversationDeleteStatus.AccessDenied => Results.Json(
                new { error = "Conversation access denied" },
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(
                new { error = "Conversation admission unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task HandlePublicStreamAsync(
        HttpContext http,
        string scopeId,
        JsonElement body,
        CancellationToken ct)
    {
        var request = Deserialize<PublicStreamRequest>(body);
        var streamType = request.Type?.Trim();
        var clientRequestId = ResolveClientRequestId(http, request.ClientRequestId);
        if (!TryValidateControlIdentity(clientRequestId, out clientRequestId))
        {
            await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "CLIENT_REQUEST_ID_REQUIRED",
                "clientRequestId or Idempotency-Key is required.", ct);
            return;
        }

        var createIfMissing = string.IsNullOrWhiteSpace(request.ConversationId);
        if (createIfMissing && string.Equals(streamType, "action.continue", StringComparison.Ordinal))
        {
            await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "CONVERSATION_ID_REQUIRED",
                "conversationId is required for action.continue.", ct);
            return;
        }
        var actorId = createIfMissing
            ? NyxIdChatPublicIdentity.CreateConversationActorId(scopeId, clientRequestId)
            : request.ConversationId!.Trim();
        if (!TryValidateControlIdentity(actorId, out actorId))
        {
            await WritePublicErrorAsync(http, StatusCodes.Status400BadRequest, "CONVERSATION_ID_INVALID",
                "conversationId is invalid.", ct);
            return;
        }

        var streamRequest = new NyxIdChatStreamRequest(
            request.Prompt,
            InputParts: request.InputParts,
            ClientRequestId: clientRequestId,
            Type: streamType,
            OriginTurnId: request.OriginTurnId,
            Actions: request.Actions,
            AgentProfile: request.AgentProfile,
            ContextAttachments: request.ContextAttachments);
        await HandleStreamMessageCoreAsync(
            http,
            scopeId,
            actorId,
            streamRequest,
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(),
            http.RequestServices.GetRequiredService<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(),
            http.RequestServices.GetRequiredService<ILoggerFactory>(),
            createIfMissing,
            ct);
    }

    private static Task<IResult> HandlePublicApprovalAsync(
        HttpContext http,
        string scopeId,
        JsonElement body,
        CancellationToken ct)
    {
        var request = Deserialize<PublicApprovalRequest>(body);
        if (!request.Approved.HasValue)
        {
            return Task.FromResult(Results.Json(new
            {
                code = "APPROVAL_DECISION_REQUIRED",
                message = "approved must be explicitly set to true or false.",
            }, statusCode: StatusCodes.Status400BadRequest));
        }

        return HandleApprovalResolveControlAsync(
            http,
            scopeId,
            request.ConversationId ?? string.Empty,
            new NyxIdChatApprovalResolveRequest(
                request.RequestId,
                ResolveClientRequestId(http, request.ClientRequestId),
                request.Approved.Value,
                request.Reason,
                request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(),
            ct);
    }

    private static Task<IResult> HandlePublicInputAsync(
        HttpContext http,
        string scopeId,
        JsonElement body,
        CancellationToken ct)
    {
        var request = Deserialize<PublicInputRequest>(body);
        return HandleInputResolveControlAsync(
            http,
            scopeId,
            request.ConversationId ?? string.Empty,
            new NyxIdChatInputResolveRequest(
                request.RequestId,
                ResolveClientRequestId(http, request.ClientRequestId),
                request.Answer,
                request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(),
            ct);
    }

    private static Task<IResult> HandlePublicStopAsync(HttpContext http, string scopeId, JsonElement body, CancellationToken ct)
    {
        var request = Deserialize<PublicStopRequest>(body);
        return HandleStopControlAsync(
            http, scopeId, request.ConversationId ?? string.Empty,
            new NyxIdChatStopRequest(request.TurnId, request.StopRequestId, ResolveClientRequestId(http, request.ClientRequestId), request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(), ct);
    }

    private static Task<IResult> HandlePublicSteeringAsync(HttpContext http, string scopeId, JsonElement body, CancellationToken ct)
    {
        var request = Deserialize<PublicSteeringRequest>(body);
        return HandleSteeringControlAsync(
            http, scopeId, request.ConversationId ?? string.Empty,
            new NyxIdChatSteeringRequest(request.TurnId, request.SteeringId, ResolveClientRequestId(http, request.ClientRequestId), request.Instruction, request.InputParts, request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(), ct);
    }

    private static Task<IResult> HandlePublicRetryAsync(HttpContext http, string scopeId, JsonElement body, CancellationToken ct)
    {
        var request = Deserialize<PublicRetryRequest>(body);
        return HandleRetryControlAsync(
            http, scopeId, request.ConversationId ?? string.Empty, request.TurnId ?? string.Empty, request.StepId ?? string.Empty,
            new NyxIdChatRetryStepRequest(request.TaskId, request.RetryRequestId, ResolveClientRequestId(http, request.ClientRequestId), request.ExpectedOperationGeneration, request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(), ct);
    }

    private static Task<IResult> HandlePublicSkipAsync(HttpContext http, string scopeId, JsonElement body, CancellationToken ct)
    {
        var request = Deserialize<PublicSkipRequest>(body);
        return HandleSkipControlAsync(
            http, scopeId, request.ConversationId ?? string.Empty, request.TurnId ?? string.Empty, request.StepId ?? string.Empty,
            new NyxIdChatSkipStepRequest(request.TaskId, request.SkipRequestId, ResolveClientRequestId(http, request.ClientRequestId), request.ExpectedOperationGeneration, request.ExpectedStateVersion),
            http.RequestServices.GetRequiredService<IScopeResourceAdmissionPort>(),
            http.RequestServices.GetRequiredService<INyxIdChatControlCommandPort>(), ct);
    }

    private static T Deserialize<T>(JsonElement body) where T : class =>
        body.Deserialize<T>(PublicChatJsonOptions)
        ?? throw new JsonException("Assistant request body is required.");

    private static bool TryGetPublicScope(HttpContext http, out string scopeId) =>
        AevatarScopeAccessGuard.TryGetCallerScopeId(http, out scopeId);

    private static IResult PublicUnauthorized() => Results.Json(new
    {
        code = "AUTHENTICATED_SCOPE_REQUIRED",
        message = "A single authenticated scope claim is required.",
    }, statusCode: StatusCodes.Status401Unauthorized);

    private static Task WritePublicErrorAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        return http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    private static Task ExecutePublicResultAsync(HttpContext http, IResult result) => result.ExecuteAsync(http);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicStreamRequest(
        string? Type,
        string? ConversationId,
        string? ClientRequestId,
        string? Prompt,
        IReadOnlyList<ContentPartDto>? InputParts,
        string? OriginTurnId,
        IReadOnlyList<NyxIdChatActionReportDto>? Actions,
        NyxIdChatAgentProfileReferenceDto? AgentProfile,
        IReadOnlyList<NyxIdChatContextAttachmentDto>? ContextAttachments);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicApprovalRequest(
        string? Type, string? ConversationId, string? ClientRequestId, string? RequestId,
        bool? Approved, string? Reason = null, long ExpectedStateVersion = 0);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicInputRequest(
        string? Type, string? ConversationId, string? ClientRequestId, string? RequestId,
        NyxIdChatInputAnswerRequest? Answer, long ExpectedStateVersion = 0);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicStopRequest(
        string? Type, string? ConversationId, string? TurnId, string? StopRequestId, string? ClientRequestId, long ExpectedStateVersion = 0);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicSteeringRequest(
        string? Type, string? ConversationId, string? TurnId, string? SteeringId, string? ClientRequestId, string? Instruction, IReadOnlyList<ContentPartDto>? InputParts = null, long ExpectedStateVersion = 0);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicRetryRequest(
        string? Type, string? ConversationId, string? TurnId, string? TaskId, string? StepId, string? RetryRequestId, string? ClientRequestId, long ExpectedOperationGeneration, long ExpectedStateVersion = 0);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PublicSkipRequest(
        string? Type, string? ConversationId, string? TurnId, string? TaskId, string? StepId, string? SkipRequestId, string? ClientRequestId, long ExpectedOperationGeneration, long ExpectedStateVersion = 0);
}
