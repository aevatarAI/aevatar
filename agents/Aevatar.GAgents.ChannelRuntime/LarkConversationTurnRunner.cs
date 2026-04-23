using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class LarkConversationTurnRunner : IConversationTurnRunner
{
    private readonly IServiceProvider _services;
    private readonly IChannelBotRegistrationQueryPort _registrationQueryPort;
    private readonly IEnumerable<IPlatformAdapter> _platformAdapters;
    private readonly NyxIdApiClient _nyxClient;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly ILogger<LarkConversationTurnRunner> _logger;

    public LarkConversationTurnRunner(
        IServiceProvider services,
        IChannelBotRegistrationQueryPort registrationQueryPort,
        IEnumerable<IPlatformAdapter> platformAdapters,
        NyxIdApiClient nyxClient,
        IConversationReplyGenerator replyGenerator,
        ILogger<LarkConversationTurnRunner> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrationQueryPort = registrationQueryPort ?? throw new ArgumentNullException(nameof(registrationQueryPort));
        _platformAdapters = platformAdapters ?? throw new ArgumentNullException(nameof(platformAdapters));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var registration = await ResolveRegistrationAsync(activity.Bot?.Value, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        var inbound = ToInboundMessage(activity);
        if (await TryHandleWorkflowResumeAsync(inbound, ct) is { } workflowResumeResult)
            return workflowResumeResult;

        var inboundEvent = ToInboundEvent(activity, registration, inbound);

        if (await TryHandleAgentBuilderAsync(activity, inboundEvent, registration, ct) is { } agentBuilderResult)
            return agentBuilderResult;

        var reply = await _replyGenerator.GenerateReplyAsync(activity, BuildReplyMetadata(inboundEvent), ct);
        if (string.IsNullOrWhiteSpace(reply))
            return ConversationTurnResult.TransientFailure("empty_reply", "Nyx chat returned an empty response.");

        return await SendReplyAsync(reply, activity, inbound, registration, ct);
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

        return await SendReplyAsync(command.Payload?.Text ?? string.Empty, command.CommandId, inbound, registration, ct);
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
            (!AgentBuilderCardFlow.TryResolve(inboundEvent, out decision) || decision is null))
        {
            return null;
        }

        if (decision is null)
            return null;

        var replyPayload = decision.ReplyPayload;
        if (decision.RequiresToolExecution)
        {
            var previousMetadata = AgentToolRequestContext.CurrentMetadata;
            try
            {
                AgentToolRequestContext.CurrentMetadata = BuildAgentBuilderMetadata(activity, inboundEvent);
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
                authPrincipal: "bot")
            : result;
    }

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct) =>
        await SendReplyAsync(replyText, activity.Id, inbound, registration, ct);

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        string sentActivitySeed,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
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
                "Lark conversation reply rejected: registration={RegistrationId}, detail={Detail}, kind={Kind}",
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
            authPrincipal: "bot");
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(string? registrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        return await _registrationQueryPort.GetAsync(registrationId, ct);
    }

    private async Task<ConversationTurnResult?> TryHandleWorkflowResumeAsync(InboundMessage inbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        var routed = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out var resumeCommand);
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
            [LLMRequestMetadataKeys.NyxIdAccessToken] = inboundEvent.RegistrationToken,
            [LLMRequestMetadataKeys.NyxIdOrgToken] = inboundEvent.RegistrationToken,
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
        ChannelInboundEvent inboundEvent)
    {
        var metadata = new Dictionary<string, string>(BuildReplyMetadata(inboundEvent), StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ChatType] = ResolveConversationChatType(activity.Conversation),
        };
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
            Extra = extra,
        };
    }

    private static ChannelInboundEvent ToInboundEvent(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        InboundMessage inbound)
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
            RegistrationToken = string.Empty,
            RegistrationScopeId = registration.ScopeId,
            NyxProviderSlug = registration.NyxProviderSlug,
        };

        foreach (var pair in inbound.Extra)
            inboundEvent.Extra[pair.Key] = pair.Value;

        return inboundEvent;
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
        return conversation?.Scope == ConversationScope.Group
            ? "group"
            : "p2p";
    }
}
