using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed class ChannelConversationTurnRunner : IConversationTurnRunner
{
    private readonly IServiceProvider _services;
    private readonly IChannelBotRegistrationQueryPort _registrationQueryPort;
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort? _registrationQueryByNyxIdentityPort;
    private readonly IEnumerable<IPlatformAdapter> _platformAdapters;
    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdRelayOutboundPort _relayOutboundPort;
    private readonly IInteractiveReplyDispatcher? _interactiveReplyDispatcher;
    private readonly ILogger<ChannelConversationTurnRunner> _logger;

    public ChannelConversationTurnRunner(
        IServiceProvider services,
        IChannelBotRegistrationQueryPort registrationQueryPort,
        IChannelBotRegistrationQueryByNyxIdentityPort? registrationQueryByNyxIdentityPort,
        IEnumerable<IPlatformAdapter> platformAdapters,
        NyxIdApiClient nyxClient,
        NyxIdRelayOutboundPort relayOutboundPort,
        IInteractiveReplyDispatcher? interactiveReplyDispatcher,
        ILogger<ChannelConversationTurnRunner> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrationQueryPort = registrationQueryPort ?? throw new ArgumentNullException(nameof(registrationQueryPort));
        _registrationQueryByNyxIdentityPort = registrationQueryByNyxIdentityPort;
        _platformAdapters = platformAdapters ?? throw new ArgumentNullException(nameof(platformAdapters));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _relayOutboundPort = relayOutboundPort ?? throw new ArgumentNullException(nameof(relayOutboundPort));
        _interactiveReplyDispatcher = interactiveReplyDispatcher;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConversationTurnResult> RunInboundAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var registration = await ResolveRegistrationAsync(activity, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        // Capture the typing-reaction Task instead of `_ =`-discarding it. The direct-reply
        // AgentBuilder path can complete fast enough that the swap fires before Lark has
        // persisted the typing reaction; the swap GET would then find nothing to delete and
        // leave both Typing + DONE on the message. Threading the task to the swap site lets
        // the swap await-with-timeout the typing POST first. The deferred-LLM and streaming
        // paths don't get this task (different invocation), but their natural latency is
        // orders of magnitude greater than the typing POST so the race cannot fire.
        var typingReactionTask = TrySendImmediateLarkReactionAsync(activity, registration, ct);

        var inbound = ToInboundMessage(activity);
        if (await TryHandleWorkflowResumeAsync(inbound, ct) is { } workflowResumeResult)
            return workflowResumeResult;

        var inboundEvent = ToInboundEvent(activity, registration, inbound, ResolveUserAccessToken(activity));

        if (await TryHandleAgentBuilderAsync(activity, inboundEvent, registration, runtimeContext, typingReactionTask, ct) is { } agentBuilderResult)
            return agentBuilderResult;

        if (activity.Type == ActivityType.CardAction)
        {
            // A card_action that survived both routers has no actionable meaning for this
            // bot: promoting it into an LLM turn would send a blank user message and waste
            // a model call. Return a no-reply completion instead of falling through.
            _logger.LogInformation(
                "Ignoring unrecognized card_action inbound: activity={ActivityId}, conversation={CanonicalKey}, actionId={ActionId}",
                activity.Id,
                activity.Conversation?.CanonicalKey,
                activity.Content?.CardAction?.ActionId);
            return ConversationTurnResult.Ignored(
                "unrecognized_card_action",
                activity.Id,
                "Card action payload did not match workflow resume or agent-builder routing.");
        }

        if (string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
        {
            return ConversationTurnResult.PermanentFailure(
                "conversation_not_found",
                "Conversation routing target is missing.");
        }

        return ConversationTurnResult.LlmReplyRequested(
            BuildLlmReplyRequest(activity, registration, inboundEvent, runtimeContext));
    }

    public Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, CancellationToken ct) =>
        RunInboundAsync(activity, ConversationTurnRuntimeContext.Empty, ct);

    public async Task<ConversationTurnResult> RunLlmReplyAsync(
        LlmReplyReadyEvent reply,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (reply.Activity is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "activity_required",
                "Deferred LLM reply is missing the source activity.");
        }

        var outboundIntent = reply.Outbound?.Clone() ?? new MessageContent();
        if (!HasContent(outboundIntent))
        {
            return ConversationTurnResult.TransientFailure(
                string.IsNullOrWhiteSpace(reply.ErrorCode) ? "empty_reply" : reply.ErrorCode,
                string.IsNullOrWhiteSpace(reply.ErrorSummary)
                    ? "Deferred LLM reply is empty."
                    : reply.ErrorSummary);
        }

        var inbound = ToInboundMessage(reply.Activity);
        // Resolve registration even on the relay path (where SendReplyAsync itself does not need
        // it) so the post-reply Lark typing→done reaction swap can find NyxProviderSlug. Skipping
        // resolution here only when HasRelayDelivery would silently disable the swap on the most
        // common production path. Direct path still requires registration to send the reply.
        var registration = await ResolveRegistrationForReplyAsync(reply, ct);
        if (!HasRelayDelivery(inbound) && registration is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "registration_not_found",
                "Channel registration not found.");
        }

        var sentSeed = string.IsNullOrWhiteSpace(reply.CorrelationId)
            ? reply.Activity.Id
            : reply.CorrelationId;
        var result = await SendReplyAsync(
            outboundIntent,
            sentSeed,
            reply.Activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);
        if (result.Success)
            _ = TrySwapTypingReactionToDoneAsync(inbound, registration, ct);
        return result;
    }

    public Task<ConversationTurnResult> RunLlmReplyAsync(LlmReplyReadyEvent reply, CancellationToken ct) =>
        RunLlmReplyAsync(reply, ConversationTurnRuntimeContext.Empty, ct);

    public async Task<ConversationTurnResult> RunContinueAsync(
        ConversationContinueRequestedEvent command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Kind == PrincipalKind.OnBehalfOfUser)
        {
            return ConversationTurnResult.PermanentFailure(
                "unsupported_auth_context",
                "Legacy Lark outbound bridge does not support delegated proactive sends.");
        }

        var registration = await ResolveRegistrationAsync(command.Conversation?.Bot?.Value, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        var conversationId = ResolveRoutingConversationId(command.Conversation);
        if (string.IsNullOrWhiteSpace(conversationId))
            return ConversationTurnResult.PermanentFailure("conversation_not_found", "Conversation routing target is missing.");

        var inbound = new InboundMessage
        {
            Platform = registration.Platform,
            ConversationId = conversationId,
            SenderId = command.OnBehalfOfUserId ?? string.Empty,
            SenderName = string.Empty,
            Text = command.Payload?.Text ?? string.Empty,
            MessageId = command.CommandId,
            ChatType = ResolveChatType(command.Conversation),
        };

        return await SendReplyAsync(
            command.Payload?.Clone() ?? new MessageContent(),
            command.CommandId,
            command.Conversation,
            inbound,
            registration,
            ConversationTurnRuntimeContext.Empty,
            ct);
    }

    public async Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct)
    {
        // Streaming-completion path in ConversationGAgent calls this hook because it finalizes
        // the reply without going through RunLlmReplyAsync (which is where the non-streaming swap
        // lives). For non-Lark platforms or activities missing the platform message id, the swap
        // helper short-circuits in ShouldSwapTypingReaction.
        if (activity is null)
            return;

        var registration = await ResolveRegistrationAsync(activity, ct);
        if (registration is null)
            return;

        var inbound = ToInboundMessage(activity);
        await TrySwapTypingReactionToDoneAsync(inbound, registration, ct);
    }

    public async Task<ConversationStreamChunkResult> RunStreamChunkAsync(
        LlmReplyStreamChunkEvent chunk,
        string? currentPlatformMessageId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.Activity is null)
        {
            return ConversationStreamChunkResult.Failed(
                "activity_required",
                "Stream chunk event is missing the source activity.");
        }

        var inbound = ToInboundMessage(chunk.Activity);
        if (!HasRelayDelivery(inbound))
        {
            return ConversationStreamChunkResult.Failed(
                "invalid_delivery",
                "Stream chunk requires a relay outbound delivery context.");
        }

        var relayDelivery = inbound.OutboundDelivery!.Clone();
        var relayToken = ResolveRelayReplyToken(relayDelivery, runtimeContext);
        if (relayToken is null)
        {
            return ConversationStreamChunkResult.Failed(
                "reply_token_missing_or_expired",
                "Nyx relay reply token is missing or expired for this streaming chunk.");
        }

        var conversation = chunk.Activity.Conversation;
        var platform = ResolveRelayPlatform(inbound, conversation);
        var content = new MessageContent { Text = NormalizeReplyText(chunk.AccumulatedText) };

        EmitResult emit;
        if (string.IsNullOrWhiteSpace(currentPlatformMessageId))
        {
            emit = await _relayOutboundPort.SendAsync(
                platform,
                conversation?.Clone() ?? new ConversationReference(),
                content,
                relayDelivery,
                relayToken,
                ct);
        }
        else
        {
            emit = await _relayOutboundPort.UpdateAsync(
                platform,
                conversation?.Clone() ?? new ConversationReference(),
                content,
                relayDelivery,
                currentPlatformMessageId,
                relayToken,
                ct);
        }

        if (!emit.Success)
        {
            var editUnsupported = string.Equals(
                emit.ErrorCode,
                "relay_reply_edit_unsupported",
                StringComparison.Ordinal);
            return ConversationStreamChunkResult.Failed(
                string.IsNullOrWhiteSpace(emit.ErrorCode) ? "stream_chunk_rejected" : emit.ErrorCode,
                emit.ErrorMessage ?? "Relay stream chunk rejected.",
                editUnsupported);
        }

        var resolvedPlatformMessageId = string.IsNullOrWhiteSpace(emit.PlatformMessageId)
            ? currentPlatformMessageId
            : emit.PlatformMessageId;
        return ConversationStreamChunkResult.Succeeded(resolvedPlatformMessageId);
    }

    private async Task<ConversationTurnResult?> TryHandleAgentBuilderAsync(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        Task typingReactionTask,
        CancellationToken ct)
    {
        AgentBuilderFlowDecision? decision = null;
        var relayDecisionMatched = NyxRelayAgentBuilderFlow.TryResolve(inboundEvent, out decision);
        if (!relayDecisionMatched &&
            ((decision = await AgentBuilderCardFlow.TryResolveAsync(
                    inboundEvent,
                    _services.GetService<IUserConfigQueryPort>(),
                    ct)) is null))
        {
            // No slash-command/card flow matched.
        }

        if (decision is null)
            return null;

        var replyContent = decision.ReplyContent ?? new MessageContent { Text = decision.ReplyPayload };
        if (decision.RequiresToolExecution)
        {
            var previousMetadata = AgentToolRequestContext.CurrentMetadata;
            try
            {
                AgentToolRequestContext.CurrentMetadata = BuildAgentBuilderMetadata(
                    activity,
                    inboundEvent,
                    ResolveUserAccessToken(activity));
                var tool = ActivatorUtilities.CreateInstance<AgentBuilderTool>(_services);
                var toolResult = await tool.ExecuteAsync(decision.ToolArgumentsJson!, ct);
                replyContent = relayDecisionMatched
                    ? NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResult)
                    : AgentBuilderCardFlow.FormatToolResult(decision, toolResult);
            }
            finally
            {
                AgentToolRequestContext.CurrentMetadata = previousMetadata;
            }
        }

        var inbound = ToInboundMessage(activity);
        var result = await SendReplyAsync(
            replyContent,
            activity.Id,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);
        if (result.Success)
            _ = AwaitTypingReactionThenSwapAsync(typingReactionTask, inbound, registration, ct);
        return result.Success
            ? ConversationTurnResult.Sent(
                sentActivityId: $"direct-reply:{activity.Id}",
                outbound: replyContent.Clone(),
                authPrincipal: "bot",
                outboundDelivery: result.OutboundDelivery?.Clone())
            : result;
    }

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        await SendReplyAsync(
            new MessageContent { Text = replyText },
            activity.Id,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);

    private async Task<ConversationTurnResult> SendReplyAsync(
        MessageContent outboundIntent,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outboundIntent);

        if (HasRelayDelivery(inbound))
        {
            var relayDelivery = inbound.OutboundDelivery!.Clone();
            var relayToken = ResolveRelayReplyToken(relayDelivery, runtimeContext);
            if (relayToken is null)
            {
                return ConversationTurnResult.PermanentFailure(
                    "reply_token_missing_or_expired",
                    "Nyx relay reply token is missing or expired for this conversation turn.");
            }

            if (await TrySendInteractiveRelayReplyAsync(
                    outboundIntent,
                    sentActivitySeed,
                    conversation,
                    inbound,
                    relayDelivery,
                    relayToken,
                    ct) is { } interactiveResult)
            {
                return interactiveResult;
            }

            var emit = await _relayOutboundPort.SendAsync(
                ResolveRelayPlatform(inbound, conversation),
                conversation?.Clone() ?? new ConversationReference(),
                outboundIntent,
                relayDelivery,
                relayToken,
                ct);
            return emit.Success
                ? BuildRelaySentResult(
                    emit.SentActivityId,
                    sentActivitySeed,
                    outboundIntent,
                    relayDelivery)
                : ToRelayFailure(emit);
        }

        if (registration is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "registration_not_found",
                "Channel registration not found.");
        }

        var adapter = _platformAdapters.FirstOrDefault(platformAdapter =>
            string.Equals(platformAdapter.Platform, registration.Platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "adapter_not_found",
                $"No platform adapter registered for '{registration.Platform}'.");
        }

        var replyText = NormalizeReplyText(
            string.IsNullOrWhiteSpace(outboundIntent.Text) && HasInteractiveContent(outboundIntent)
                ? NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(outboundIntent)
                : outboundIntent.Text);
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return ConversationTurnResult.TransientFailure(
                "empty_reply",
                "Deferred LLM reply is empty.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var replyService = _services.GetService<ChannelPlatformReplyService>();
        var delivery = replyService is not null
            ? await replyService.DeliverAsync(adapter, replyText, inbound, registration, cts.Token)
            : await adapter.SendReplyAsync(replyText, inbound, registration, _nyxClient, cts.Token);
        if (!delivery.Succeeded)
        {
            _logger.LogWarning(
                "Channel conversation reply rejected: registration={RegistrationId}, detail={Detail}, kind={Kind}",
                registration.Id,
                delivery.Detail,
                delivery.FailureKind);
            return delivery.FailureKind == PlatformReplyFailureKind.Permanent
                ? ConversationTurnResult.PermanentFailure("reply_rejected", delivery.Detail ?? "reply rejected")
                : ConversationTurnResult.TransientFailure("reply_rejected", delivery.Detail ?? "reply rejected");
        }

        return ConversationTurnResult.Sent(
            sentActivityId: $"direct-reply:{sentActivitySeed}",
            outbound: new MessageContent { Text = replyText },
            authPrincipal: "bot",
            outboundDelivery: inbound.OutboundDelivery?.Clone());
    }

    private async Task<ConversationTurnResult?> TrySendInteractiveRelayReplyAsync(
        MessageContent outboundIntent,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        OutboundDeliveryContext relayDelivery,
        string relayToken,
        CancellationToken ct)
    {
        if (!HasInteractiveContent(outboundIntent))
            return null;

        var fallbackText = NormalizeReplyText(NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(outboundIntent));
        if (_interactiveReplyDispatcher is null)
        {
            _logger.LogWarning(
                "Interactive relay reply requested without dispatcher; degrading to text. messageId={MessageId}",
                relayDelivery.ReplyMessageId);
            return await SendRelayTextFallbackAsync(
                fallbackText,
                sentActivitySeed,
                conversation,
                inbound,
                relayDelivery,
                relayToken,
                ct);
        }

        var dispatch = await _interactiveReplyDispatcher.DispatchAsync(
            ResolveRelayChannel(inbound, conversation),
            relayDelivery.ReplyMessageId,
            relayToken,
            outboundIntent,
            new ComposeContext
            {
                Conversation = conversation?.Clone() ?? new ConversationReference(),
            },
            ct);
        if (dispatch.Succeeded)
        {
            var delivered = dispatch.FellBackToText
                ? new MessageContent { Text = fallbackText }
                : outboundIntent.Clone();
            return BuildRelaySentResult(
                dispatch.MessageId,
                sentActivitySeed,
                delivered,
                relayDelivery);
        }

        // The dispatcher has already consumed the relay reply token via NyxID's
        // `channel-relay/reply` endpoint — even when the upstream returns 5xx, NyxID's
        // single-use semantics mark the token as used before the failure surfaces. A second
        // call with the same token (the previous "degrade to text" retry) lands as
        // `401 Reply token already used`, which then escapes as a hard relay failure and
        // queues an inbound turn retry that re-consumes the (already gone) token forever
        // — observed in production after PR #409 introduced interactive cards: NyxID
        // returned 502 for the card payload, the legacy fallback re-sent as text and got
        // 401, and the bot looked silent on every subsequent DM.
        //
        // Use the distinct `relay_reply_token_consumed` error code so `ToRelayFailure` maps
        // it to `PermanentFailure` (vs. transient). Without this, `ConversationGAgent
        // .HandleInboundTurnTransientFailureAsync` would queue an `InboundTurnRetryScheduled
        // Event` and re-run the same inbound turn with the same already-consumed token —
        // shifting the 401 cascade from in-turn replay (fixed) to grain-level replay (still
        // broken). The token is single-use, so we get exactly one attempt per inbound; if
        // that fails, the only correct recovery is to NOT replay it.
        _logger.LogWarning(
            "Interactive relay reply rejected; reply token consumed, not retrying. messageId={MessageId}, detail={Detail}",
            relayDelivery.ReplyMessageId,
            dispatch.Detail);
        return ToRelayFailure(EmitResult.Failed(
            "relay_reply_token_consumed",
            string.IsNullOrWhiteSpace(dispatch.Detail)
                ? "Interactive relay reply rejected; reply token consumed."
                : dispatch.Detail));
    }

    private async Task<ConversationTurnResult> SendRelayTextFallbackAsync(
        string? fallbackText,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        OutboundDeliveryContext relayDelivery,
        string relayToken,
        CancellationToken ct)
    {
        var outbound = new MessageContent { Text = NormalizeReplyText(fallbackText) };
        var emit = await _relayOutboundPort.SendAsync(
            ResolveRelayPlatform(inbound, conversation),
            conversation?.Clone() ?? new ConversationReference(),
            outbound,
            relayDelivery,
            relayToken,
            ct);
        return emit.Success
            ? BuildRelaySentResult(
                emit.SentActivityId,
                sentActivitySeed,
                outbound,
                relayDelivery)
            : ToRelayFailure(emit);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(string? registrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        return await _registrationQueryPort.GetAsync(registrationId, ct);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var nyxAgentApiKeyId = NormalizeOptional(activity.TransportExtras?.NyxAgentApiKeyId);
        if (!string.IsNullOrWhiteSpace(nyxAgentApiKeyId) &&
            _registrationQueryByNyxIdentityPort is not null)
        {
            var byNyxIdentity = await _registrationQueryByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync(
                nyxAgentApiKeyId,
                ct);
            if (byNyxIdentity is not null)
                return byNyxIdentity;

            if (IsNyxRelayActivity(activity, nyxAgentApiKeyId))
            {
                var byBoundedScan = await ResolveRegistrationByNyxIdentityScanAsync(nyxAgentApiKeyId, ct);
                if (byBoundedScan is not null)
                    return byBoundedScan;
            }
        }

        return await ResolveRegistrationAsync(activity.Bot?.Value, ct);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationByNyxIdentityScanAsync(
        string nyxAgentApiKeyId,
        CancellationToken ct)
    {
        var registrations = await _registrationQueryPort.QueryAllAsync(ct);
        return registrations.FirstOrDefault(entry =>
            string.Equals(NormalizeOptional(entry.NyxAgentApiKeyId), nyxAgentApiKeyId, StringComparison.Ordinal));
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationForReplyAsync(
        LlmReplyReadyEvent reply,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reply.RegistrationId))
            return await ResolveRegistrationAsync(reply.RegistrationId, ct);

        if (reply.Activity is not null)
            return await ResolveRegistrationAsync(reply.Activity, ct);

        return null;
    }

    private async Task<ConversationTurnResult?> TryHandleWorkflowResumeAsync(InboundMessage inbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        var routed = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var resumeCommand);
        if (!routed)
            routed = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out resumeCommand);

        if (!routed ||
            resumeCommand is null)
        {
            return null;
        }

        var resumeService = _services.GetService<
            ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        if (resumeService is null)
        {
            _logger.LogError(
                "Workflow resume service unavailable for registration callback: conversation={ConversationId}",
                inbound.ConversationId);
            return ConversationTurnResult.TransientFailure(
                "workflow_resume_service_unavailable",
                "Workflow resume service unavailable.");
        }

        var dispatch = await resumeService.DispatchAsync(resumeCommand, ct);
        if (!dispatch.Succeeded || dispatch.Receipt is null)
        {
            var error = dispatch.Error;
            if (error is null)
            {
                return ConversationTurnResult.TransientFailure(
                    "workflow_resume_dispatch_failed",
                    "Workflow control dispatch failed.");
            }

            return error.Code switch
            {
                WorkflowRunControlStartErrorCode.InvalidActorId =>
                    ConversationTurnResult.PermanentFailure("invalid_actor_id", "actorId is required."),
                WorkflowRunControlStartErrorCode.InvalidRunId =>
                    ConversationTurnResult.PermanentFailure("invalid_run_id", "runId is required."),
                WorkflowRunControlStartErrorCode.InvalidStepId =>
                    ConversationTurnResult.PermanentFailure("invalid_step_id", "stepId is required."),
                WorkflowRunControlStartErrorCode.ActorNotFound =>
                    ConversationTurnResult.PermanentFailure("actor_not_found", $"Actor '{error.ActorId}' not found."),
                WorkflowRunControlStartErrorCode.ActorNotWorkflowRun =>
                    ConversationTurnResult.PermanentFailure(
                        "actor_not_workflow_run",
                        $"Actor '{error.ActorId}' is not a workflow run actor."),
                WorkflowRunControlStartErrorCode.RunBindingMissing =>
                    ConversationTurnResult.PermanentFailure(
                        "run_binding_missing",
                        $"Actor '{error.ActorId}' does not have a bound run id."),
                WorkflowRunControlStartErrorCode.RunBindingMismatch =>
                    ConversationTurnResult.PermanentFailure(
                        "run_binding_mismatch",
                        $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
                _ => ConversationTurnResult.TransientFailure(
                    "workflow_resume_dispatch_failed",
                    "Workflow control dispatch failed."),
            };
        }

        return ConversationTurnResult.Sent(
            sentActivityId: $"workflow-resume:{dispatch.Receipt.CommandId}",
            outbound: new MessageContent(),
            authPrincipal: "bot");
    }

    private static IReadOnlyDictionary<string, string> BuildReplyMetadata(
        ChannelInboundEvent inboundEvent,
        ChatActivity? activity = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope_id"] = inboundEvent.RegistrationScopeId,
            [ChannelMetadataKeys.Platform] = inboundEvent.Platform,
            [ChannelMetadataKeys.SenderId] = inboundEvent.SenderId,
            [ChannelMetadataKeys.SenderName] = inboundEvent.SenderName,
            [ChannelMetadataKeys.ConversationId] = inboundEvent.ConversationId,
            [ChannelMetadataKeys.MessageId] = inboundEvent.MessageId,
            [ChannelMetadataKeys.ChatType] = inboundEvent.ChatType,
        };

        var platformMessageId = NormalizeOptional(activity?.TransportExtras?.NyxPlatformMessageId);
        if (!string.IsNullOrWhiteSpace(platformMessageId))
            metadata[ChannelMetadataKeys.PlatformMessageId] = platformMessageId;

        // Lark cross-app outbound delivery: agent-builder consumers prefer the tenant-stable
        // union_id / chat_id captured at ingress over the relay-app-scoped open_id, so a
        // mismatch between the relay-side Lark app and the customer's outbound Lark app does
        // not surface as `code:99992361 open_id cross app` rejections at send time.
        var larkUnionId = NormalizeOptional(activity?.TransportExtras?.NyxLarkUnionId);
        if (!string.IsNullOrWhiteSpace(larkUnionId))
            metadata[ChannelMetadataKeys.LarkUnionId] = larkUnionId;

        var larkChatId = NormalizeOptional(activity?.TransportExtras?.NyxLarkChatId);
        if (!string.IsNullOrWhiteSpace(larkChatId))
            metadata[ChannelMetadataKeys.LarkChatId] = larkChatId;

        return metadata;
    }

    private static IReadOnlyDictionary<string, string> BuildAgentBuilderMetadata(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        string? userAccessToken)
    {
        var metadata = new Dictionary<string, string>(BuildReplyMetadata(inboundEvent, activity), StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ChatType] = ResolveConversationChatType(activity.Conversation),
        };
        if (!string.IsNullOrWhiteSpace(userAccessToken))
        {
            metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userAccessToken.Trim();
            metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = userAccessToken.Trim();
        }
        return metadata;
    }

    internal static InboundMessage ToInboundMessage(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (activity.Type == ActivityType.CardAction && activity.Content?.CardAction is { } cardAction)
        {
            if (cardAction.Arguments.TryGetValue("agent_builder_action", out var builderAction) &&
                !string.IsNullOrWhiteSpace(builderAction))
            {
                extra["agent_builder_action"] = builderAction;
            }
            else if (!string.IsNullOrWhiteSpace(cardAction.ActionId))
            {
                extra["agent_builder_action"] = cardAction.ActionId;
            }

            foreach (var pair in cardAction.Arguments)
                extra[pair.Key] = pair.Value;
            foreach (var pair in cardAction.FormFields)
                extra[pair.Key] = pair.Value;
            if (!string.IsNullOrWhiteSpace(cardAction.SourceMessageId))
                extra["event_id"] = cardAction.SourceMessageId;
        }

        return new InboundMessage
        {
            Platform = activity.ChannelId?.Value ?? string.Empty,
            ConversationId = ResolveRoutingConversationId(activity.Conversation),
            SenderId = activity.From?.CanonicalId ?? string.Empty,
            SenderName = activity.From?.DisplayName ?? string.Empty,
            Text = activity.Content?.Text ?? string.Empty,
            MessageId = activity.Id,
            ChatType = ResolveChatType(activity.Conversation, activity.Type),
            OutboundDelivery = activity.OutboundDelivery?.Clone(),
            TransportExtras = activity.TransportExtras?.Clone(),
            Extra = extra,
        };
    }

    private static ChannelInboundEvent ToInboundEvent(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        InboundMessage inbound,
        string? userAccessToken)
    {
        var inboundEvent = new ChannelInboundEvent
        {
            Text = inbound.Text,
            SenderId = inbound.SenderId,
            SenderName = inbound.SenderName,
            ConversationId = inbound.ConversationId,
            MessageId = inbound.MessageId ?? string.Empty,
            ChatType = inbound.ChatType ?? string.Empty,
            Platform = inbound.Platform,
            RegistrationId = registration.Id,
            RegistrationToken = userAccessToken ?? string.Empty,
            RegistrationScopeId = registration.ScopeId,
            NyxProviderSlug = registration.NyxProviderSlug,
        };

        foreach (var pair in inbound.Extra)
            inboundEvent.Extra[pair.Key] = pair.Value;

        return inboundEvent;
    }

    private static NeedsLlmReplyEvent BuildLlmReplyRequest(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        ChannelInboundEvent inboundEvent,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = activity.Id,
            TargetActorId = ConversationGAgent.BuildActorId(activity.Conversation!.CanonicalKey),
            RegistrationId = registration.Id,
            Activity = activity.Clone(),
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Carry the relay reply credential through the inbox as transient inbox-only
        // fields. ConversationGAgent strips these before persisting NeedsLlmReplyEvent;
        // ChannelLlmReplyInboxRuntime echoes them into the LlmReplyReadyEvent so the
        // outbound reply does not depend on the actor's in-memory token dict surviving
        // deactivation.
        if (runtimeContext.NyxRelayReplyToken is { } token &&
            token.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            request.ReplyToken = token.ReplyToken;
            request.ReplyTokenExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
        }

        foreach (var pair in BuildReplyMetadata(inboundEvent, activity))
            request.Metadata[pair.Key] = pair.Value;

        return request;
    }

    private static string ResolveRoutingConversationId(ConversationReference? conversation)
    {
        if (conversation is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(conversation.Partition))
            return conversation.Partition;

        if (conversation.Scope == ConversationScope.DirectMessage)
            return string.Empty;

        return ResolveLastCanonicalSegment(conversation.CanonicalKey);
    }

    private static string ResolveLastCanonicalSegment(string? canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey))
            return string.Empty;

        var parts = canonicalKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static string ResolveChatType(ConversationReference? conversation, ActivityType activityType = ActivityType.Message)
    {
        if (activityType == ActivityType.CardAction)
            return "card_action";

        return ResolveConversationChatType(conversation);
    }

    private static string ResolveConversationChatType(ConversationReference? conversation)
    {
        return conversation?.Scope switch
        {
            ConversationScope.DirectMessage => "p2p",
            ConversationScope.Group => "group",
            ConversationScope.Channel => "channel",
            ConversationScope.Thread => "thread",
            _ => "conversation",
        };
    }

    private static bool HasRelayDelivery(InboundMessage inbound) =>
        inbound.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private static string? ResolveRelayReplyToken(
        OutboundDeliveryContext relayDelivery,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var tokenContext = runtimeContext.NyxRelayReplyToken;
        if (tokenContext is null || tokenContext.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return null;

        if (!string.Equals(
                NormalizeOptional(relayDelivery.CorrelationId),
                NormalizeOptional(tokenContext.CorrelationId),
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(
                NormalizeOptional(relayDelivery.ReplyMessageId),
                NormalizeOptional(tokenContext.ReplyMessageId),
                StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeOptional(tokenContext.ReplyToken);
    }

    private static string? ResolveUserAccessToken(ChatActivity activity) =>
        NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ResolveRelayPlatform(InboundMessage inbound, ConversationReference? conversation)
    {
        var platform = !string.IsNullOrWhiteSpace(inbound.TransportExtras?.NyxPlatform)
            ? inbound.TransportExtras.NyxPlatform
            : !string.IsNullOrWhiteSpace(inbound.Platform)
                ? inbound.Platform
                : conversation?.Channel?.Value ?? string.Empty;

        return string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase)
            ? "lark"
            : platform;
    }

    private static bool IsNyxRelayActivity(ChatActivity activity, string nyxAgentApiKeyId) =>
        activity.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        } &&
        string.Equals(NormalizeOptional(activity.Bot?.Value), nyxAgentApiKeyId, StringComparison.Ordinal);

    // Lark reaction emoji_type for "hands typing on keyboard" — added immediately on inbound
    // so the user sees the bot is working before the LLM reply lands. Swapped to DoneReactionEmojiType
    // after the reply succeeds so the same message ends up with a single completion reaction.
    private const string TypingReactionEmojiType = "Typing";
    private const string DoneReactionEmojiType = "DONE";

    private async Task TrySendImmediateLarkReactionAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (!ShouldSendImmediateLarkReaction(activity, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                accessToken!,
                providerSlug!,
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions",
                "POST",
                $$$"""{"reaction_type":{"emoji_type":"{{{TypingReactionEmojiType}}}"}}""",
                null,
                ct);

            if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            {
                if (larkCode == LarkBotErrorCodes.NoPermissionToReact)
                {
                    // The bot is missing reaction permission on Lark — a
                    // tenant-level config issue that recurs on every inbound
                    // message until ops fixes the app scope. Log at Debug so
                    // it stays discoverable when the channel is opted into
                    // verbose logging without spamming Warnings on every turn.
                    _logger.LogDebug(
                        "Immediate Lark typing reaction skipped (missing reaction scope): provider={ProviderSlug}, message={MessageId}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        detail);
                }
                else
                {
                    // Anything else — a Nyx envelope error, an unexpected Lark
                    // business code (rate limit, archived message, bot kicked,
                    // etc.) — is a real signal that should stay at Warning so
                    // we notice when Lark behavior changes.
                    _logger.LogWarning(
                        "Immediate Lark typing reaction failed: provider={ProviderSlug}, message={MessageId}, larkCode={LarkCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        larkCode,
                        detail);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Immediate Lark typing reaction threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    // Direct-reply paths (TryHandleAgentBuilderAsync) can complete a slash-command reply faster
    // than the typing POST takes to land in Lark, leaving the swap GET to find no Typing reaction
    // to delete and the orphaned typing reaction to materialize after DONE was already added —
    // both reactions on the same message. Awaiting (with a short cap) the typing task before the
    // GET closes that race. The cap protects against a hung POST stalling the swap forever; if it
    // expires the swap still proceeds — Lark will at worst end up with both reactions, same as
    // before this guard. The deferred-LLM and streaming paths skip this guard because their reply
    // latency dwarfs the typing POST and so cannot race.
    private async Task AwaitTypingReactionThenSwapAsync(
        Task typingReactionTask,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        try
        {
            await typingReactionTask.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Lark typing reaction task did not complete within timeout before swap; proceeding anyway");
        }
        catch (Exception)
        {
            // The typing task already logged its own exception — proceed with the swap so the
            // user-visible message still ends up with a DONE reaction whenever possible.
        }

        await TrySwapTypingReactionToDoneAsync(inbound, registration, ct);
    }

    // After a successful reply, replace the bot's "Typing" reaction with a "DONE" reaction so the
    // same message ends with a single completion marker. Uses list-based discovery (filter by
    // emoji_type=Typing AND operator_type=app) instead of caching the immediate reaction's
    // reaction_id locally — the runner is a singleton and cross-turn state on it would violate the
    // "中间层进程内缓存作为事实源" rule. Filtering on operator_type=app avoids deleting any user
    // who happened to add the same Typing reaction.
    private async Task TrySwapTypingReactionToDoneAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        CancellationToken ct)
    {
        if (registration is null)
            return;

        if (!ShouldSwapTypingReaction(inbound, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var listResponse = await _nyxClient.ProxyRequestAsync(
                accessToken!,
                providerSlug!,
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions?reaction_type={TypingReactionEmojiType}&page_size=50",
                "GET",
                body: null,
                extraHeaders: null,
                ct);

            if (LarkProxyResponse.TryGetError(listResponse, out var listCode, out var listDetail))
            {
                _logger.LogDebug(
                    "Lark typing reaction list failed; skipping swap: provider={ProviderSlug}, message={MessageId}, larkCode={LarkCode}, detail={Detail}",
                    providerSlug,
                    platformMessageId,
                    listCode,
                    listDetail);
                return;
            }

            foreach (var reactionId in ParseAppReactionIds(listResponse))
            {
                try
                {
                    var deleteResponse = await _nyxClient.ProxyRequestAsync(
                        accessToken!,
                        providerSlug!,
                        $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions/{Uri.EscapeDataString(reactionId)}",
                        "DELETE",
                        body: null,
                        extraHeaders: null,
                        ct);

                    if (LarkProxyResponse.TryGetError(deleteResponse, out var deleteCode, out var deleteDetail))
                    {
                        _logger.LogDebug(
                            "Lark typing reaction delete failed: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}, larkCode={LarkCode}, detail={Detail}",
                            providerSlug,
                            platformMessageId,
                            reactionId,
                            deleteCode,
                            deleteDetail);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Lark typing reaction delete threw: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}",
                        providerSlug,
                        platformMessageId,
                        reactionId);
                }
            }

            var addResponse = await _nyxClient.ProxyRequestAsync(
                accessToken!,
                providerSlug!,
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions",
                "POST",
                $$$"""{"reaction_type":{"emoji_type":"{{{DoneReactionEmojiType}}}"}}""",
                null,
                ct);

            if (LarkProxyResponse.TryGetError(addResponse, out var addCode, out var addDetail))
            {
                if (addCode == LarkBotErrorCodes.NoPermissionToReact)
                {
                    _logger.LogDebug(
                        "Lark done reaction skipped (missing reaction scope): provider={ProviderSlug}, message={MessageId}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        addDetail);
                }
                else
                {
                    _logger.LogWarning(
                        "Lark done reaction failed: provider={ProviderSlug}, message={MessageId}, larkCode={LarkCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        addCode,
                        addDetail);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark typing→done reaction swap threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    private static IEnumerable<string> ParseAppReactionIds(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            yield break;

        List<string> ids;
        try
        {
            ids = ExtractAppReactionIds(response);
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var id in ids)
            yield return id;
    }

    private static List<string> ExtractAppReactionIds(string response)
    {
        var ids = new List<string>();
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return ids;

        if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
            return ids;

        if (!dataProp.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var item in itemsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // Only delete reactions added by the bot itself (operator_type=app); leave any
            // user-added Typing reactions alone so the swap doesn't accidentally erase them.
            if (!item.TryGetProperty("operator", out var operatorProp) ||
                operatorProp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!operatorProp.TryGetProperty("operator_type", out var operatorTypeProp) ||
                operatorTypeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(operatorTypeProp.GetString(), "app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("reaction_id", out var reactionIdProp) ||
                reactionIdProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var reactionId = reactionIdProp.GetString();
            if (!string.IsNullOrWhiteSpace(reactionId))
                ids.Add(reactionId);
        }

        return ids;
    }

    private static bool ShouldSwapTypingReaction(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        var platform = NormalizeOptional(inbound.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(registration.Platform) ??
                       NormalizeOptional(inbound.Platform);
        if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        accessToken = NormalizeOptional(inbound.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(inbound.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSendImmediateLarkReaction(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        if (activity.Type != ActivityType.Message)
            return false;

        var platform = NormalizeOptional(activity.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(registration.Platform) ??
                       NormalizeOptional(activity.ChannelId?.Value);
        if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        accessToken = NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(activity.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }

    private static ConversationTurnResult ToRelayFailure(EmitResult emit)
    {
        var errorCode = string.IsNullOrWhiteSpace(emit.ErrorCode) ? "relay_reply_rejected" : emit.ErrorCode;
        var errorMessage = string.IsNullOrWhiteSpace(emit.ErrorMessage)
            ? "Nyx relay reply rejected."
            : emit.ErrorMessage;

        return errorCode switch
        {
            // The reply token has already been consumed (single-use). Re-running the inbound
            // turn at grain level (`ConversationGAgent.HandleInboundTurnTransientFailureAsync`)
            // would replay the same token and get `401 Reply token already used` forever, so
            // route to PermanentFailure to short-circuit the retry queue. The user-facing
            // recovery is to send a fresh inbound message which carries a fresh token.
            "relay_reply_token_consumed" or
            "reply_token_missing_or_expired" or "missing_reply_message_id" or "empty_reply" =>
                ConversationTurnResult.PermanentFailure(errorCode, errorMessage),
            _ when emit.RetryAfterTimeSpan is { } retryAfter =>
                ConversationTurnResult.TransientFailure(errorCode, errorMessage, retryAfter),
            _ => ConversationTurnResult.TransientFailure(errorCode, errorMessage),
        };
    }

    private static ChannelId ResolveRelayChannel(InboundMessage inbound, ConversationReference? conversation) =>
        ChannelId.From(ResolveRelayPlatform(inbound, conversation));

    private static bool HasContent(MessageContent content) =>
        !string.IsNullOrWhiteSpace(content.Text) ||
        HasInteractiveContent(content) ||
        content.Attachments.Count > 0;

    private static bool HasInteractiveContent(MessageContent content) =>
        content.Actions.Count > 0 || content.Cards.Count > 0;

    private static string NormalizeReplyText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(no content)" : text.Trim();

    private static ConversationTurnResult BuildRelaySentResult(
        string? sentActivityId,
        string sentActivitySeed,
        MessageContent outbound,
        OutboundDeliveryContext relayDelivery) =>
        ConversationTurnResult.Sent(
            sentActivityId: string.IsNullOrWhiteSpace(sentActivityId)
                ? $"direct-reply:{sentActivitySeed}"
                : sentActivityId,
            outbound: outbound.Clone(),
            authPrincipal: "bot",
            outboundDelivery: new OutboundDeliveryContext
            {
                ReplyMessageId = relayDelivery.ReplyMessageId,
                CorrelationId = relayDelivery.CorrelationId,
            });
}
