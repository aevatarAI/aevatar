using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class ChannelConversationTurnRunner : IConversationTurnRunner
{
    private readonly IServiceProvider _services;
    private readonly IChannelBotRegistrationQueryPort _registrationQueryPort;
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort? _registrationQueryByNyxIdentityPort;
    private readonly IEnumerable<IPlatformAdapter> _platformAdapters;
    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdRelayOutboundPort _relayOutboundPort;
    private readonly ILogger<ChannelConversationTurnRunner> _logger;

    public ChannelConversationTurnRunner(
        IServiceProvider services,
        IChannelBotRegistrationQueryPort registrationQueryPort,
        IChannelBotRegistrationQueryByNyxIdentityPort? registrationQueryByNyxIdentityPort,
        IEnumerable<IPlatformAdapter> platformAdapters,
        NyxIdApiClient nyxClient,
        NyxIdRelayOutboundPort relayOutboundPort,
        ILogger<ChannelConversationTurnRunner> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrationQueryPort = registrationQueryPort ?? throw new ArgumentNullException(nameof(registrationQueryPort));
        _registrationQueryByNyxIdentityPort = registrationQueryByNyxIdentityPort;
        _platformAdapters = platformAdapters ?? throw new ArgumentNullException(nameof(platformAdapters));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _relayOutboundPort = relayOutboundPort ?? throw new ArgumentNullException(nameof(relayOutboundPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var registration = await ResolveRegistrationAsync(activity, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        var inbound = ToInboundMessage(activity);
        if (await TryHandleWorkflowResumeAsync(inbound, ct) is { } workflowResumeResult)
            return workflowResumeResult;

        var inboundEvent = ToInboundEvent(activity, registration, inbound, ResolveUserAccessToken(activity));

        if (await TryHandleAgentBuilderAsync(activity, inboundEvent, registration, ct) is { } agentBuilderResult)
            return agentBuilderResult;

        if (string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
        {
            return ConversationTurnResult.PermanentFailure(
                "conversation_not_found",
                "Conversation routing target is missing.");
        }

        return ConversationTurnResult.LlmReplyRequested(BuildLlmReplyRequest(activity, registration, inboundEvent));
    }

    public async Task<ConversationTurnResult> RunLlmReplyAsync(LlmReplyReadyEvent reply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (reply.Activity is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "activity_required",
                "Deferred LLM reply is missing the source activity.");
        }

        var replyText = reply.Outbound?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return ConversationTurnResult.TransientFailure(
                string.IsNullOrWhiteSpace(reply.ErrorCode) ? "empty_reply" : reply.ErrorCode,
                string.IsNullOrWhiteSpace(reply.ErrorSummary)
                    ? "Deferred LLM reply is empty."
                    : reply.ErrorSummary);
        }

        var inbound = ToInboundMessage(reply.Activity);
        ChannelBotRegistrationEntry? registration = null;
        if (!HasRelayDelivery(inbound))
        {
            registration = await ResolveRegistrationForReplyAsync(reply, ct);
            if (registration is null)
            {
                return ConversationTurnResult.PermanentFailure(
                    "registration_not_found",
                    "Channel registration not found.");
            }
        }

        var sentSeed = string.IsNullOrWhiteSpace(reply.CorrelationId)
            ? reply.Activity.Id
            : reply.CorrelationId;
        return await SendReplyAsync(
            replyText,
            sentSeed,
            reply.Activity.Conversation,
            inbound,
            registration,
            ct);
    }

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
            command.Payload?.Text ?? string.Empty,
            command.CommandId,
            command.Conversation,
            inbound,
            registration,
            ct);
    }

    private async Task<ConversationTurnResult?> TryHandleAgentBuilderAsync(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        ChannelBotRegistrationEntry registration,
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

        var replyPayload = decision.ReplyPayload;
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
                replyPayload = relayDecisionMatched
                    ? NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResult)
                    : AgentBuilderCardFlow.FormatToolResult(decision, toolResult);
            }
            finally
            {
                AgentToolRequestContext.CurrentMetadata = previousMetadata;
            }
        }

        var result = await SendReplyAsync(replyPayload, activity, ToInboundMessage(activity), registration, ct);
        return result.Success
            ? ConversationTurnResult.Sent(
                sentActivityId: $"direct-reply:{activity.Id}",
                outbound: new MessageContent { Text = replyPayload },
                authPrincipal: "bot",
                outboundDelivery: result.OutboundDelivery?.Clone())
            : result;
    }

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct) =>
        await SendReplyAsync(replyText, activity.Id, activity.Conversation, inbound, registration, ct);

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        CancellationToken ct)
    {
        if (HasRelayDelivery(inbound))
        {
            var relayDelivery = inbound.OutboundDelivery!.Clone();
            var emit = await _relayOutboundPort.SendAsync(
                ResolveRelayPlatform(inbound, conversation),
                conversation?.Clone() ?? new ConversationReference(),
                new MessageContent { Text = replyText },
                relayDelivery,
                ct);
            if (!emit.Success)
                return ToRelayFailure(emit);

            return ConversationTurnResult.Sent(
                sentActivityId: string.IsNullOrWhiteSpace(emit.SentActivityId)
                    ? $"direct-reply:{sentActivitySeed}"
                    : emit.SentActivityId,
                outbound: new MessageContent { Text = replyText },
                authPrincipal: "bot",
                outboundDelivery: new OutboundDeliveryContext
                {
                    ReplyMessageId = relayDelivery.ReplyMessageId,
                    ReplyAccessToken = relayDelivery.ReplyAccessToken,
                });
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

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(string? registrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        return await _registrationQueryPort.GetAsync(registrationId, ct);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var nyxAgentApiKeyId = activity.TransportExtras?.NyxAgentApiKeyId;
        if (!string.IsNullOrWhiteSpace(nyxAgentApiKeyId) &&
            _registrationQueryByNyxIdentityPort is not null)
        {
            var byNyxIdentity = await _registrationQueryByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync(
                nyxAgentApiKeyId,
                ct);
            if (byNyxIdentity is not null)
                return byNyxIdentity;
        }

        return await ResolveRegistrationAsync(activity.Bot?.Value, ct);
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

    private static IReadOnlyDictionary<string, string> BuildReplyMetadata(ChannelInboundEvent inboundEvent)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope_id"] = inboundEvent.RegistrationScopeId,
            [ChannelMetadataKeys.Platform] = inboundEvent.Platform,
            [ChannelMetadataKeys.SenderId] = inboundEvent.SenderId,
            [ChannelMetadataKeys.SenderName] = inboundEvent.SenderName,
            [ChannelMetadataKeys.ConversationId] = inboundEvent.ConversationId,
            [ChannelMetadataKeys.MessageId] = inboundEvent.MessageId,
            [ChannelMetadataKeys.ChatType] = inboundEvent.ChatType,
        };
    }

    private static IReadOnlyDictionary<string, string> BuildAgentBuilderMetadata(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        string? userAccessToken)
    {
        var metadata = new Dictionary<string, string>(BuildReplyMetadata(inboundEvent), StringComparer.Ordinal)
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
        ChannelInboundEvent inboundEvent)
    {
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = activity.Id,
            TargetActorId = ConversationGAgent.BuildActorId(activity.Conversation!.CanonicalKey),
            RegistrationId = registration.Id,
            Activity = activity.Clone(),
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        foreach (var pair in BuildReplyMetadata(inboundEvent))
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
        };

    private static string? ResolveUserAccessToken(ChatActivity activity) =>
        NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ResolveRelayPlatform(InboundMessage inbound, ConversationReference? conversation)
    {
        if (!string.IsNullOrWhiteSpace(inbound.TransportExtras?.NyxPlatform))
            return inbound.TransportExtras.NyxPlatform;
        if (!string.IsNullOrWhiteSpace(inbound.Platform))
            return inbound.Platform;
        return conversation?.Channel?.Value ?? string.Empty;
    }

    private static ConversationTurnResult ToRelayFailure(EmitResult emit)
    {
        var errorCode = string.IsNullOrWhiteSpace(emit.ErrorCode) ? "relay_reply_rejected" : emit.ErrorCode;
        var errorMessage = string.IsNullOrWhiteSpace(emit.ErrorMessage)
            ? "Nyx relay reply rejected."
            : emit.ErrorMessage;

        return errorCode switch
        {
            "missing_reply_access_token" or "missing_reply_message_id" or "empty_reply" =>
                ConversationTurnResult.PermanentFailure(errorCode, errorMessage),
            _ when emit.RetryAfterTimeSpan is { } retryAfter =>
                ConversationTurnResult.TransientFailure(errorCode, errorMessage, retryAfter),
            _ => ConversationTurnResult.TransientFailure(errorCode, errorMessage),
        };
    }
}
