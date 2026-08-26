using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Capabilities;
using Aevatar.AGUI.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private const int MaxInlineInputPartBytes = 10 * 1024 * 1024;
    internal const string ActionContinuationCredentialRefreshRequiredCode =
        "NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED";
    internal const string ActionContinuationCatalogUnavailableCode =
        "NYXID_ACTION_CONTINUATION_CATALOG_UNAVAILABLE";

    internal static TimeSpan StreamKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
    internal static TimeSpan StreamTerminalTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private static async Task HandleStreamMessageAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        [FromServices] ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> actionContinuationInteractionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        await HandleStreamMessageCoreAsync(
            http,
            scopeId,
            actorId,
            request,
            admissionPort,
            interactionService,
            actionContinuationInteractionService,
            loggerFactory,
            createIfMissing: false,
            ct);

    private static async Task HandleStreamMessageCoreAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        IScopeResourceAdmissionPort admissionPort,
        ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> actionContinuationInteractionService,
        ILoggerFactory loggerFactory,
        bool createIfMissing,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var accessToken = string.Empty;
        var credentials = AgentToolCredentials.Empty;
        var prompt = string.Empty;
        var streamType = request.Type?.Trim() ?? string.Empty;
        var clientRequestId = ResolveClientRequestId(http, request.ClientRequestId);
        var turnId = CreateTurnId(actorId, clientRequestId);
        var ownerSubject = string.Empty;
        AgentProfileReference? agentProfileReference = null;
        IReadOnlyList<NyxIdChatActionReport> actionReports = [];
        IReadOnlyList<ChatContentPart> inputParts = [];

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            credentials = ExtractNyxIdCredentials(http) ?? AgentToolCredentials.Empty;
            accessToken = credentials.NyxIdAccessToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(http.User, out ownerSubject))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.Equals(streamType, "text", StringComparison.Ordinal))
            {
                prompt = request.Prompt?.Trim() ?? string.Empty;
                if ((string.IsNullOrWhiteSpace(prompt) && request.InputParts is not { Count: > 0 }) ||
                    !string.IsNullOrWhiteSpace(request.OriginTurnId) ||
                    request.Actions is { Count: > 0 } ||
                    (request.AgentProfile is not null &&
                     (!createIfMissing || !TryMapAgentProfileReference(request.AgentProfile, out agentProfileReference))))
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else if (string.Equals(streamType, "action.continue", StringComparison.Ordinal))
            {
                var originTurnId = request.OriginTurnId?.Trim() ?? string.Empty;
                if (!TryValidateControlIdentity(clientRequestId, out clientRequestId) ||
                    string.IsNullOrWhiteSpace(ownerSubject) ||
                    !string.IsNullOrWhiteSpace(request.Prompt) ||
                    request.InputParts is { Count: > 0 } ||
                    request.AgentProfile is not null ||
                    !TryMapActionReports(request.Actions, originTurnId, out actionReports))
                {
                    http.Response.StatusCode = string.IsNullOrWhiteSpace(ownerSubject)
                        ? StatusCodes.Status401Unauthorized
                        : StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!createIfMissing && !await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Stream,
                    ct))
                return;

            if (string.Equals(streamType, "action.continue", StringComparison.Ordinal) &&
                !await TryEnsureActionContinuationCredentialVisibilityAsync(
                    http,
                    actionReports,
                    accessToken,
                    ct))
            {
                return;
            }

            if (string.Equals(streamType, "text", StringComparison.Ordinal))
            {
                var normalizedInput = await MaterializeInlineInputPartsAsync(
                    request.InputParts,
                    scopeId,
                    turnId,
                    http.RequestServices.GetService<IFileArtifactIngressPort>(),
                    ct);
                if (!normalizedInput.Succeeded)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                inputParts = normalizedInput.Parts;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID chat request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var writer = new NyxIdChatSseWriter(http.Response);
        var writerGate = new NyxIdChatStreamWriterGate();
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerGate,
            logger,
            actorId,
            turnId,
            StreamKeepAliveInterval,
            ct);
        var interactionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<CommandInteractionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>>? interactionTask = null;
        var interactionDetached = false;
        try
        {
            await writer.StartAsync(ct);
            await writerGate.WriteAsync(
                token => writer.WriteRunStartedAsync(actorId, turnId, token),
                ct);
            heartbeat.Start();
            async ValueTask EmitAsync(AGUIEvent evt, CancellationToken token)
            {
                var isTerminal = IsTerminalFrame(evt);
                if (isTerminal)
                {
                    var wroteTerminal = await writerGate.WriteTerminalAsync(
                        async writeToken =>
                        {
                            await NyxIdChatAguiSseEventWriter.WriteAsync(
                                evt,
                                turnId,
                                writer,
                                writeToken);
                        },
                        token);
                    if (wroteTerminal)
                        heartbeat.Stop();
                    return;
                }

                await writerGate.WriteAsync(
                    async writeToken =>
                    {
                        await NyxIdChatAguiSseEventWriter.WriteAsync(
                            evt,
                            turnId,
                            writer,
                            writeToken);
                    },
                    token);
            }

            CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus> result;
            if (string.Equals(streamType, "action.continue", StringComparison.Ordinal))
            {
                var commandId = NyxIdChatPublicIdentity.CreateActionContinuationCommandId(
                    actorId,
                    scopeId,
                    ownerSubject,
                    clientRequestId!,
                    request.OriginTurnId?.Trim() ?? string.Empty,
                    actionReports);
                interactionTask = actionContinuationInteractionService.ExecuteAsync(
                    new NyxIdActionContinuationCommand(
                        actorId,
                        scopeId,
                        request.OriginTurnId?.Trim() ?? string.Empty,
                        turnId,
                        ownerSubject,
                        clientRequestId!,
                        actionReports,
                        CommandId: commandId,
                        CorrelationId: commandId,
                        ToolContext: BuildAuthenticatedOwnerControlToolContext(
                            scopeId,
                            actorId,
                            clientRequestId!,
                            ownerSubject,
                            credentials)),
                    EmitAsync,
                    null,
                    interactionCancellation.Token);
            }
            else
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                var llmControl = await BuildLlmControlAsync(http, accessToken, ct);
                var rawInputParts = request.InputParts?.Select(static part => part.ToProto()).ToArray() ?? [];
                var commandId = NyxIdChatPublicIdentity.CreateChatCommandId(
                    actorId,
                    scopeId,
                    ownerSubject,
                    clientRequestId,
                    turnId,
                    prompt,
                    rawInputParts,
                    agentProfileReference,
                    request.ContextAttachments?.Select(static attachment => attachment.ToProto()));

                // Streaming endpoints do not pre-read runtime state before command dispatch.
                // The shared CQRS resolver owns actor lookup and attach-existing observation.
                interactionTask = interactionService.ExecuteAsync(
                    new NyxIdChatCommand(
                        actorId,
                        scopeId,
                        prompt,
                        turnId,
                        accessToken,
                        inputParts,
                        metadata,
                        llmControl,
                        CommandId: commandId,
                        CorrelationId: commandId,
                        ClientRequestId: clientRequestId,
                        CreateIfMissing: createIfMissing,
                        OwnerSubject: ownerSubject,
                        AgentProfileReference: agentProfileReference,
                        NyxIdCredentialKind: credentials.NyxIdCredentialKind,
                        InputPartsFingerprint: NyxIdChatPublicIdentity.CreateInputPartsFingerprint(rawInputParts),
                        ContextAttachments: request.ContextAttachments?.Select(static attachment => attachment.ToProto()).ToArray()),
                    EmitAsync,
                    null,
                    interactionCancellation.Token);
            }

            result = await WaitForStreamTerminalAsync(interactionTask, ct);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerGate,
                turnId,
                string.Equals(streamType, "action.continue", StringComparison.Ordinal)
                    ? "The action continuation failed. Please try again."
                    : "The chat request failed. Please try again.");
        }
        catch (NyxIdChatStreamDeadlineExceededException)
        {
            logger.LogWarning("NyxID chat stream timed out for actor {ActorId} turn {TurnId}", actorId, turnId);
            try
            {
                var wroteTimeout = await writerGate.WriteTerminalAsync(
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "STREAM_TIMEOUT",
                        "The chat request timed out. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
                if (wroteTimeout)
                    heartbeat.Stop();
            }
            finally
            {
                interactionDetached = true;
                CancelObserveAndDisposeInteraction(
                    interactionCancellation,
                    interactionTask!,
                    logger,
                    actorId,
                    turnId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            heartbeat.Stop();
            await writerGate.CloseAsync(CancellationToken.None);
            if (interactionTask is { IsCompleted: false })
            {
                interactionDetached = true;
                CancelObserveAndDisposeInteraction(
                    interactionCancellation,
                    interactionTask,
                    logger,
                    actorId,
                    turnId);
            }
        }
        catch (OperationCanceledException)
        {
            heartbeat.Stop();
            await writerGate.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            await writerGate.WriteTerminalAsync(
                token => writer.WriteRunErrorAsync(
                    turnId,
                    "STREAM_FAILURE",
                    "The chat request failed. Please try again.",
                    0,
                    token),
                CancellationToken.None);
        }
        finally
        {
            if (!interactionDetached)
                interactionCancellation.Dispose();
        }
    }

    /// <summary>
    /// Handles tool approval decisions from the frontend.
    /// Opens an SSE connection to stream the continuation chat response.
    /// </summary>
    private static async Task HandleApproveAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdApprovalRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        await HandleApproveCoreAsync(
            http,
            scopeId,
            actorId,
            request,
            admissionPort,
            interactionService,
            loggerFactory,
            clientRequestId: null,
            ct);

    private static async Task HandleApproveCoreAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdApprovalRequest request,
        IScopeResourceAdmissionPort admissionPort,
        ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        ILoggerFactory loggerFactory,
        string? clientRequestId,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var turnId = CreateTurnId(actorId, clientRequestId);

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var accessToken = ExtractNyxIdAccessToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Approve,
                    ct))
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var writer = new NyxIdChatSseWriter(http.Response);
        var writerGate = new NyxIdChatStreamWriterGate();
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerGate,
            logger,
            actorId,
            turnId,
            StreamKeepAliveInterval,
            ct);
        var interactionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<CommandInteractionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>>? interactionTask = null;
        var interactionDetached = false;
        try
        {
            await writer.StartAsync(ct);
            await writerGate.WriteAsync(
                token => writer.WriteRunStartedAsync(actorId, turnId, token),
                ct);
            heartbeat.Start();
            // Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
            // Approval continuation follows the same resolver-owned lookup path as chat streaming.
            // Missing actors are typed command start failures, not Host-side runtime probes.
            // This keeps endpoint lifecycle independent from the actor runtime implementation.
            interactionTask = interactionService.ExecuteAsync(
                new NyxIdApprovalCommand(
                    actorId,
                    request.RequestId,
                    request.Approved,
                    request.Reason ?? string.Empty,
                    turnId),
                async (evt, token) =>
                {
                    var isTerminal = IsTerminalFrame(evt);
                    if (isTerminal)
                    {
                        var wroteTerminal = await writerGate.WriteTerminalAsync(
                            async writeToken =>
                            {
                                await NyxIdChatAguiSseEventWriter.WriteAsync(
                                    evt,
                                    turnId,
                                    writer,
                                    writeToken);
                            },
                            token);
                        if (wroteTerminal)
                            heartbeat.Stop();
                        return;
                    }

                    await writerGate.WriteAsync(
                        async writeToken =>
                        {
                            await NyxIdChatAguiSseEventWriter.WriteAsync(
                                evt,
                                turnId,
                                writer,
                                writeToken);
                        },
                        token);
                },
                null,
                interactionCancellation.Token);
            var result = await WaitForStreamTerminalAsync(interactionTask, ct);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerGate,
                turnId,
                "The approval continuation failed. Please try again.");
        }
        catch (NyxIdChatStreamDeadlineExceededException)
        {
            logger.LogWarning("NyxID approval stream timed out for actor {ActorId} turn {TurnId}", actorId, turnId);
            try
            {
                var wroteTimeout = await writerGate.WriteTerminalAsync(
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "STREAM_TIMEOUT",
                        "The approval continuation timed out. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
                if (wroteTimeout)
                    heartbeat.Stop();
            }
            finally
            {
                interactionDetached = true;
                CancelObserveAndDisposeInteraction(
                    interactionCancellation,
                    interactionTask!,
                    logger,
                    actorId,
                    turnId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            heartbeat.Stop();
            await writerGate.CloseAsync(CancellationToken.None);
            if (interactionTask is { IsCompleted: false })
            {
                interactionDetached = true;
                CancelObserveAndDisposeInteraction(
                    interactionCancellation,
                    interactionTask,
                    logger,
                    actorId,
                    turnId);
            }
        }
        catch (OperationCanceledException)
        {
            heartbeat.Stop();
            await writerGate.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            await writerGate.WriteTerminalAsync(
                token => writer.WriteRunErrorAsync(
                    turnId,
                    "STREAM_FAILURE",
                    "The approval continuation failed. Please try again.",
                    0,
                    token),
                CancellationToken.None);
        }
        finally
        {
            if (!interactionDetached)
                interactionCancellation.Dispose();
        }
    }

    private static async Task HandleInteractionFailureAsync(
        CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus> result,
        NyxIdChatSseWriter writer,
        NyxIdChatStreamWriterGate writerGate,
        string turnId,
        string message)
    {
        if (result.Succeeded)
            return;

        var attachmentReason = ToAttachmentAdmissionFailureReason(result.Error);
        if (attachmentReason != ConversationContextAttachmentAdmissionFailureReason.Unspecified)
        {
            await writerGate.WriteTerminalAsync(
                token => writer.WriteRunErrorAsync(
                    turnId,
                    "ATTACHMENT_ADMISSION_DENIED",
                    ToAttachmentAdmissionMessage(attachmentReason),
                    ToAttachmentAdmissionWireName(attachmentReason),
                    0,
                    token),
                CancellationToken.None);
            return;
        }

        await writerGate.WriteTerminalAsync(
            token => writer.WriteRunErrorAsync(
                turnId,
                result.Error switch
                {
                    NyxIdChatStartError.ProjectionUnavailable => "PROJECTION_UNAVAILABLE",
                    NyxIdChatStartError.AdmissionUnavailable => "ADMISSION_UNAVAILABLE",
                    NyxIdChatStartError.ActorNotFound => "ACTOR_NOT_FOUND",
                    _ => "COMMAND_START_FAILED",
                },
                result.Error switch
                {
                    NyxIdChatStartError.ProjectionUnavailable => "NyxID chat projection pipeline is unavailable.",
                    NyxIdChatStartError.AdmissionUnavailable =>
                        "NyxID chat admission is unavailable for the requested Agent Profile or route.",
                    NyxIdChatStartError.ActorNotFound => "NyxID chat conversation was not found.",
                    _ => message,
                },
                0,
                token),
            CancellationToken.None);
    }

    private static ConversationContextAttachmentAdmissionFailureReason ToAttachmentAdmissionFailureReason(
        NyxIdChatStartError error) => error switch
        {
            NyxIdChatStartError.AttachmentNotFound =>
                ConversationContextAttachmentAdmissionFailureReason.NotFound,
            NyxIdChatStartError.AttachmentAccessDenied =>
                ConversationContextAttachmentAdmissionFailureReason.AccessDenied,
            NyxIdChatStartError.AttachmentUnsupportedKind =>
                ConversationContextAttachmentAdmissionFailureReason.UnsupportedKind,
            NyxIdChatStartError.AttachmentOverLimit =>
                ConversationContextAttachmentAdmissionFailureReason.OverLimit,
            NyxIdChatStartError.AttachmentPinnedRevisionUnavailable =>
                ConversationContextAttachmentAdmissionFailureReason.PinnedRevisionUnavailable,
            NyxIdChatStartError.AttachmentInvalidRequest =>
                ConversationContextAttachmentAdmissionFailureReason.InvalidRequest,
            NyxIdChatStartError.AttachmentInactive =>
                ConversationContextAttachmentAdmissionFailureReason.Inactive,
            NyxIdChatStartError.AttachmentReadModelUnavailable =>
                ConversationContextAttachmentAdmissionFailureReason.ReadModelUnavailable,
            _ => ConversationContextAttachmentAdmissionFailureReason.Unspecified,
        };

    private static string ToAttachmentAdmissionWireName(
        ConversationContextAttachmentAdmissionFailureReason reason) => reason switch
        {
            ConversationContextAttachmentAdmissionFailureReason.NotFound => "not_found",
            ConversationContextAttachmentAdmissionFailureReason.AccessDenied => "access_denied",
            ConversationContextAttachmentAdmissionFailureReason.UnsupportedKind => "unsupported_kind",
            ConversationContextAttachmentAdmissionFailureReason.OverLimit => "over_limit",
            ConversationContextAttachmentAdmissionFailureReason.PinnedRevisionUnavailable =>
                "pinned_revision_unavailable",
            ConversationContextAttachmentAdmissionFailureReason.InvalidRequest => "invalid_request",
            ConversationContextAttachmentAdmissionFailureReason.Inactive => "inactive",
            ConversationContextAttachmentAdmissionFailureReason.ReadModelUnavailable =>
                "read_model_unavailable",
            _ => "unspecified",
        };

    private static string ToAttachmentAdmissionMessage(
        ConversationContextAttachmentAdmissionFailureReason reason) => reason switch
        {
            ConversationContextAttachmentAdmissionFailureReason.NotFound =>
                "A requested context attachment was not found.",
            ConversationContextAttachmentAdmissionFailureReason.AccessDenied =>
                "Access to a requested context attachment was denied.",
            ConversationContextAttachmentAdmissionFailureReason.UnsupportedKind =>
                "A requested context attachment kind is unsupported.",
            ConversationContextAttachmentAdmissionFailureReason.OverLimit =>
                $"A conversation can bind at most {ConversationContextAttachmentAdmission.MaximumAttachments} context attachments.",
            ConversationContextAttachmentAdmissionFailureReason.PinnedRevisionUnavailable =>
                "A requested pinned context attachment revision is unavailable.",
            ConversationContextAttachmentAdmissionFailureReason.InvalidRequest =>
                "The context attachment declaration is invalid.",
            ConversationContextAttachmentAdmissionFailureReason.Inactive =>
                "A requested context attachment is inactive.",
            ConversationContextAttachmentAdmissionFailureReason.ReadModelUnavailable =>
                "Context attachment admission is temporarily unavailable.",
            _ => "Context attachment admission was denied.",
        };

    private static bool IsTerminalFrame(AGUIEvent evt) =>
        evt.EventCase is AGUIEvent.EventOneofCase.RunFinished or AGUIEvent.EventOneofCase.RunError;

    private static string? ResolveClientRequestId(HttpContext http, string? clientRequestId)
    {
        if (!string.IsNullOrWhiteSpace(clientRequestId))
            return clientRequestId.Trim();

        var headerValue = http.Request.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(headerValue) ? null : headerValue.Trim();
    }

    private static bool TryMapAgentProfileReference(
        NyxIdChatAgentProfileReferenceDto input,
        out AgentProfileReference? reference)
    {
        reference = null;
        var profileSlug = input.ProfileSlug?.Trim() ?? string.Empty;
        var ownerKind = input.OwnerKind?.Trim().ToLowerInvariant() switch
        {
            "caller" => AgentProfileReferenceOwnerKind.Caller,
            "system" => AgentProfileReferenceOwnerKind.System,
            _ => AgentProfileReferenceOwnerKind.Unspecified,
        };
        if (ownerKind == AgentProfileReferenceOwnerKind.Unspecified || profileSlug.Length == 0)
            return false;

        reference = new AgentProfileReference
        {
            OwnerKind = ownerKind,
            ProfileSlug = profileSlug,
        };
        return true;
    }

    private static bool TryMapActionReports(
        IReadOnlyList<NyxIdChatActionReportDto>? reports,
        string originTurnId,
        out IReadOnlyList<NyxIdChatActionReport> mapped)
    {
        mapped = [];
        if (reports is null)
            return false;
        if (reports.Count == 0)
            return string.IsNullOrEmpty(originTurnId);
        if (!TryValidateControlIdentity(originTurnId, out originTurnId))
            return false;

        var values = new List<NyxIdChatActionReport>(reports.Count);
        var actionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            if (report is null ||
                !TryValidateControlIdentity(report.ActionRequestId, out var actionRequestId) ||
                !TryValidateControlIdentity(report.OriginTurnId, out var reportOriginTurnId) ||
                !string.Equals(reportOriginTurnId, originTurnId, StringComparison.Ordinal) ||
                !actionIds.Add(actionRequestId) ||
                !TryParseActionDisposition(report.Disposition, out var disposition) ||
                !TryMapActionResource(report.Resource, out var resource))
            {
                return false;
            }

            var value = new NyxIdChatActionReport
            {
                ActionRequestId = actionRequestId,
                OriginTurnId = reportOriginTurnId,
                Disposition = disposition,
            };
            if (resource is not null)
                value.Resource = resource;
            values.Add(value);
        }

        mapped = values;
        return true;
    }

    private static async Task<bool> TryEnsureActionContinuationCredentialVisibilityAsync(
        HttpContext http,
        IReadOnlyList<NyxIdChatActionReport> actionReports,
        string bearerToken,
        CancellationToken ct)
    {
        var userServiceIds = actionReports
            .Where(static report =>
                report.Disposition == NyxIdChatActionDisposition.Completed &&
                report.Resource?.ResourceCase ==
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService)
            .Select(static report => report.Resource.UserService.UserServiceId)
            .Where(static userServiceId => !string.IsNullOrWhiteSpace(userServiceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (userServiceIds.Length == 0)
            return true;

        var visibilityPort = http.RequestServices.GetService<
            INyxIdActionContinuationCredentialVisibilityPort>();
        if (visibilityPort is null)
        {
            await WriteActionContinuationVisibilityFailureAsync(
                http,
                StatusCodes.Status503ServiceUnavailable,
                ActionContinuationCatalogUnavailableCode,
                "The NyxID action continuation catalog is temporarily unavailable.",
                ct);
            return false;
        }

        try
        {
            foreach (var userServiceId in userServiceIds)
            {
                var visibility = await visibilityPort
                    .InspectUserServiceAsync(bearerToken, userServiceId, ct)
                    .ConfigureAwait(false);
                switch (visibility.Status)
                {
                    case NyxIdActionContinuationCredentialVisibilityStatus.Visible:
                        continue;
                    case NyxIdActionContinuationCredentialVisibilityStatus.CredentialRefreshRequired:
                        await WriteActionContinuationVisibilityFailureAsync(
                            http,
                            StatusCodes.Status401Unauthorized,
                            ActionContinuationCredentialRefreshRequiredCode,
                            "The action continuation requires a refreshed NyxID credential.",
                            ct);
                        return false;
                    default:
                        await WriteActionContinuationVisibilityFailureAsync(
                            http,
                            StatusCodes.Status503ServiceUnavailable,
                            ActionContinuationCatalogUnavailableCode,
                            "The NyxID action continuation catalog is temporarily unavailable.",
                            ct);
                        return false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteActionContinuationVisibilityFailureAsync(
                http,
                StatusCodes.Status503ServiceUnavailable,
                ActionContinuationCatalogUnavailableCode,
                "The NyxID action continuation catalog is temporarily unavailable.",
                ct);
            return false;
        }

        return true;
    }

    private static Task WriteActionContinuationVisibilityFailureAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        return http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    private static bool TryParseActionDisposition(
        string? value,
        out NyxIdChatActionDisposition disposition)
    {
        disposition = value?.Trim() switch
        {
            "completed" => NyxIdChatActionDisposition.Completed,
            "declined" => NyxIdChatActionDisposition.Declined,
            "failed" => NyxIdChatActionDisposition.Failed,
            "cancelled" => NyxIdChatActionDisposition.Cancelled,
            "expired" => NyxIdChatActionDisposition.Expired,
            _ => NyxIdChatActionDisposition.Unspecified,
        };
        return disposition != NyxIdChatActionDisposition.Unspecified;
    }

    private static bool TryMapActionResource(
        NyxIdChatActionResourceDto? resource,
        out NyxIdChatSafeResourceRef? mapped)
    {
        mapped = null;
        if (resource is null)
            return true;

        var variants = new object?[]
        {
            resource.UserService,
            resource.Key,
            resource.Node,
            resource.ServiceAccount,
            resource.DeveloperApp,
            resource.Device,
        };
        if (variants.Count(static value => value is not null) != 1)
            return false;

        if (resource.UserService is { } userService &&
            TryValidateControlIdentity(userService.UserServiceId, out var userServiceId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef { UserServiceId = userServiceId },
            };
            return true;
        }

        if (resource.Key is { } key && TryValidateControlIdentity(key.KeyId, out var keyId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Key = new NyxIdChatKeyRef { KeyId = keyId },
            };
            return true;
        }

        if (resource.Node is { } node && TryValidateControlIdentity(node.NodeId, out var nodeId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Node = new NyxIdChatNodeRef { NodeId = nodeId },
            };
            return true;
        }

        if (resource.ServiceAccount is { } serviceAccount &&
            TryValidateControlIdentity(serviceAccount.ServiceAccountId, out var serviceAccountId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                ServiceAccount = new NyxIdChatServiceAccountRef
                {
                    ServiceAccountId = serviceAccountId,
                },
            };
            return true;
        }

        if (resource.DeveloperApp is { } developerApp &&
            TryValidateControlIdentity(developerApp.ClientId, out var clientId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                DeveloperApp = new NyxIdChatDeveloperAppRef { ClientId = clientId },
            };
            return true;
        }

        if (resource.Device is { } device &&
            TryValidateControlIdentity(device.DeviceId, out var deviceId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Device = new NyxIdChatDeviceRef { DeviceId = deviceId },
            };
            return true;
        }

        return false;
    }

    private static string CreateTurnId(string actorId, string? clientRequestId)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId))
            return $"turn-{Guid.NewGuid():N}";

        var normalizedActorId = actorId?.Trim() ?? string.Empty;
        var normalizedClientRequestId = clientRequestId.Trim();
        var identity = $"{normalizedActorId.Length}:{normalizedActorId}{normalizedClientRequestId.Length}:{normalizedClientRequestId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"turn-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static TimeSpan ResolveStreamTerminalTimeout() =>
        StreamTerminalTimeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : StreamTerminalTimeout;

    private static async Task<T> WaitForStreamTerminalAsync<T>(
        Task<T> interactionTask,
        CancellationToken requestCancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellationToken);
        deadline.CancelAfter(ResolveStreamTerminalTimeout());
        try
        {
            return await interactionTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!requestCancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new NyxIdChatStreamDeadlineExceededException(exception);
        }
    }

    private static void CancelObserveAndDisposeInteraction(
        CancellationTokenSource cancellation,
        Task interactionTask,
        ILogger logger,
        string actorId,
        string turnId)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "NyxID detached interaction cancellation failed: actor={ActorId} turn={TurnId} exceptionType={ExceptionType}",
                actorId,
                turnId,
                exception.GetType().Name);
        }
        _ = interactionTask.ContinueWith(
            static (task, state) =>
            {
                var (detachedCancellation, detachedLogger, detachedActorId, detachedTurnId) =
                    ((CancellationTokenSource, ILogger, string, string))state!;
                try
                {
                    var exception = task.Exception?.GetBaseException();
                    if (exception != null)
                    {
                        detachedLogger.LogWarning(
                            "NyxID detached interaction failed after stream closure: actor={ActorId} turn={TurnId} exceptionType={ExceptionType}",
                            detachedActorId,
                            detachedTurnId,
                            exception.GetType().Name);
                    }
                }
                finally
                {
                    detachedCancellation.Dispose();
                }
            },
            (cancellation, logger, actorId, turnId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class NyxIdChatStreamWriterGate
    {
        private readonly SemaphoreSlim _writerLock = new(1, 1);
        private bool _closed;

        public async ValueTask WriteAsync(
            Func<CancellationToken, ValueTask> writeAsync,
            CancellationToken ct)
        {
            await _writerLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_closed)
                    return;

                await writeAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writerLock.Release();
            }
        }

        public async ValueTask<bool> WriteTerminalAsync(
            Func<CancellationToken, ValueTask> writeAsync,
            CancellationToken ct)
        {
            await _writerLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_closed)
                    return false;

                _closed = true;
                await writeAsync(ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writerLock.Release();
            }
        }

        public async ValueTask CloseAsync(CancellationToken ct)
        {
            await _writerLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _closed = true;
            }
            finally
            {
                _writerLock.Release();
            }
        }
    }

    private sealed class NyxIdChatStreamDeadlineExceededException(Exception innerException)
        : Exception("The NyxID stream wall-clock deadline elapsed.", innerException);

    private sealed class NyxIdChatStreamKeepAlive : IAsyncDisposable
    {
        private readonly NyxIdChatSseWriter _writer;
        private readonly NyxIdChatStreamWriterGate _writerGate;
        private readonly ILogger _logger;
        private readonly string _actorId;
        private readonly string _turnId;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts;
        private Task? _loop;

        public NyxIdChatStreamKeepAlive(
            NyxIdChatSseWriter writer,
            NyxIdChatStreamWriterGate writerGate,
            ILogger logger,
            string actorId,
            string turnId,
            TimeSpan interval,
            CancellationToken requestCancellationToken)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _writerGate = writerGate ?? throw new ArgumentNullException(nameof(writerGate));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _actorId = actorId;
            _turnId = turnId;
            _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : interval;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        }

        public void Start()
        {
            if (_loop != null)
                return;

            _loop = RunAsync();
        }

        public void Stop() => _cts.Cancel();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_loop != null)
            {
                try
                {
                    await _loop;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    await _writerGate.WriteAsync(
                        token => _writer.WriteKeepAliveAsync(_actorId, _turnId, token),
                        _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NyxID chat keepalive stopped for actor {ActorId}", _actorId);
            }
        }
    }

    public sealed record NyxIdApprovalRequest(
        string? RequestId,
        bool Approved = true,
        string? Reason = null,
        [property: Obsolete("sessionId is deprecated and ignored. Approval continuation turn identity is server-owned.")]
        string? SessionId = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        [property: Obsolete("sessionId is deprecated and ignored. Use clientRequestId for retry idempotency.")]
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null,
        string? ClientRequestId = null,
        string? Type = null,
        string? OriginTurnId = null,
        IReadOnlyList<NyxIdChatActionReportDto>? Actions = null,
        NyxIdChatAgentProfileReferenceDto? AgentProfile = null,
        IReadOnlyList<NyxIdChatContextAttachmentDto>? ContextAttachments = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatContextAttachmentDto(
        string? ArtifactId,
        string? RevisionMode,
        string? PinnedRevisionId)
    {
        public ConversationContextAttachment ToProto() =>
            new()
            {
                ArtifactId = ArtifactId?.Trim() ?? string.Empty,
                RevisionMode = RevisionMode?.Trim().ToUpperInvariant() switch
                {
                    "FOLLOW_CURRENT" => ConversationContextAttachmentRevisionMode.FollowCurrent,
                    "PINNED_REVISION" => ConversationContextAttachmentRevisionMode.PinnedRevision,
                    _ => ConversationContextAttachmentRevisionMode.Unspecified,
                },
                PinnedRevisionId = PinnedRevisionId?.Trim() ?? string.Empty,
            };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatAgentProfileReferenceDto(
        string? OwnerKind,
        string? ProfileSlug);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatActionReportDto(
        string? ActionRequestId,
        string? OriginTurnId,
        string? Disposition,
        NyxIdChatActionResourceDto? Resource = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatActionResourceDto(
        NyxIdChatUserServiceRefDto? UserService = null,
        NyxIdChatKeyRefDto? Key = null,
        NyxIdChatNodeRefDto? Node = null,
        NyxIdChatServiceAccountRefDto? ServiceAccount = null,
        NyxIdChatDeveloperAppRefDto? DeveloperApp = null,
        NyxIdChatDeviceRefDto? Device = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatUserServiceRefDto(string? UserServiceId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatKeyRefDto(string? KeyId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatNodeRefDto(string? NodeId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatServiceAccountRefDto(string? ServiceAccountId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatDeveloperAppRefDto(string? ClientId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatDeviceRefDto(string? DeviceId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ContentPartDto(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null)
    {
        public ChatContentPart ToProto() => new()
        {
            Kind = Type?.ToLowerInvariant() switch
            {
                "image" => ChatContentPartKind.Image,
                "audio" => ChatContentPartKind.Audio,
                "video" => ChatContentPartKind.Video,
                "text" => ChatContentPartKind.Text,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = Text ?? string.Empty,
            DataBase64 = DataBase64 ?? string.Empty,
            MediaType = MediaType ?? string.Empty,
            Uri = Uri ?? string.Empty,
            Name = Name ?? string.Empty,
        };
    }

    private static async ValueTask<InlineInputPartsResult> MaterializeInlineInputPartsAsync(
        IReadOnlyList<ContentPartDto>? parts,
        string scopeId,
        string turnId,
        IFileArtifactIngressPort? fileIngressPort,
        CancellationToken ct)
    {
        if (parts is not { Count: > 0 })
            return InlineInputPartsResult.Success([]);

        var normalized = new List<ChatContentPart>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part is null)
                return InlineInputPartsResult.Invalid();

            var proto = part.ToProto();
            if (string.IsNullOrWhiteSpace(part.DataBase64))
            {
                normalized.Add(proto);
                continue;
            }

            if (fileIngressPort is null)
                throw new InvalidOperationException("File artifact ingress is unavailable for inline chat input.");

            byte[] content;
            try
            {
                content = Convert.FromBase64String(part.DataBase64);
            }
            catch (FormatException)
            {
                return InlineInputPartsResult.Invalid();
            }

            if (content.Length is 0 or > MaxInlineInputPartBytes)
                return InlineInputPartsResult.Invalid();

            var ingress = await fileIngressPort.IngestAsync(
                new FileArtifactIngressRequest(
                    content,
                    FileArtifactSourceKind.ChatInput,
                    SourceMessageId: turnId,
                    SourceResourceKey: $"inline-{index}",
                    FileName: NormalizeInlineInputValue(part.Name),
                    MediaType: NormalizeInlineInputValue(part.MediaType),
                    OwnerScopeId: scopeId),
                ct);
            proto.DataBase64 = string.Empty;
            proto.FileRef = ToChatFileRef(ingress.FileRef);
            proto.Uri = ingress.FileRef.ArtifactId ?? proto.Uri;
            proto.MediaType = ingress.FileRef.MediaType ?? proto.MediaType;
            proto.Name = ingress.FileRef.FileName ?? proto.Name;
            normalized.Add(proto);
        }

        return InlineInputPartsResult.Success(normalized);
    }

    private static Aevatar.AI.Abstractions.ChatFileRef ToChatFileRef(FileArtifactRef source) =>
        new()
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKind = source.SourceKind switch
            {
                FileArtifactSourceKind.ChatInput => Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                FileArtifactSourceKind.FormUpload => Aevatar.AI.Abstractions.ChatFileSourceKind.FormUpload,
                FileArtifactSourceKind.ConnectedServiceResource => Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
                FileArtifactSourceKind.ExternalResource => Aevatar.AI.Abstractions.ChatFileSourceKind.ExternalResource,
                FileArtifactSourceKind.Generated => Aevatar.AI.Abstractions.ChatFileSourceKind.Generated,
                _ => Aevatar.AI.Abstractions.ChatFileSourceKind.Unspecified,
            },
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };

    private static string? NormalizeInlineInputValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct InlineInputPartsResult(
        bool Succeeded,
        IReadOnlyList<ChatContentPart> Parts)
    {
        public static InlineInputPartsResult Success(IReadOnlyList<ChatContentPart> parts) =>
            new(true, parts);

        public static InlineInputPartsResult Invalid() => new(false, []);
    }
}
