using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-conversation single-activation actor keyed by <see cref="ConversationReference.CanonicalKey"/>.
/// Owns conversation-scoped dedup state and is the authoritative boundary for atomic
/// "admit activity → invoke bot turn → commit outbound + dedup entry" semantics per RFC §5.2b.
/// </summary>
/// <remarks>
/// <para>
/// Dedup is strongly serialized by the actor turn. The pipeline may fast-path-check
/// <see cref="ConversationGAgentState.ProcessedMessageIds"/> upstream, but the authoritative
/// check lives inside <see cref="HandleInboundActivityAsync"/>. Double delivery of the same
/// <see cref="ChatActivity.Id"/> is collapsed to one emitted <see cref="ConversationTurnCompletedEvent"/>.
/// </para>
/// <para>
/// Downstream projections subscribe through the standard
/// <see cref="Aevatar.Foundation.Abstractions.CommittedStateEventPublished"/> pipeline wired up by
/// <see cref="GAgentBase{TState}.PersistDomainEventAsync{TEvent}"/>. No inline projection writes.
/// </para>
/// </remarks>
// Refactor (iter20/cluster-004):
//   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
//   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
// Refactor (iter107/cluster-1-channel-business-io-process-queue):
//   Old pattern: process-local Channel/Task workers owned business IO via singleton executor.
//   New principle: actor-owned operation state (operation_id/lease_epoch/step) + typed self-continuation events; provider IO is inline async, no in-process worker queue.
[GAgent("channel.runtime.conversation")]
public sealed partial class ConversationGAgent :
    GAgentBase<ConversationGAgentState>,
    IEventSourcingVersionDriftRecoverableActor,
    IReplyOperationActorContext
{
    // Refactor (iter17/cluster-038):
    //   Old pattern: Nyx relay replay/idempotency 和 reply 累积在 process-local ConcurrentDictionary/lock(NyxRelayBridgeIdempotencyGuard / NyxIdRelayReplayGuard / NyxIdRelayReplyAccumulator)。
    //   New principle: ConversationGAgent persist callback_jti admission 为 typed event 优先于 business work;删除 process-local replay guards + dead accumulator。
    // Orleans Reminders (the durable scheduler backing ScheduleSelfDurableTimeoutAsync)
    // round dueTime up to the local reminder service tick (typically ~1 minute), so
    // sub-minute schedules are unreliable. The run dispatch happens inline via
    // IChannelLlmReplyRunDispatcher; the durable timer is reserved for retry/rehydration.
    private static readonly TimeSpan DeferredLlmDispatchRetryDelay = TimeSpan.FromSeconds(60);
    // Pending LLM reply requests older than this are considered stale on rehydration:
    // the user gave up, the relay reply_token (~30 min TTL) is likely already expired,
    // and the user access token (~15 min TTL) used for the LLM call is definitely gone.
    // Drop them rather than burn an LLM round and reply hours late.
    private static readonly TimeSpan PendingLlmReplyRequestMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StreamingFailureUpdateTimeout = TimeSpan.FromSeconds(10);

    // Mirror of DeferredLlmDispatchRetryDelay for the inbound-turn retry pipeline.
    // The same reminder-granularity floor applies: any requested retry shorter than this
    // would be silently rounded up by Orleans and appear lost.
    private static readonly TimeSpan DeferredInboundTurnRetryDelay = TimeSpan.FromSeconds(60);
    // Bounded retry count for transient inbound-turn failures. On exhaustion the actor
    // persists a terminal ConversationContinueFailedEvent (NotRetryable) so the pending
    // set does not grow unboundedly.
    public const int MaxInboundTurnRetryCount = 5;
    private const int RelayReplayClaimsCap = 10000;
    private const int PendingRelayAdmissionsCap = 1000;
    private const int RetainedHistoryMessagesCap = 100;
    private const int RecentAttachmentActivityCap = 5;
    private const int RecentDeliveriesCap = 100;
    private const int MaxNyxRelayInterimUpdateRetryCount = 2;
    private const string RelayReplyTokenSecretPurpose = "channel-relay-reply-token";
    private const string RelayUserAccessTokenSecretPurpose = "channel-relay-user-access-token";
    private static readonly TimeSpan RecentAttachmentActivityWindow = TimeSpan.FromMinutes(10);
    private const int RuntimeCredentialLocalOccRetryCount = 3;

    /// <summary>
    /// Sliding window cap on retained processed ids. Keeps state size bounded while still
    /// catching typical redelivery windows (seconds to minutes).
    /// </summary>
    public const int ProcessedIdsCap = 10000;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await SchedulePendingLlmReplyDispatchesAsync(ct);
        await DispatchPendingWorkflowDraftRunsAsync(ct);
        await SchedulePendingInboundTurnRetriesAsync(ct);
        await DispatchPendingRelayAdmissionTurnsAsync(ct);
    }

    /// <inheritdoc />
    protected override ConversationGAgentState TransitionState(ConversationGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationTurnCompletedEvent>(ApplyTurnCompleted)
            .On<NeedsLlmReplyEvent>(ApplyLlmReplyRequested)
            .On<NeedsWorkflowDraftRunEvent>(ApplyWorkflowDraftRunRequested)
            .On<ConversationContinueRejectedEvent>(ApplyContinueRejected)
            .On<ConversationContinueFailedEvent>(ApplyContinueFailed)
            .On<InboundTurnRetryScheduledEvent>(ApplyInboundTurnRetryScheduled)
            .On<NyxRelayCallbackAdmittedEvent>(ApplyNyxRelayCallbackAdmitted)
            .On<LlmReplyDeliveredEvent>(ApplyLastReplyDelivered)
            .On<LlmReplyDeliveryFailedEvent>(ApplyLastReplyDeliveryFailed)
            .On<DeliveryProducedEvent>(ApplyDeliveryProduced)
            .On<ConversationReplyLifecycleChangedEvent>(ApplyReplyLifecycleChanged)
            .On<ConversationReplyLifecycleClearedEvent>(ApplyReplyLifecycleCleared)
            .On<ConversationRetainedHistoryClearedEvent>(ApplyRetainedHistoryCleared)
            .OrCurrent();

    /// <summary>
    /// Authoritative inbound admission: dedup + run bot turn + commit atomically.
    /// </summary>
    [EventHandler]
    public Task HandleInboundActivityAsync(ChatActivity activity) =>
        HandleInboundActivityCoreAsync(activity, ConversationTurnRuntimeContext.Empty);

    [EventHandler]
    public async Task HandleNyxRelayInboundActivityAsync(NyxRelayInboundActivity relayActivity)
    {
        ArgumentNullException.ThrowIfNull(relayActivity);

        var activity = relayActivity.Activity?.Clone() ?? new ChatActivity();
        var relayApiKeyId = NormalizeOptional(relayActivity.RelayApiKeyId);
        var callbackJti = NormalizeOptional(relayActivity.CallbackJti);
        if (relayApiKeyId is not null && callbackJti is not null)
        {
            if (HasActiveRelayReplayClaim(relayApiKeyId, callbackJti, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                Logger.LogInformation(
                    "Duplicate Nyx relay callback {CallbackJti} for api key {RelayApiKeyId}; skipping turn",
                    callbackJti,
                    relayApiKeyId);
                return;
            }
        }

        var runtimeContext = BuildNyxRelayRuntimeContext(
            relayActivity.CorrelationId,
            activity,
            relayActivity.ReplyToken,
            relayActivity.ReplyTokenExpiresAtUnixMs,
            activity.TransportExtras?.NyxUserAccessToken);

        if (relayApiKeyId is not null && callbackJti is not null)
        {
            var nowMs = relayActivity.CallbackObservedAtUnixMs > 0
                ? relayActivity.CallbackObservedAtUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var admitted = new NyxRelayCallbackAdmittedEvent
            {
                ActivityId = activity.Id ?? string.Empty,
                RelayApiKeyId = relayApiKeyId,
                CallbackJti = callbackJti,
                Activity = CloneForDurableState(activity),
                AdmittedAtUnixMs = nowMs,
                ClaimExpiresAtUnixMs = relayActivity.CallbackReplayExpiresAtUnixMs > nowMs
                    ? relayActivity.CallbackReplayExpiresAtUnixMs
                    : nowMs + (long)TimeSpan.FromMinutes(5).TotalMilliseconds,
            };
            var admissionPersisted = await PersistRelayAdmissionWithLocalRetryAsync(
                admitted,
                relayApiKeyId,
                callbackJti,
                CancellationToken.None);
            if (!admissionPersisted)
                return;

            await SendToAsync(
                Id,
                new NyxRelayCallbackTurnRequestedEvent
                {
                    ActivityId = admitted.ActivityId,
                    RelayApiKeyId = relayApiKeyId,
                    CallbackJti = callbackJti,
                    RequestedAtUnixMs = nowMs,
                    ReplyToken = relayActivity.ReplyToken ?? string.Empty,
                    ReplyTokenExpiresAtUnixMs = relayActivity.ReplyTokenExpiresAtUnixMs,
                    NyxUserAccessToken = activity.TransportExtras?.NyxUserAccessToken ?? string.Empty,
                },
                CancellationToken.None);
            return;
        }

        await HandleInboundActivityCoreAsync(activity, runtimeContext);
    }

    // AllowSelfHandling is required: admission persists then self-sends NyxRelayCallbackTurnRequestedEvent
    // via SendToAsync(Id, ...). EventHandlerAttribute defaults AllowSelfHandling=false, which causes
    // StaticHandlerAdapter to drop the envelope when PublisherActorId == this.Id, so the handler never
    // runs and the bot goes silent with zero log signature (2026-05-21 prod Lark outage).
    // OnlySelfHandling is NOT set here: it gates by envelope TopologyAudience (must be Self), but
    // SendToAsync produces a Direct route whose audience reads back as Unspecified, so adding
    // OnlySelfHandling=true would re-filter the same envelope we are trying to admit. Pairs with
    // RoleGAgent.cs:73 (same SendToAsync + AllowSelfHandling-only pattern).
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayCallbackTurnRequestedAsync(NyxRelayCallbackTurnRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var admission = FindPendingRelayAdmission(evt.RelayApiKeyId, evt.CallbackJti, evt.ActivityId);
        if (admission is null)
        {
            Logger.LogDebug(
                "Ignoring Nyx relay callback turn without pending admission: activity={ActivityId} callbackJti={CallbackJti}",
                evt.ActivityId,
                evt.CallbackJti);
            return;
        }

        if (admission.Activity is null)
        {
            Logger.LogWarning(
                "Ignoring Nyx relay callback turn with missing admitted activity: activity={ActivityId} callbackJti={CallbackJti}",
                evt.ActivityId,
                evt.CallbackJti);
            return;
        }

        var activity = admission.Activity.Clone();
        RestoreRuntimeTransportCredentials(activity, NormalizeOptional(evt.NyxUserAccessToken));
        var runtimeContext = BuildNyxRelayRuntimeContext(
            NormalizeOptional(activity.OutboundDelivery?.CorrelationId) ??
            NormalizeOptional(admission.ActivityId),
            activity,
            evt.ReplyToken,
            evt.ReplyTokenExpiresAtUnixMs,
            evt.NyxUserAccessToken);
        await HandleInboundActivityCoreAsync(activity.Clone(), runtimeContext);
    }

    private async Task HandleInboundActivityCoreAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            Logger.LogWarning("Dropping ChatActivity with empty id (conversation={Key})",
                activity.Conversation?.CanonicalKey);
            return;
        }

        if (State.ProcessedMessageIds.Contains(activity.Id))
        {
            Logger.LogInformation(
                "Duplicate inbound activity {ActivityId} (conversation={Key}); skipping turn",
                activity.Id, activity.Conversation?.CanonicalKey);
            await ClearReplyLifecyclesAsync(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity, "duplicate_activity");
            return;
        }

        // Implement (issue #694):
        //   Behavior: relay turns consult ChatRouteResolver during admission before runner dispatch.
        //   Why this shape: routing remains a boundary decision and does not add an actor hop before the existing run handoff.
        var targetRef = await ResolveInboundTargetRefAsync(activity, CancellationToken.None);
        if (targetRef.Reject is not null)
        {
            var rejected = new ConversationContinueFailedEvent
            {
                CommandId = string.Empty,
                CorrelationId = activity.Id,
                CausationId = string.Empty,
                Kind = FailureKind.PermanentAdapterError,
                ErrorCode = "chat_route_rejected",
                ErrorSummary = string.IsNullOrWhiteSpace(targetRef.Reject.Reason)
                    ? "The chat route policy rejected this request."
                    : targetRef.Reject.Reason,
                NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
                FailedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await PersistDomainEventAsync(rejected);
            await ClearReplyLifecyclesAsync(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity, "chat_route_rejected");
            return;
        }

        var runner = ResolveRunner();
        // Tell the runner's group-chat gate when this inbound replies to one of the bot's own
        // messages, so a thread reply addresses the bot without a re-@-mention.
        var inboundContext = runtimeContext with
        {
            IsReplyToBot = ConversationBotMessageLedger.IsReplyToBotMessage(
                State.BotSentPlatformMessageIds,
                activity.ReplyToActivityId),
        };
        var result = await runner.RunInboundAsync(activity, inboundContext, CancellationToken.None);
        var runnerResultKind = result.LlmReplyRequest is not null
            ? "llm_reply_requested"
            : result.WorkflowDraftRunRequest is not null
                ? "workflow_draft_run_requested"
                : result.Success && result.SentActivityId.StartsWith("ignored:", StringComparison.Ordinal)
                    ? "ignored"
                    : result.Success
                        ? "sent"
                        : "failed";
        Logger.LogInformation(
            "Conversation inbound runner result: activity={ActivityId}, kind={ResultKind}, sent={SentId}, failureKind={FailureKind}, errorCode={ErrorCode}, retainedHistoryClear={RetainedHistoryClear}",
            activity.Id,
            runnerResultKind,
            result.SentActivityId,
            result.FailureKind,
            result.ErrorCode,
            result.RetainedHistoryClearRequested);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.RetainedHistoryClearRequested)
        {
            var cleared = new ConversationRetainedHistoryClearedEvent
            {
                ProcessedActivityId = activity.Id ?? string.Empty,
                ClearedEntryCount = State.RetainedHistory.Count,
                ClearedAtUnixMs = nowMs,
            };
            await PersistDomainEventAsync(cleared);
            Logger.LogInformation(
                "Cleared conversation retained history on user request: conversation={Key} entries={Count}",
                activity.Conversation?.CanonicalKey,
                cleared.ClearedEntryCount);
        }

        if (result.LlmReplyRequest is not null)
        {
            if (string.IsNullOrWhiteSpace(result.LlmReplyRequest.RunId))
            {
                await DropNewLlmReplyWithoutRunIdAsync(result.LlmReplyRequest);
                return;
            }

            // The transient run command copy keeps the raw reply token and per-call credentials
            // so the run actor can echo them back inside LlmReplyReadyEvent and forward them to
            // the LLM call. The persisted copy keeps only encrypted runtime-secret references.
            var runCopy = result.LlmReplyRequest.Clone();
            runCopy.TargetActorId = Id;
            runCopy.TargetRef = targetRef.Clone();
            // Refactor (iter98/cluster-002): Old=ConversationGAgent filled run_id from correlation_id; New=producer must supply run_id before this handoff.
            runCopy.RunId = NormalizeOptional(runCopy.RunId)!;
            ApplyRuntimeReplyToken(runCopy, runtimeContext);
            RestoreRuntimeTransportCredentials(runCopy.Activity, runtimeContext);
            await AttachRelayRuntimeSecretReferencesAsync(runCopy, runtimeContext, CancellationToken.None);
            runCopy.PriorHistory.Clear();
            runCopy.PriorHistory.AddRange(State.RetainedHistory.Select(entry => entry.Clone()));
            runCopy.RecentAttachmentActivities.Clear();
            runCopy.RecentAttachmentActivities.AddRange(SelectRecentAttachmentActivities(State, nowMs));
            var persistedCopy = runCopy.Clone();
            persistedCopy.ReplyToken = string.Empty;
            persistedCopy.ReplyTokenExpiresAtUnixMs = 0;
            persistedCopy.Activity = CloneForDurableState(persistedCopy.Activity);
            persistedCopy.TargetRef = null;
            persistedCopy.LlmControl = null;
            persistedCopy.PriorHistory.Clear();
            persistedCopy.RecentAttachmentActivities.Clear();
            StripRuntimeCredentialsFromToolContext(persistedCopy);
            LlmReplyCredentialMetadataKeys.StripFrom(persistedCopy.Metadata);
            await PersistDomainEventAsync(persistedCopy);
            await DispatchPendingLlmReplyAsync(runCopy, CancellationToken.None);
            Logger.LogInformation(
                "Accepted inbound activity for deferred LLM reply: activity={ActivityId} conversation={Key}",
                activity.Id,
                activity.Conversation?.CanonicalKey);
            return;
        }

        if (result.WorkflowDraftRunRequest is not null)
        {
            if (string.IsNullOrWhiteSpace(result.WorkflowDraftRunRequest.RunId))
            {
                await DropNewWorkflowDraftRunWithoutRunIdAsync(result.WorkflowDraftRunRequest);
                return;
            }

            var runCopy = result.WorkflowDraftRunRequest.Clone();
            runCopy.TargetActorId = Id;
            runCopy.RunId = NormalizeOptional(runCopy.RunId)!;
            ApplyRuntimeReplyToken(runCopy, runtimeContext);
            RestoreRuntimeTransportCredentials(runCopy.Activity, runtimeContext);
            if (!string.IsNullOrWhiteSpace(runtimeContext.NyxUserAccessToken))
                runCopy.NyxUserAccessToken = runtimeContext.NyxUserAccessToken.Trim();
            await AttachWorkflowRuntimeSecretReferencesAsync(runCopy, runtimeContext, CancellationToken.None);

            var persistedCopy = runCopy.Clone();
            persistedCopy.ReplyToken = string.Empty;
            persistedCopy.ReplyTokenExpiresAtUnixMs = 0;
            persistedCopy.NyxUserAccessToken = string.Empty;
            persistedCopy.Activity = CloneForDurableState(persistedCopy.Activity);
            await PersistDomainEventAsync(persistedCopy);
            await DispatchPendingWorkflowDraftRunAsync(runCopy, CancellationToken.None);
            Logger.LogInformation(
                "Accepted inbound activity for workflow draft run: activity={ActivityId} conversation={Key}",
                activity.Id,
                activity.Conversation?.CanonicalKey);
            return;
        }

        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = activity.Id,
                CausationCommandId = string.Empty,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = result.AuthPrincipal,
                Conversation = activity.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? new MessageContent(),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            await PersistDomainEventAsync(completed);
            await ClearReplyLifecyclesAsync(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity, "turn_completed");
            Logger.LogInformation(
                "Completed inbound turn: activity={ActivityId} sent={SentId} conversation={Key}",
                activity.Id, result.SentActivityId, activity.Conversation?.CanonicalKey);
            return;
        }

        if (result.FailureKind == FailureKind.TransientAdapterError)
        {
            await HandleInboundTurnTransientFailureAsync(activity, runtimeContext, result, nowMs);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = string.Empty,
            CorrelationId = activity.Id,
            CausationId = string.Empty,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        await PersistDomainEventAsync(failed);
        await ClearReplyLifecyclesAsync(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity, "inbound_retries_exhausted");
        Logger.LogWarning(
            "Inbound turn failed: activity={ActivityId} code={Code} kind={Kind}",
            activity.Id, result.ErrorCode, result.FailureKind);
    }

    private static void StripRuntimeCredentialsFromToolContext(NeedsLlmReplyEvent request)
    {
        if (request.ToolContext is null)
            return;

        var durableContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
        {
            Credentials = AgentToolCredentials.Empty,
        };
        request.ToolContext = HasDurableToolContext(durableContext)
            ? durableContext.ToPayload()
            : null;
    }

    private static bool HasDurableToolContext(AgentToolExecutionContext context) =>
        !string.IsNullOrWhiteSpace(context.Request.RequestId) ||
        !string.IsNullOrWhiteSpace(context.Request.CallId) ||
        !string.IsNullOrWhiteSpace(context.Caller.ScopeId) ||
        !string.IsNullOrWhiteSpace(context.Caller.OwnerSubject) ||
        !string.IsNullOrWhiteSpace(context.Caller.ResponseId) ||
        !string.IsNullOrWhiteSpace(context.Channel.Platform) ||
        !string.IsNullOrWhiteSpace(context.Channel.SenderId) ||
        !string.IsNullOrWhiteSpace(context.Channel.RegistrationScopeId) ||
        !string.IsNullOrWhiteSpace(context.Channel.MessageId) ||
        !string.IsNullOrWhiteSpace(context.Channel.PlatformMessageId) ||
        !string.IsNullOrWhiteSpace(context.Channel.DeliveryTargetId) ||
        !string.IsNullOrWhiteSpace(context.SenderBinding.BindingId) ||
        !string.IsNullOrWhiteSpace(context.SenderBinding.NyxUserId) ||
        context.NyxIdAuthority.IsComplete ||
        !string.IsNullOrWhiteSpace(context.Routing.ModelOverride) ||
        !string.IsNullOrWhiteSpace(context.Routing.NyxIdRoutePreference) ||
        context.Routing.MaxToolRoundsOverride.HasValue ||
        !string.IsNullOrWhiteSpace(context.Routing.UserMemoryPrompt) ||
        !string.IsNullOrWhiteSpace(context.ConnectedServices.ContextJson) ||
        context.ExternalMetadata.Count > 0 ||
        context.SkillRecovery.RequireInitialOrnnSearch ||
        context.SkillRecovery.RequireOrnnSearchOnBlocker ||
        !string.IsNullOrWhiteSpace(context.SkillRecovery.CommandName) ||
        !string.IsNullOrWhiteSpace(context.SkillRecovery.OriginalCommand) ||
        !string.IsNullOrWhiteSpace(context.SkillRecovery.PrimarySkillName) ||
        context.SkillRecovery.MaxOrnnSearchAttempts > 0;

    private async Task<ChatRouteAction> ResolveInboundTargetRefAsync(
        ChatActivity activity,
        CancellationToken ct)
    {
        var queryPort = Services.GetService<IChatRoutePolicyQueryPort>();
        var resolver = Services.GetService<ChatRouteResolver>();
        var callerScope = TryBuildRelayCallerScope(activity);
        if (queryPort is null || resolver is null || callerScope is null)
            return new ChatRouteAction();

        var snapshot = await queryPort.LookupForCallerAsync(callerScope, ct);
        var input = new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxRelay,
            CallerScope = callerScope.Clone(),
            Channel = callerScope.Platform,
            CommandName = ExtractCommandName(activity.Content?.Text),
            ContentHint = string.Empty,
            ToolMode = ToolMode.None,
        };
        var decision = resolver.Resolve(snapshot, input);
        return decision.Action.Clone();
    }

    private static OwnerScope? TryBuildRelayCallerScope(ChatActivity activity)
    {
        var platform = NormalizeOptional(activity.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(activity.ChannelId?.Value);
        var registrationScopeId = NormalizeOptional(activity.TransportExtras?.NyxRegistrationScopeId) ??
                                  NormalizeOptional(activity.Bot?.Value);
        var senderId = NormalizeOptional(activity.From?.CanonicalId);
        if (platform is null || registrationScopeId is null || senderId is null)
            return null;

        // The sender's NyxID is resolved by the relay ingress (NyxID `/me` with the
        // user access token) and stashed in TransportExtras. Using it here matches
        // per-user channel policies; an empty value falls through to scope-only
        // policies (same key shape as policy upserts without a per-user binding).
        var senderNyxUserId = NormalizeOptional(activity.TransportExtras?.NyxSenderUserId)
                              ?? string.Empty;
        return OwnerScope.ForChannel(senderNyxUserId, platform, registrationScopeId, senderId);
    }

    private static string ExtractCommandName(string? text)
    {
        var first = ChannelTextCommandParser.Tokenize(text).FirstOrDefault();
        return first is { Length: > 1 } && first[0] == '/' ? first : string.Empty;
    }

    /// <summary>
    /// Mirrors the deferred LLM reply retry pattern for the inbound-turn path: bounds the retry
    /// count, schedules a durable reminder for the next attempt, or emits a terminal
    /// <see cref="ConversationContinueFailedEvent"/> on exhaustion so the pending entry is
    /// reaped by the state matcher.
    /// </summary>
    private async Task HandleInboundTurnTransientFailureAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        ConversationTurnResult result,
        long nowMs)
    {
        var existingPending = FindPendingInboundTurn(activity.Id);
        var nextRetryCount = (existingPending?.RetryCount ?? 0) + 1;

        if (nextRetryCount > MaxInboundTurnRetryCount)
        {
            var failed = new ConversationContinueFailedEvent
            {
                CommandId = string.Empty,
                CorrelationId = activity.Id,
                CausationId = string.Empty,
                Kind = FailureKind.TransientAdapterError,
                ErrorCode = string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? "inbound_turn_retries_exhausted"
                    : result.ErrorCode,
                ErrorSummary = string.IsNullOrWhiteSpace(result.ErrorSummary)
                    ? "Inbound turn retries exhausted."
                    : result.ErrorSummary,
                NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
                FailedAtUnixMs = nowMs,
            };
            await PersistDomainEventAsync(failed);
            await ClearReplyLifecyclesAsync(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity, "turn_failed");
            Logger.LogWarning(
                "Inbound turn retries exhausted: activity={ActivityId} retryCount={RetryCount} code={Code}",
                activity.Id,
                nextRetryCount - 1,
                result.ErrorCode);
            return;
        }

        var requested = result.RetryAfter ?? DeferredInboundTurnRetryDelay;
        // Floor to reminder granularity so the durable scheduler does not silently round the
        // request up past the retry window and drop the dispatch (same trap the LLM reply
        // retry path has to guard against).
        var retryAfter = requested < DeferredInboundTurnRetryDelay
            ? DeferredInboundTurnRetryDelay
            : requested;
        var firstFailedUnixMs = existingPending is { FirstFailedUnixMs: > 0 }
            ? existingPending.FirstFailedUnixMs
            : nowMs;
        var nextRetryUnixMs = DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeMilliseconds();

        var scheduled = new InboundTurnRetryScheduledEvent
        {
            ActivityId = activity.Id,
            Activity = CloneForDurableState(activity),
            RetryCount = nextRetryCount,
            FirstFailedUnixMs = firstFailedUnixMs,
            NextRetryUnixMs = nextRetryUnixMs,
            ScheduledAtUnixMs = nowMs,
        };
        await PersistDomainEventAsync(scheduled);
        await ScheduleDeferredInboundTurnRetryAsync(activity.Id, retryAfter, CancellationToken.None);

        Logger.LogInformation(
            "Scheduled inbound turn retry: activity={ActivityId} retryCount={RetryCount} retryAfter={RetryAfter} code={Code}",
            activity.Id,
            nextRetryCount,
            retryAfter,
            result.ErrorCode);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync(DeferredLlmReplyDispatchRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var pendingRequest = FindPendingLlmReplyRequest(evt.CorrelationId);
        if (pendingRequest is null)
        {
            Logger.LogDebug(
                "Ignoring deferred LLM dispatch trigger without pending request: correlation={CorrelationId}",
                evt.CorrelationId);
            return;
        }

        await DispatchPendingLlmReplyAsync(pendingRequest, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDeferredLlmReplyDroppedAsync(DeferredLlmReplyDroppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // ADR-0021 §6 / canon §9 absorbing-finalized: a late drop notification for an
        // already-finalized turn (e.g. the run actor's terminal-cleanup callback fires
        // after a successful reply already landed) must no-op rather than overwrite the
        // turn outcome with a synthetic ConversationContinueFailedEvent.
        if (IsLlmReplyTurnFinalized(evt.CorrelationId))
        {
            Logger.LogDebug(
                "Ignoring deferred LLM reply drop for already-finalized turn: correlation={CorrelationId} reason={Reason}",
                evt.CorrelationId,
                evt.Reason);
            return;
        }

        var pending = FindPendingLlmReplyRequest(evt.CorrelationId);
        if (pending is null)
        {
            Logger.LogDebug(
                "Ignoring deferred LLM reply drop without pending request: correlation={CorrelationId} reason={Reason}",
                evt.CorrelationId,
                evt.Reason);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(evt.Reason) ? "deferred_llm_reply_dropped" : evt.Reason;
        var failed = new ConversationContinueFailedEvent
        {
            CommandId = BuildLlmReplyCommandId(evt.CorrelationId),
            CorrelationId = evt.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = reason,
            ErrorSummary = "Deferred LLM reply request was dropped by the run actor pre-LLM gate.",
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = evt.DroppedAtUnixMs > 0
                ? evt.DroppedAtUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await PersistDomainEventAsync(failed);
        await ClearReplyLifecyclesAsync(evt.CorrelationId, pending.Activity, "deferred_llm_reply_dropped");

        Logger.LogInformation(
            "Retired pending LLM reply after run drop: correlation={CorrelationId} reason={Reason}",
            evt.CorrelationId,
            reason);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeferredInboundTurnRetryRequestedAsync(DeferredInboundTurnRetryRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var pending = FindPendingInboundTurn(evt.ActivityId);
        if (pending is null || pending.Activity is null)
        {
            // Pending entry already reaped — either by ApplyTurnCompleted (success), the
            // terminal NotRetryable ApplyContinueFailed (exhaustion), or ApplyLlmReplyRequested
            // (redelivery accepted into the LLM reply pipeline before this retry could fire).
            Logger.LogDebug(
                "Ignoring deferred inbound turn retry without pending entry: activity={ActivityId}",
                evt.ActivityId);
            return;
        }

        var runtimeContext = BuildNyxRelayRuntimeContext(
            pending.Activity.OutboundDelivery?.CorrelationId,
            pending.Activity);

        if (IsRelayActivity(pending.Activity) && runtimeContext.NyxRelayReplyToken is null)
        {
            await PersistMissingRuntimeCredentialFailureAsync(
                commandId: string.Empty,
                correlationId: pending.ActivityId,
                errorCode: "missing_runtime_reply_token",
                errorSummary: "Pending relay inbound retry cannot continue after rehydration because reply credentials are runtime-only.",
                failedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }

        var activity = pending.Activity.Clone();
        RestoreRuntimeTransportCredentials(activity, runtimeContext);
        await HandleInboundActivityCoreAsync(activity, runtimeContext);
    }

    private async Task DispatchPendingLlmReplyAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        var dispatcher = Services.GetService<IChannelLlmReplyRunDispatcher>();
        if (dispatcher is null)
        {
            Logger.LogWarning(
                "Channel LLM reply run dispatcher not registered; scheduling durable retry: correlation={CorrelationId}",
                request.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(request, DeferredLlmDispatchRetryDelay, ct);
            return;
        }

        var dispatchRequest = request.Clone();
        try
        {
            await RestoreRelayRuntimeCredentialsAsync(dispatchRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to resolve relay runtime credentials; scheduling durable retry: correlation={CorrelationId}",
                request.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(request, DeferredLlmDispatchRetryDelay, ct);
            return;
        }

        if (IsRelayActivity(dispatchRequest.Activity) && string.IsNullOrWhiteSpace(dispatchRequest.ReplyToken))
        {
            await PersistMissingRuntimeCredentialFailureAsync(
                BuildLlmReplyCommandId(dispatchRequest.CorrelationId),
                dispatchRequest.CorrelationId,
                "missing_runtime_reply_token",
                "Pending relay LLM reply cannot be dispatched because its runtime reply credential is unavailable or expired.",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }

        if (string.IsNullOrWhiteSpace(dispatchRequest.RunId))
        {
            await DropLegacyPendingLlmReplyWithoutRunIdAsync(dispatchRequest);
            return;
        }

        try
        {
            // Refactor (iter56/cluster-935-agent-run-actor-admission): old=dispatcher in-process admission, new=actor-owned admission with plain Task
            //   Conversation observes only dispatch handoff success/failure here.
            //   Run duplicate/stale decisions are committed by AgentRunGAgent events.
            await dispatcher.DispatchAsync(dispatchRequest, ct);
            Logger.LogInformation(
                "Dispatched LLM reply run request: runId={RunId} correlation={CorrelationId} conversation={Key}",
                dispatchRequest.RunId,
                dispatchRequest.CorrelationId,
                dispatchRequest.Activity?.Conversation?.CanonicalKey);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to dispatch LLM reply run request; scheduling durable retry: correlation={CorrelationId}",
                request.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(request, DeferredLlmDispatchRetryDelay, ct);
        }
    }

    private async Task DispatchPendingWorkflowDraftRunAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
    {
        var dispatcher = Services.GetService<IChannelWorkflowDraftRunInteractionPort>();
        if (dispatcher is null)
        {
            Logger.LogWarning(
                "Channel workflow draft-run interaction port not registered; failing request: correlation={CorrelationId}",
                request.CorrelationId);
            await PersistWorkflowDraftRunFailureAsync(
                request,
                "workflow_draft_run_interaction_port_unavailable",
                "Workflow draft-run interaction port is unavailable.",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }

        var dispatchRequest = request.Clone();
        try
        {
            await RestoreWorkflowRuntimeCredentialsAsync(dispatchRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to resolve workflow draft-run runtime credentials: correlation={CorrelationId}",
                request.CorrelationId);
            await PersistWorkflowDraftRunFailureAsync(
                request,
                "workflow_draft_run_runtime_credential_unavailable",
                "Workflow draft-run runtime credentials could not be resolved.",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }

        if (IsRelayActivity(dispatchRequest.Activity))
        {
            if (string.IsNullOrWhiteSpace(dispatchRequest.ReplyToken))
            {
                await PersistMissingRuntimeCredentialFailureAsync(
                    BuildWorkflowDraftRunCommandId(request.CorrelationId),
                    request.CorrelationId,
                    "missing_runtime_reply_token",
                    "Pending relay workflow draft-run cannot be dispatched after rehydration because reply credentials are runtime-only.",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return;
            }

            if (string.IsNullOrWhiteSpace(dispatchRequest.NyxUserAccessToken))
            {
                await PersistMissingRuntimeCredentialFailureAsync(
                    BuildWorkflowDraftRunCommandId(request.CorrelationId),
                    request.CorrelationId,
                    "missing_runtime_user_access_token",
                    "Pending relay workflow draft-run cannot be dispatched after rehydration because user credentials are runtime-only.",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return;
            }
        }

        try
        {
            await dispatcher.DispatchAsync(dispatchRequest, ct);
            Logger.LogInformation(
                "Dispatched workflow draft-run request: runId={RunId} correlation={CorrelationId} conversation={Key}",
                request.RunId,
                request.CorrelationId,
                request.Activity?.Conversation?.CanonicalKey);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to dispatch workflow draft-run request: correlation={CorrelationId}",
                request.CorrelationId);
            await PersistWorkflowDraftRunFailureAsync(
                request,
                "workflow_draft_run_dispatch_failed",
                "Workflow draft-run dispatch failed.",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private async Task PersistWorkflowDraftRunFailureAsync(
        NeedsWorkflowDraftRunEvent request,
        string errorCode,
        string errorSummary,
        long failedAtUnixMs)
    {
        await PersistDomainEventAsync(new ConversationContinueFailedEvent
        {
            CommandId = BuildWorkflowDraftRunCommandId(request.CorrelationId),
            CorrelationId = request.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = failedAtUnixMs,
        });
    }

    private async Task DropLegacyPendingLlmReplyWithoutRunIdAsync(NeedsLlmReplyEvent request)
    {
        // Refactor (iter98/cluster-002): Old=dispatcher recovered actor identity from correlation_id; New=legacy persisted requests without run_id are explicitly quarantined/dropped.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Logger.LogWarning(
            "Dropping legacy pending LLM reply request without run_id; correlation remains trace-only. correlation={CorrelationId}",
            request.CorrelationId);
        await PersistDomainEventAsync(new ConversationContinueFailedEvent
        {
            CommandId = BuildLlmReplyCommandId(request.CorrelationId),
            CorrelationId = request.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = "legacy_pending_llm_reply_missing_run_id_dropped",
            ErrorSummary = "Legacy pending LLM reply request did not contain run_id and was dropped instead of deriving actor identity from correlation_id.",
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = nowMs,
        });
    }

    private async Task DropNewLlmReplyWithoutRunIdAsync(NeedsLlmReplyEvent request)
    {
        // Refactor (iter98/cluster-002): Old=ConversationGAgent supplied implicit run_id; New=new dispatch without run_id is rejected before persistence/dispatch.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Logger.LogWarning(
            "Rejecting deferred LLM reply request without run_id before persistence/dispatch. correlation={CorrelationId}",
            request.CorrelationId);
        await PersistDomainEventAsync(new ConversationContinueFailedEvent
        {
            CommandId = BuildLlmReplyCommandId(request.CorrelationId),
            CorrelationId = request.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = "deferred_llm_reply_missing_run_id_rejected",
            ErrorSummary = "Deferred LLM reply request must carry explicit run_id before persistence and dispatch.",
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = nowMs,
        });
    }

    private async Task DropNewWorkflowDraftRunWithoutRunIdAsync(NeedsWorkflowDraftRunEvent request)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Logger.LogWarning(
            "Rejecting workflow draft-run request without run_id before persistence/dispatch. correlation={CorrelationId}",
            request.CorrelationId);
        await PersistDomainEventAsync(new ConversationContinueFailedEvent
        {
            CommandId = BuildWorkflowDraftRunCommandId(request.CorrelationId),
            CorrelationId = request.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = "workflow_draft_run_missing_run_id_rejected",
            ErrorSummary = "Workflow draft-run request must carry explicit run_id before persistence and dispatch.",
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = nowMs,
        });
    }

    [EventHandler]
    public async Task HandleLlmReplyReadyAsync(LlmReplyReadyEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var commandId = ResolvePendingReplyCommandId(evt.CorrelationId);
        var pendingRequest = FindPendingLlmReplyRequest(evt.CorrelationId);
        var pendingWorkflowRequest = FindPendingWorkflowDraftRunRequest(evt.CorrelationId);
        if (IsReplyTurnFinalized(evt.CorrelationId))
        {
            Logger.LogInformation(
                "Duplicate LLM reply ready event {CorrelationId} (conversation={Key}); skipping outbound",
                evt.CorrelationId,
                State.Conversation?.CanonicalKey);
            return;
        }

        var referenceActivity = pendingRequest?.Activity ?? pendingWorkflowRequest?.Activity ?? evt.Activity;
        var runtimeContext = await BuildNyxRelayRuntimeContextForReplyAsync(
            evt,
            referenceActivity,
            CancellationToken.None);
        Logger.LogInformation(
            "Received LLM reply ready: correlation={CorrelationId} terminal={TerminalState} replyTokenSource={Source}",
            evt.CorrelationId,
            evt.TerminalState,
            DescribeReplyTokenSource(evt, runtimeContext));

        if (await TryCompleteStreamedReplyAsync(evt, commandId, referenceActivity, runtimeContext))
            return;

        var runner = ResolveRunner();
        var result = await runner.RunLlmReplyAsync(
            evt,
            runtimeContext,
            CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = string.Empty,
                CausationCommandId = commandId,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = string.IsNullOrWhiteSpace(result.AuthPrincipal) ? "bot" : result.AuthPrincipal,
                Conversation = evt.Activity?.Conversation?.Clone() ?? State.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? evt.Outbound?.Clone() ?? new MessageContent(),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            completed.AppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
            // ADR-0021 chain.delivered observable: persist the user-visible delivery ack
            // before the turn-completed summary event so readers do not need to infer
            // delivery status from the channel sink return code, and so existing
            // "events.Last() is turn-completed" consumers stay correct.
            var delivered = new LlmReplyDeliveredEvent
            {
                CorrelationId = evt.CorrelationId ?? string.Empty,
                RunId = evt.RunId ?? string.Empty,
                AckedAtUnixMs = nowMs,
                ChannelMessageId = result.OutboundDelivery?.ReplyMessageId ?? string.Empty,
            };
            var deliveryProduced = BuildDeliveryProducedEvent(
                DeliveryKind.TextMessage,
                DeliveryStatus.Succeeded,
                referenceActivity,
                evt.RunId,
                evt.CorrelationId,
                commandId,
                sourceEventId: evt.CorrelationId,
                providerMessageId: result.OutboundDelivery?.ReplyMessageId,
                cardId: string.Empty);
            await PersistReplyReadyEventsWithLocalRetryAsync(
                evt.CorrelationId,
                "completed",
                [delivered, deliveryProduced, completed],
                CancellationToken.None);
            await ClearReplyLifecyclesAsync(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity, "llm_reply_completed");
            Logger.LogInformation(
                "Completed deferred LLM reply: correlation={CorrelationId} sent={SentId} conversation={Key}",
                evt.CorrelationId,
                result.SentActivityId,
                completed.Conversation?.CanonicalKey);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = commandId,
            CorrelationId = evt.CorrelationId,
            CausationId = string.Empty,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        // ADR-0021 chain.delivered failure observable: structured delivery failure persists
        // before the chain-finalizing failure event so existing "events.Last() is
        // ConversationContinueFailedEvent" consumers stay correct.
        var deliveryFailed = new LlmReplyDeliveryFailedEvent
        {
            CorrelationId = evt.CorrelationId ?? string.Empty,
            RunId = evt.RunId ?? string.Empty,
            FailedAtUnixMs = nowMs,
            ErrorCode = result.ErrorCode ?? string.Empty,
            ErrorMessage = result.ErrorSummary ?? string.Empty,
        };
        var failedDeliveryProduced = BuildDeliveryProducedEvent(
            DeliveryKind.TextMessage,
            DeliveryStatus.FailedPreSend,
            referenceActivity,
            evt.RunId,
            evt.CorrelationId,
            commandId,
            sourceEventId: evt.CorrelationId,
            providerMessageId: string.Empty,
            cardId: string.Empty);
        await PersistReplyReadyEventsWithLocalRetryAsync(
            evt.CorrelationId,
            "failed",
            [deliveryFailed, failedDeliveryProduced, failed],
            CancellationToken.None);
        if (failed.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
            await ClearReplyLifecyclesAsync(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity, "llm_reply_failed_not_retryable");
        if (failed.RetryPolicyCase != ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable &&
            pendingRequest is not null)
        {
            var requested = failed.RetryAfterMs > 0
                ? TimeSpan.FromMilliseconds(failed.RetryAfterMs)
                : DeferredLlmDispatchRetryDelay;
            // Floor the retry delay to the durable scheduler's reliable granularity. Orleans
            // Reminders effectively round sub-minute schedules up to the next tick, so any
            // shorter requested delay would silently miss; honour at least DeferredLlmDispatchRetryDelay.
            var retryAfter = requested < DeferredLlmDispatchRetryDelay
                ? DeferredLlmDispatchRetryDelay
                : requested;
            await ScheduleDeferredLlmReplyDispatchAsync(
                pendingRequest,
                retryAfter,
                CancellationToken.None);
        }
        Logger.LogWarning(
            "Deferred LLM reply failed: correlation={CorrelationId} code={Code} kind={Kind}",
            evt.CorrelationId,
            result.ErrorCode,
            result.FailureKind);
    }

    [EventHandler]
    public async Task HandleLarkCardDeliveryCompletedAsync(LarkCardDeliveryCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (IsReplyTurnFinalized(evt.CorrelationId))
        {
            Logger.LogInformation(
                "Duplicate Lark card delivery completion {CorrelationId} (conversation={Key}); skipping",
                evt.CorrelationId,
                State.Conversation?.CanonicalKey);
            return;
        }

        var nowMs = evt.CompletedAtUnixMs > 0
            ? evt.CompletedAtUnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var commandId = NormalizeOptional(evt.CommandId) ?? BuildLlmReplyCommandId(evt.CorrelationId);
        var activity = evt.Activity?.Clone() ?? new ChatActivity();
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = string.Empty,
            CausationCommandId = commandId,
            SentActivityId = $"lark-card-stream:{evt.CardMessageId ?? string.Empty}",
            AuthPrincipal = "bot",
            Conversation = activity.Conversation?.Clone()
                           ?? State.Conversation?.Clone()
                           ?? new ConversationReference(),
            Outbound = new MessageContent { Text = evt.OutboundText ?? string.Empty },
            CompletedAtUnixMs = nowMs,
            OutboundDelivery = ToOutboundDeliveryReceipt(activity.OutboundDelivery),
        };
        completed.AppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));

        if (evt.DeliveryFailure is null)
        {
            var delivered = new LlmReplyDeliveredEvent
            {
                CorrelationId = evt.CorrelationId ?? string.Empty,
                RunId = evt.RunId ?? string.Empty,
                AckedAtUnixMs = nowMs,
                ChannelMessageId = completed.SentActivityId,
                // Bare Lark message id of the bot's streamed card — recorded so a later reply to it
                // is recognized as addressing the bot (channel_message_id is path-prefixed here).
                BotPlatformMessageId = evt.CardMessageId ?? string.Empty,
            };
            var deliveryProduced = BuildDeliveryProducedEvent(
                DeliveryKind.StreamingCard,
                DeliveryStatus.Succeeded,
                activity,
                evt.RunId,
                evt.CorrelationId,
                commandId,
                sourceEventId: evt.CorrelationId,
                providerMessageId: completed.SentActivityId,
                cardId: string.Empty,
                conversation: completed.Conversation);
            await PersistReplyReadyEventsWithLocalRetryAsync(
                evt.CorrelationId,
                "lark-card-completed",
                [delivered, deliveryProduced, completed],
                CancellationToken.None);
            if (evt.Activity is not null)
                _ = ObserveReplyDeliveredAsync(ResolveRunner(), evt.Activity);
        }
        else
        {
            var deliveryFailed = evt.DeliveryFailure.Clone();
            if (string.IsNullOrWhiteSpace(deliveryFailed.CorrelationId))
                deliveryFailed.CorrelationId = evt.CorrelationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deliveryFailed.RunId))
                deliveryFailed.RunId = evt.RunId ?? string.Empty;
            if (deliveryFailed.FailedAtUnixMs <= 0)
                deliveryFailed.FailedAtUnixMs = nowMs;
            var deliveryProduced = BuildDeliveryProducedEvent(
                DeliveryKind.StreamingCard,
                DeliveryStatus.FailedPostSend,
                activity,
                deliveryFailed.RunId,
                evt.CorrelationId,
                commandId,
                sourceEventId: evt.CorrelationId,
                providerMessageId: completed.SentActivityId,
                cardId: string.Empty,
                conversation: completed.Conversation);
            await PersistReplyReadyEventsWithLocalRetryAsync(
                evt.CorrelationId,
                "lark-card-failed",
                [deliveryFailed, deliveryProduced, completed],
                CancellationToken.None);
        }

        await ClearReplyLifecyclesAsync(evt.CorrelationId, evt.Activity, "lark_card_delivery_completed");
        Logger.LogInformation(
            "Completed card-streamed LLM reply: correlation={CorrelationId} cardMessageId={CardMessageId} conversation={Key}",
            evt.CorrelationId,
            evt.CardMessageId,
            completed.Conversation?.CanonicalKey);
    }

    private async Task<bool> PersistRelayAdmissionWithLocalRetryAsync(
        NyxRelayCallbackAdmittedEvent admitted,
        string relayApiKeyId,
        string callbackJti,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var absorbed = false;
                await PersistDomainEventAsync(
                    admitted,
                    ex =>
                    {
                        Logger.LogWarning(
                            ex,
                            "Relay admission hit optimistic concurrency; refreshing actor state and retrying locally because runtime credential envelope cannot be durably retried. activity={ActivityId} callbackJti={CallbackJti} attempt={Attempt}/{MaxAttempts}",
                            admitted.ActivityId,
                            callbackJti,
                            attempt,
                            RuntimeCredentialLocalOccRetryCount);
                        if (HasActiveRelayReplayClaim(relayApiKeyId, callbackJti, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                        {
                            if (FindPendingRelayAdmission(relayApiKeyId, callbackJti, admitted.ActivityId) is not null)
                            {
                                Logger.LogInformation(
                                    "Relay admission conflict resolved by existing pending admission; continuing with current runtime credentials. activity={ActivityId} callbackJti={CallbackJti}",
                                    admitted.ActivityId,
                                    callbackJti);
                                absorbed = true;
                                return Task.FromResult(true);
                            }

                            Logger.LogInformation(
                                "Relay admission conflict resolved by existing finalized claim; skipping duplicate callback. activity={ActivityId} callbackJti={CallbackJti}",
                                admitted.ActivityId,
                                callbackJti);
                            return Task.FromResult(true);
                        }

                        return Task.FromResult(false);
                    },
                    ct);
                if (absorbed)
                    return true;

                if (HasActiveRelayReplayClaim(relayApiKeyId, callbackJti, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) &&
                    FindPendingRelayAdmission(relayApiKeyId, callbackJti, admitted.ActivityId) is null)
                {
                    return false;
                }
                return true;
            }
            catch (EventStoreOptimisticConcurrencyException) when (attempt < RuntimeCredentialLocalOccRetryCount)
            {
            }
        }
    }

    private async Task PersistReplyReadyEventsWithLocalRetryAsync(
        string? correlationId,
        string outcome,
        IReadOnlyList<IMessage> events,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var absorbed = false;
                AssignDeliveryProducedVersions(events);
                await PersistDomainEventsAsync(
                    events,
                    ex =>
                    {
                        Logger.LogWarning(
                            ex,
                            "Reply-ready commit hit optimistic concurrency; refreshing actor state and retrying locally because runtime credential envelope cannot be durably retried. correlation={CorrelationId} outcome={Outcome} attempt={Attempt}/{MaxAttempts}",
                            correlationId,
                            outcome,
                            attempt,
                            RuntimeCredentialLocalOccRetryCount);
                        if (IsReplyTurnFinalized(correlationId))
                        {
                            absorbed = true;
                            return Task.FromResult(true);
                        }

                        return Task.FromResult(false);
                    },
                    ct);
                if (absorbed)
                    return;

                return;
            }
            catch (EventStoreOptimisticConcurrencyException) when (attempt < RuntimeCredentialLocalOccRetryCount)
            {
            }
        }
    }

    /// <summary>
    /// Drives one progressive streaming delta: placeholder send on the first chunk, edit-in-place
    /// on subsequent chunks. Runs inside the actor turn so the reply token stays within the actor
    /// boundary and the edit ordering is enforced by actor serialization.
    /// </summary>
    [EventHandler]
    public Task HandleLlmReplyStreamChunkAsync(LlmReplyStreamChunkEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return HandleNyxRelayStreamingChunkCoreAsync(evt);
    }

    private async Task HandleNyxRelayStreamingChunkCoreAsync(LlmReplyStreamChunkEvent evt)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null || evt.Activity is null || string.IsNullOrWhiteSpace(evt.AccumulatedText))
        {
            Logger.LogDebug(
                "Dropping malformed streaming chunk: correlation={CorrelationId}",
                evt.CorrelationId);
            return;
        }

        if (IsLlmReplyTurnFinalized(evt.CorrelationId))
        {
            // Turn already finalized; drop any late chunk that sneaks in via the actor inbox.
            return;
        }

        var state = GetOrInitNyxRelayStreamingState(correlationId);
        if (ShouldSkipNyxRelayStreamingForUnavailable(state, NyxRelayStreamingGuardSource.AcceptInterimChunk))
            return;

        if (state.InFlight is not null)
        {
            await PersistNyxRelayTextCoalescedStateAsync(correlationId, state, evt.AccumulatedText);
            return;
        }

        var runtimeContext = BuildNyxRelayRuntimeContext(
            evt.CorrelationId,
            evt.Activity,
            evt.ReplyToken,
            evt.ReplyTokenExpiresAtUnixMs);
        if (runtimeContext.NyxRelayReplyToken is null)
        {
            Logger.LogInformation(
                "Streaming chunk received but relay reply token is unavailable; disabling streaming for turn. correlation={CorrelationId}",
                evt.CorrelationId);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                NyxRelayStreamingPhase.DisabledPreSend,
                terminalReason: "no_reply_token");
            return;
        }

        var sequence = state.EditCount + 1L;
        var generation = NextNyxRelayTextOperationGeneration(state);
        await TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            state.Phase,
            fieldUpdate: s => s with
            {
                InFlight = new NyxRelayTextOperationInFlight(
                    NyxRelayTextOperationKind.Interim,
                    sequence,
                    generation),
                OperationGeneration = generation,
                PendingAccumulatedText = evt.AccumulatedText,
                RetryAttempt = 0,
            });
        await ScheduleNyxRelayTextOperationTimeoutAsync(
            correlationId,
            NyxRelayTextOperationKind.Interim,
            sequence,
            generation,
            evt,
            state.PlatformMessageId,
            commandId: string.Empty,
            finalText: string.Empty,
            lastFlushedText: state.LastFlushedText,
            editCount: state.EditCount,
            CancellationToken.None);
        await StartNyxRelayTextOperationAsync(
            NyxRelayTextOperationKind.Interim,
            evt,
            correlationId,
            state.PlatformMessageId,
            commandId: string.Empty,
            finalText: string.Empty,
            lastFlushedText: state.LastFlushedText,
            editCount: state.EditCount,
            sequence,
            generation);
    }

    private async Task<bool> TryCompleteStreamedReplyAsync(
        LlmReplyReadyEvent evt,
        string commandId,
        ChatActivity? referenceActivity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return false;

        var state = GetOrInitNyxRelayStreamingState(correlationId);
        if (state.InFlight is not null)
        {
            if (evt.TerminalState == LlmReplyTerminalState.Failed)
            {
                var failureText = NormalizeOptional(evt.Outbound?.Text)
                    ?? NormalizeOptional(evt.ErrorSummary)
                    ?? "Sorry, the reply failed. Please try again.";
                await PersistNyxRelayTextCoalescedStateAsync(
                    correlationId,
                    state,
                    finalizeText: failureText,
                    finalizeCommandId: commandId,
                    terminalState: LlmReplyTerminalState.Failed,
                    appendedHistory: evt.AppendedHistory);
                return true;
            }

            if (evt.TerminalState == LlmReplyTerminalState.Completed)
            {
                await PersistNyxRelayTextCoalescedStateAsync(
                    correlationId,
                    state,
                    finalizeText: evt.Outbound?.Text ?? string.Empty,
                    finalizeCommandId: commandId,
                    terminalState: LlmReplyTerminalState.Completed,
                    appendedHistory: evt.AppendedHistory);
                return true;
            }
        }

        if (ShouldSkipNyxRelayStreamingForUnavailable(state, NyxRelayStreamingGuardSource.Finalize))
            return false;

        var platformMessageId = state.PlatformMessageId!;

        // Streaming-start already consumed the reply token. On Failed, falling through to
        // RunLlmReplyAsync would issue a fresh /reply against the dead token and surface
        // as `401 Reply token already used` to NyxID — leaving the user staring at the
        // streaming partial (often just "...") forever with no error explanation. Self-heal
        // by editing the existing placeholder in place with the classified failure text;
        // turn is then terminal (no retry, no second /reply).
        if (evt.TerminalState == LlmReplyTerminalState.Failed)
        {
            var failureText = NormalizeOptional(evt.Outbound?.Text)
                ?? NormalizeOptional(evt.ErrorSummary)
                ?? "Sorry, the reply failed. Please try again.";
            var failureChunk = new LlmReplyStreamChunkEvent
            {
                CorrelationId = evt.CorrelationId,
                RegistrationId = evt.RegistrationId,
                Activity = referenceActivity?.Clone() ?? evt.Activity?.Clone() ?? new ChatActivity(),
                AccumulatedText = failureText,
                ChunkAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            ApplyRuntimeReplyToken(failureChunk, runtimeContext);
            var sequence = state.EditCount + 1L;
            var generation = NextNyxRelayTextOperationGeneration(state);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                state.Phase,
                fieldUpdate: s => s with
                {
                    InFlight = new NyxRelayTextOperationInFlight(
                        NyxRelayTextOperationKind.FailureSelfHeal,
                        sequence,
                        generation),
                    OperationGeneration = generation,
                    RetryAttempt = 0,
                });
            await ScheduleNyxRelayTextOperationTimeoutAsync(
                correlationId,
                NyxRelayTextOperationKind.FailureSelfHeal,
                sequence,
                generation,
                failureChunk,
                platformMessageId,
                commandId,
                finalText: failureText,
                lastFlushedText: state.LastFlushedText,
                editCount: state.EditCount,
                CancellationToken.None);
            await StartNyxRelayTextOperationAsync(
                NyxRelayTextOperationKind.FailureSelfHeal,
                failureChunk,
                correlationId,
                platformMessageId,
                commandId,
                finalText: failureText,
                lastFlushedText: state.LastFlushedText,
                editCount: state.EditCount,
                sequence,
                generation);
            return true;
        }

        if (evt.TerminalState != LlmReplyTerminalState.Completed)
            return false;
        var finalText = evt.Outbound?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(finalText))
        {
            // Streaming rendered something partial but the LLM reported empty; the reply token
            // is dead (first chunk consumed it), so we cannot fall back to /reply. Accept the
            // last flushed text as the terminal user-visible state rather than spinning on a
            // dead token.
            Logger.LogWarning(
                "Streaming LLM reply final text was empty; persisting last flushed partial as terminal. correlation={CorrelationId} platformMessageId={PlatformMessageId}",
                evt.CorrelationId,
                platformMessageId);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                NyxRelayStreamingPhase.TerminalPartial,
                terminalReason: "empty_final_text");
            await PersistStreamedCompletionAsync(evt, commandId, referenceActivity, platformMessageId, state.LastFlushedText, state.EditCount);
            return true;
        }

        var edits = state.EditCount;
        if (!string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal))
        {
            var finalChunk = new LlmReplyStreamChunkEvent
            {
                CorrelationId = evt.CorrelationId,
                RegistrationId = evt.RegistrationId,
                Activity = referenceActivity?.Clone() ?? evt.Activity?.Clone() ?? new ChatActivity(),
                AccumulatedText = finalText,
                ChunkAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            ApplyRuntimeReplyToken(finalChunk, runtimeContext);
            var sequence = state.EditCount + 1L;
            var generation = NextNyxRelayTextOperationGeneration(state);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                state.Phase,
                fieldUpdate: s => s with
                {
                    InFlight = new NyxRelayTextOperationInFlight(
                        NyxRelayTextOperationKind.Final,
                        sequence,
                        generation),
                    OperationGeneration = generation,
                    RetryAttempt = 0,
                });
            await ScheduleNyxRelayTextOperationTimeoutAsync(
                correlationId,
                NyxRelayTextOperationKind.Final,
                sequence,
                generation,
                finalChunk,
                platformMessageId,
                commandId,
                finalText,
                state.LastFlushedText,
                state.EditCount,
                CancellationToken.None);
            await StartNyxRelayTextOperationAsync(
                NyxRelayTextOperationKind.Final,
                finalChunk,
                correlationId,
                platformMessageId,
                commandId,
                finalText,
                state.LastFlushedText,
                state.EditCount,
                sequence,
                generation);
            return true;
        }

        await TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            NyxRelayStreamingPhase.TerminalSucceeded,
            terminalReason: "completed");
        await PersistStreamedCompletionAsync(evt, commandId, referenceActivity, platformMessageId, finalText, edits);
        return true;
    }

    private Task<NyxRelayStreamingState> PersistNyxRelayTextCoalescedStateAsync(
        string correlationId,
        NyxRelayStreamingState state,
        string? accumulatedText = null,
        string? finalizeText = null,
        string? finalizeCommandId = null,
        LlmReplyTerminalState terminalState = LlmReplyTerminalState.Unspecified,
        IEnumerable<ConversationHistoryEntry>? appendedHistory = null) =>
        TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            state.Phase,
            fieldUpdate: s => s with
            {
                PendingAccumulatedText = NormalizeOptional(accumulatedText) ?? s.PendingAccumulatedText,
                PendingFinalizeText = NormalizeOptional(finalizeText) ?? s.PendingFinalizeText,
                PendingFinalizeCommandId = NormalizeOptional(finalizeCommandId) ?? s.PendingFinalizeCommandId,
                PendingTerminalState = terminalState == LlmReplyTerminalState.Unspecified
                    ? s.PendingTerminalState
                    : terminalState,
                PendingAppendedHistory = appendedHistory is null
                    ? s.PendingAppendedHistory
                    : appendedHistory.Select(entry => entry.Clone()).ToArray(),
            });

    private async Task ScheduleNyxRelayTextOperationTimeoutAsync(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation,
        LlmReplyStreamChunkEvent chunk,
        string? currentPlatformMessageId,
        string? commandId,
        string? finalText,
        string? lastFlushedText,
        int editCount,
        CancellationToken ct)
    {
        await ScheduleSelfDurableTimeoutAsync(
            BuildNyxRelayTextOperationTimeoutCallbackId(correlationId, operation, generation),
            StreamingFailureUpdateTimeout,
            new NyxRelayTextOperationTimeoutFiredEvent
            {
                CorrelationId = correlationId,
                Operation = operation,
                Sequence = sequence,
                OperationGeneration = generation,
                Chunk = CloneNyxRelayTextTimeoutChunkForDurableState(chunk),
                CurrentPlatformMessageId = currentPlatformMessageId ?? string.Empty,
                CommandId = commandId ?? string.Empty,
                FinalText = finalText ?? string.Empty,
                LastFlushedText = lastFlushedText ?? string.Empty,
                EditCount = editCount,
                FiredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private static LlmReplyStreamChunkEvent CloneNyxRelayTextTimeoutChunkForDurableState(
        LlmReplyStreamChunkEvent chunk) =>
        new()
        {
            CorrelationId = chunk.CorrelationId ?? string.Empty,
            RegistrationId = chunk.RegistrationId ?? string.Empty,
            Activity = CloneForDurableState(chunk.Activity) ?? new ChatActivity(),
            AccumulatedText = chunk.AccumulatedText ?? string.Empty,
            ChunkAtUnixMs = chunk.ChunkAtUnixMs,
        };

    private Task StartNyxRelayTextOperationAsync(
        NyxRelayTextOperationKind operation,
        LlmReplyStreamChunkEvent chunk,
        string correlationId,
        string? currentPlatformMessageId,
        string? commandId,
        string? finalText,
        string? lastFlushedText,
        int editCount,
        long sequence,
        long generation)
    {
        var renderer = ResolveNyxRelayTextReplyStreamRenderer();
        var step = renderer.CreateStep(
            new NyxRelayTextOperationStepInput(
                operation,
                chunk,
                correlationId,
                currentPlatformMessageId,
                commandId,
                finalText,
                lastFlushedText,
                editCount,
                sequence,
                generation));
        return PublishReplyOperationStepAsync(step, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleReplyOperationStepAsync(ReplyOperationStepEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.PayloadCase != ReplyOperationStepEvent.PayloadOneofCase.NyxRelayText)
            return;
        if (!string.Equals(NormalizeOptional(evt.CorrelationId), evt.CorrelationId, StringComparison.Ordinal))
            return;

        var renderer = ResolveReplyOperationStepRenderers().FirstOrDefault(candidate => candidate.CanHandle(evt));
        if (renderer is null)
        {
            Logger.LogDebug(
                "Ignoring reply operation step without a matching renderer. operationId={OperationId}",
                evt.OperationId);
            return;
        }

        await renderer.ExecuteAsync(this, evt, CancellationToken.None);
    }

    bool IReplyOperationActorContext.MatchesNyxRelayTextInFlight(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation)
    {
        var state = GetOrInitNyxRelayStreamingState(correlationId);
        return MatchesNyxRelayTextInFlight(state, operation, sequence, generation);
    }

    bool IReplyOperationActorContext.MatchesLarkCardInFlight(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId) =>
        false;

    private static ConversationStreamChunkResult ToStreamChunkResult(NyxRelayTextOperationCompletedEvent evt)
    {
        var raw = evt.RawResult ?? new NyxRelayTextOperationRawResult();
        if (evt.State == NyxRelayTextOperationResultState.Succeeded)
            return ConversationStreamChunkResult.Succeeded(raw.PlatformMessageId);

        return ConversationStreamChunkResult.Failed(
            evt.State == NyxRelayTextOperationResultState.Faulted
                ? BuildNyxRelayTextFaultErrorCode(raw)
                : raw.RawErrorCode,
            evt.State == NyxRelayTextOperationResultState.Faulted
                ? raw.ExceptionMessage
                : raw.RawErrorSummary,
            raw.EditUnsupported,
            raw.FailureKind,
            raw.RetryAfterMs > 0 ? TimeSpan.FromMilliseconds(raw.RetryAfterMs) : null,
            raw.HttpStatus,
            raw.RawErrorKey,
            raw.RawErrorCodeValue);
    }

    private static string BuildNyxRelayTextFaultErrorCode(NyxRelayTextOperationRawResult raw)
    {
        var exceptionType = string.IsNullOrWhiteSpace(raw.ExceptionType)
            ? "Exception"
            : raw.ExceptionType;
        return $"relay_text_threw:{exceptionType}";
    }

    // Text-edit streaming is the fallback path behind Lark CardKit. Its Task.Run
    // executor also reports completion through a self-dispatched Direct envelope,
    // so the handler must opt in to self handling or the fallback cannot progress.
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayTextOperationCompletedAsync(NyxRelayTextOperationCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitNyxRelayStreamingState(correlationId);
        if (!MatchesNyxRelayTextInFlight(state, evt.Operation, evt.Sequence, evt.OperationGeneration))
            return;

        switch (evt.Operation)
        {
            case NyxRelayTextOperationKind.Interim:
                await HandleNyxRelayTextInterimCompletionAsync(correlationId, state, evt);
                return;
            case NyxRelayTextOperationKind.FailureSelfHeal:
                await HandleNyxRelayTextFailureSelfHealCompletionAsync(correlationId, state, evt);
                return;
            case NyxRelayTextOperationKind.Final:
                await HandleNyxRelayTextFinalCompletionAsync(correlationId, state, evt);
                return;
            default:
                return;
        }
    }
    private async Task HandleNyxRelayTextInterimCompletionAsync(
        string correlationId,
        NyxRelayStreamingState state,
        NyxRelayTextOperationCompletedEvent evt)
    {
        var result = ToStreamChunkResult(evt);
        if (!result.Success)
        {
            if (ShouldRetryNyxRelayInterimUpdate(result, state))
            {
                var retryAttempt = state.RetryAttempt + 1;
                var retryGeneration = NextNyxRelayTextOperationGeneration(state);
                await TransitionNyxRelayStreamingPhaseAsync(
                    correlationId,
                    state,
                    state.Phase,
                    fieldUpdate: s => s with
                    {
                        InFlight = new NyxRelayTextOperationInFlight(
                            NyxRelayTextOperationKind.Interim,
                            evt.Sequence,
                            retryGeneration),
                        OperationGeneration = retryGeneration,
                        RetryAttempt = retryAttempt,
                    });
                await StartNyxRelayTextOperationAsync(
                    NyxRelayTextOperationKind.Interim,
                    evt.Chunk?.Clone() ?? new LlmReplyStreamChunkEvent(),
                    correlationId,
                    NormalizeOptional(evt.CurrentPlatformMessageId) ?? state.PlatformMessageId,
                    commandId: string.Empty,
                    finalText: string.Empty,
                    lastFlushedText: state.LastFlushedText,
                    editCount: state.EditCount,
                    evt.Sequence,
                    retryGeneration);
                return;
            }

            if (state.AllowsFinalEdit)
            {
                Logger.LogInformation(
                    "Streaming interim edit failed after token consumed; suppressing interim edits, final edit will still be attempted. correlation={CorrelationId}, code={Code}, editUnsupported={EditUnsupported}",
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.EditUnsupported);
                await TransitionNyxRelayStreamingPhaseAsync(
                    correlationId,
                    state,
                    NyxRelayStreamingPhase.SuppressingInterim,
                    terminalReason: $"interim_edit_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
            }
            else
            {
                Logger.LogInformation(
                    "Streaming initial send failed before token consumed; disabling streaming and allowing /reply fallback. correlation={CorrelationId}, code={Code}, editUnsupported={EditUnsupported}",
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.EditUnsupported);
                await TransitionNyxRelayStreamingPhaseAsync(
                    correlationId,
                    state,
                    NyxRelayStreamingPhase.DisabledPreSend,
                    terminalReason: $"first_send_failed:{result.ErrorCode}",
                    fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
            }
            return;
        }

        var isFirstChunk = state.Phase == NyxRelayStreamingPhase.Idle;
        var newPlatformMessageId = string.IsNullOrWhiteSpace(result.PlatformMessageId)
            ? state.PlatformMessageId
            : result.PlatformMessageId;
        var ackedText = evt.Chunk?.AccumulatedText ?? state.PendingAccumulatedText ?? state.LastFlushedText;
        var pendingText = string.Equals(state.PendingAccumulatedText, ackedText, StringComparison.Ordinal)
            ? null
            : state.PendingAccumulatedText;
        var updated = await TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            isFirstChunk ? NyxRelayStreamingPhase.PlaceholderSent : NyxRelayStreamingPhase.Streaming,
            fieldUpdate: s => s with
            {
                PlatformMessageId = newPlatformMessageId,
                LastFlushedText = ackedText,
                EditCount = isFirstChunk ? 0 : s.EditCount + 1,
                InFlight = null,
                PendingAccumulatedText = pendingText,
                RetryAttempt = 0,
            });
        await ContinueNyxRelayTextCoalescedWorkAsync(correlationId, updated, evt.Chunk);
    }

    private static bool ShouldRetryNyxRelayInterimUpdate(
        ConversationStreamChunkResult result,
        NyxRelayStreamingState state) =>
        state.AllowsFinalEdit &&
        result.FailureKind == FailureKind.TransientAdapterError &&
        (result.RetryAfter is null || result.RetryAfter <= TimeSpan.Zero) &&
        state.RetryAttempt < MaxNyxRelayInterimUpdateRetryCount;

    private async Task HandleNyxRelayTextFailureSelfHealCompletionAsync(
        string correlationId,
        NyxRelayStreamingState state,
        NyxRelayTextOperationCompletedEvent evt)
    {
        var result = ToStreamChunkResult(evt);
        var platformMessageId = NormalizeOptional(evt.CurrentPlatformMessageId) ?? state.PlatformMessageId ?? string.Empty;
        var commandId = NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId);
        var failureText = state.PendingFinalizeText ?? evt.FinalText ?? evt.Chunk?.AccumulatedText ?? string.Empty;
        if (result.Success)
        {
            Logger.LogWarning(
                "LLM reply failed after streaming-start; updated placeholder with failure text. correlation={CorrelationId}, platformMessageId={PlatformMessageId}",
                evt.CorrelationId,
                platformMessageId);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                NyxRelayStreamingPhase.TerminalSucceeded,
                terminalReason: "failed_self_heal",
                fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
            await PersistStreamedCompletionAsync(evt, commandId, platformMessageId, failureText, state.EditCount + 1);
            return;
        }

        Logger.LogWarning(
            "Streaming LLM failure-update could not edit placeholder; persisting last flushed partial as terminal. correlation={CorrelationId}, code={Code}, platformMessageId={PlatformMessageId}",
            evt.CorrelationId,
            result.ErrorCode,
            platformMessageId);
        await TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            NyxRelayStreamingPhase.TerminalPartial,
            terminalReason: $"failed_self_heal_edit_failed:{result.ErrorCode}",
            fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
        await PersistStreamedCompletionAsync(evt, commandId, platformMessageId, state.LastFlushedText, state.EditCount);
    }

    private async Task HandleNyxRelayTextFinalCompletionAsync(
        string correlationId,
        NyxRelayStreamingState state,
        NyxRelayTextOperationCompletedEvent evt)
    {
        var result = ToStreamChunkResult(evt);
        var platformMessageId = NormalizeOptional(evt.CurrentPlatformMessageId) ?? state.PlatformMessageId ?? string.Empty;
        var commandId = NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId);
        var finalText = state.PendingFinalizeText ?? evt.FinalText ?? evt.Chunk?.AccumulatedText ?? string.Empty;
        if (!result.Success)
        {
            Logger.LogWarning(
                "Streaming final flush failed after token consumed; persisting last flushed partial as terminal. correlation={CorrelationId}, code={Code}, platformMessageId={PlatformMessageId}",
                evt.CorrelationId,
                result.ErrorCode,
                platformMessageId);
            await TransitionNyxRelayStreamingPhaseAsync(
                correlationId,
                state,
                NyxRelayStreamingPhase.TerminalPartial,
                terminalReason: $"final_edit_failed:{result.ErrorCode}",
                fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
            await PersistStreamedCompletionAsync(evt, commandId, platformMessageId, state.LastFlushedText, state.EditCount);
            return;
        }

        await TransitionNyxRelayStreamingPhaseAsync(
            correlationId,
            state,
            NyxRelayStreamingPhase.TerminalSucceeded,
            terminalReason: "completed",
            fieldUpdate: s => s with
            {
                LastFlushedText = finalText,
                EditCount = state.EditCount + 1,
                InFlight = null,
                RetryAttempt = 0,
            });
        await PersistStreamedCompletionAsync(evt, commandId, platformMessageId, finalText, state.EditCount + 1);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayTextOperationTimeoutFiredAsync(NyxRelayTextOperationTimeoutFiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return;

        var state = GetOrInitNyxRelayStreamingState(correlationId);
        if (!MatchesNyxRelayTextInFlight(state, evt.Operation, evt.Sequence, evt.OperationGeneration))
            return;

        switch (evt.Operation)
        {
            case NyxRelayTextOperationKind.Interim:
                if (state.AllowsFinalEdit)
                {
                    await TransitionNyxRelayStreamingPhaseAsync(
                        correlationId,
                        state,
                        NyxRelayStreamingPhase.SuppressingInterim,
                        terminalReason: "interim_edit_timeout",
                        fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
                }
                else
                {
                    await TransitionNyxRelayStreamingPhaseAsync(
                        correlationId,
                        state,
                        NyxRelayStreamingPhase.DisabledPreSend,
                        terminalReason: "first_send_timeout",
                        fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
                }
                return;
            case NyxRelayTextOperationKind.FailureSelfHeal:
                await TransitionNyxRelayStreamingPhaseAsync(
                    correlationId,
                    state,
                    NyxRelayStreamingPhase.TerminalPartial,
                    terminalReason: "failed_self_heal_timeout",
                    fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
                await PersistStreamedCompletionAsync(
                    evt,
                    NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId),
                    NormalizeOptional(evt.CurrentPlatformMessageId) ?? state.PlatformMessageId ?? string.Empty,
                    state.LastFlushedText,
                    state.EditCount);
                return;
            case NyxRelayTextOperationKind.Final:
                await TransitionNyxRelayStreamingPhaseAsync(
                    correlationId,
                    state,
                    NyxRelayStreamingPhase.TerminalPartial,
                    terminalReason: "final_edit_timeout",
                    fieldUpdate: s => s with { InFlight = null, RetryAttempt = 0 });
                await PersistStreamedCompletionAsync(
                    evt,
                    NormalizeOptional(evt.CommandId) ?? state.PendingFinalizeCommandId ?? BuildLlmReplyCommandId(correlationId),
                    NormalizeOptional(evt.CurrentPlatformMessageId) ?? state.PlatformMessageId ?? string.Empty,
                    state.LastFlushedText,
                    state.EditCount);
                return;
        }
    }

    private async Task ContinueNyxRelayTextCoalescedWorkAsync(
        string correlationId,
        NyxRelayStreamingState state,
        LlmReplyStreamChunkEvent? sourceChunk)
    {
        if (state.InFlight is not null || IsTerminalNyxRelayStreamingPhase(state.Phase))
            return;

        if (state.PendingFinalizeText is not null)
        {
            var commandId = state.PendingFinalizeCommandId ?? ResolvePendingReplyCommandId(correlationId);
            var ready = new LlmReplyReadyEvent
            {
                CorrelationId = correlationId,
                RunId = ResolvePendingReplyRunId(correlationId) ?? string.Empty,
                RegistrationId = sourceChunk?.RegistrationId ?? string.Empty,
                Activity = sourceChunk?.Activity?.Clone() ?? new ChatActivity(),
                Outbound = new MessageContent { Text = state.PendingFinalizeText },
                TerminalState = state.PendingTerminalState == LlmReplyTerminalState.Unspecified
                    ? LlmReplyTerminalState.Completed
                    : state.PendingTerminalState,
                ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            ready.AppendedHistory.AddRange(state.PendingAppendedHistory.Select(entry => entry.Clone()));
            var runtimeContext = BuildNyxRelayRuntimeContext(
                correlationId,
                ready.Activity,
                sourceChunk?.ReplyToken,
                sourceChunk?.ReplyTokenExpiresAtUnixMs ?? 0);
            await TryCompleteStreamedReplyAsync(ready, commandId, ready.Activity, runtimeContext);
            return;
        }

        if (state.PendingAccumulatedText is null || sourceChunk is null)
            return;

        var chunk = sourceChunk.Clone();
        chunk.AccumulatedText = state.PendingAccumulatedText;
        await HandleNyxRelayStreamingChunkCoreAsync(chunk);
    }

    private async Task PersistStreamedCompletionAsync(
        NyxRelayTextOperationCompletedEvent evt,
        string commandId,
        string platformMessageId,
        string outboundText,
        int edits) =>
        await PersistStreamedCompletionAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = evt.CorrelationId,
                RunId = ResolvePendingReplyRunId(evt.CorrelationId) ?? string.Empty,
                RegistrationId = evt.Chunk?.RegistrationId ?? string.Empty,
                Activity = evt.Chunk?.Activity?.Clone() ?? new ChatActivity(),
                Outbound = new MessageContent { Text = outboundText },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            commandId,
            evt.Chunk?.Activity,
            platformMessageId,
            outboundText,
            edits);

    private async Task PersistStreamedCompletionAsync(
        NyxRelayTextOperationTimeoutFiredEvent evt,
        string commandId,
        string platformMessageId,
        string outboundText,
        int edits) =>
        await PersistStreamedCompletionAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = evt.CorrelationId,
                RunId = ResolvePendingReplyRunId(evt.CorrelationId) ?? string.Empty,
                RegistrationId = evt.Chunk?.RegistrationId ?? string.Empty,
                Activity = evt.Chunk?.Activity?.Clone() ?? new ChatActivity(),
                Outbound = new MessageContent { Text = outboundText },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            commandId,
            evt.Chunk?.Activity,
            platformMessageId,
            outboundText,
            edits);

    private async Task PersistStreamedCompletionAsync(
        LlmReplyReadyEvent evt,
        string commandId,
        ChatActivity? referenceActivity,
        string platformMessageId,
        string outboundText,
        int edits)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = string.Empty,
            CausationCommandId = commandId,
            SentActivityId = $"nyx-relay-stream:{platformMessageId}",
            AuthPrincipal = "bot",
            Conversation = evt.Activity?.Conversation?.Clone()
                           ?? State.Conversation?.Clone()
                           ?? new ConversationReference(),
            Outbound = new MessageContent { Text = outboundText },
            CompletedAtUnixMs = nowMs,
            OutboundDelivery = ToOutboundDeliveryReceipt(evt.Activity?.OutboundDelivery),
        };
        completed.AppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
        // ADR-0021 chain.delivered observable: the streaming path always reaches this
        // function with a user-visible placeholder message id (any partial / full /
        // failure-self-heal text the user actually saw). Persist a Delivered event
        // BEFORE the turn-completed summary so "events.Last() is turn-completed"
        // consumers keep working.
        var delivered = new LlmReplyDeliveredEvent
        {
            CorrelationId = evt.CorrelationId ?? string.Empty,
            RunId = evt.RunId ?? string.Empty,
            AckedAtUnixMs = nowMs,
            ChannelMessageId = $"nyx-relay-stream:{platformMessageId}",
        };
        var deliveryProduced = BuildDeliveryProducedEvent(
            DeliveryKind.TextMessage,
            DeliveryStatus.Succeeded,
            referenceActivity ?? evt.Activity,
            evt.RunId,
            evt.CorrelationId,
            commandId,
            sourceEventId: evt.CorrelationId,
            providerMessageId: delivered.ChannelMessageId,
            cardId: string.Empty,
            conversation: completed.Conversation);
        deliveryProduced.ProducedAtVersion = NextCommittedVersion(1);
        await PersistDomainEventsAsync([delivered, deliveryProduced]);
        if (referenceActivity is not null)
            _ = ObserveReplyDeliveredAsync(ResolveRunner(), referenceActivity);
        await ClearReplyLifecyclesAsync(evt.CorrelationId, referenceActivity, "streamed_completion");
        await PersistDomainEventAsync(completed);
        Logger.LogInformation(
            "Completed streamed LLM reply: correlation={CorrelationId} platformMessageId={PlatformMessageId} edits={EditCount} conversation={Key}",
            evt.CorrelationId,
            platformMessageId,
            edits,
            completed.Conversation?.CanonicalKey);
    }

    /// <summary>
    /// Proactive command path: dedup by command id, optionally reject, otherwise invoke bot turn.
    /// </summary>
    [EventHandler]
    public async Task HandleContinueCommandAsync(ConversationContinueRequestedEvent cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (string.IsNullOrWhiteSpace(cmd.CommandId))
        {
            await EmitRejectAsync(cmd, RejectReason.Unspecified, "empty command_id");
            return;
        }

        if (State.ProcessedCommandIds.Contains(cmd.CommandId))
        {
            Logger.LogInformation(
                "Duplicate continue command {CommandId}; emitting DuplicateCommand rejection",
                cmd.CommandId);
            await EmitRejectAsync(cmd, RejectReason.DuplicateCommand, "duplicate command id");
            return;
        }

        var runner = ResolveRunner();
        var result = await runner.RunContinueAsync(cmd, CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = string.Empty,
                CausationCommandId = cmd.CommandId,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = string.IsNullOrEmpty(result.AuthPrincipal)
                    ? AuthPrincipalForContinue(cmd)
                    : result.AuthPrincipal,
                Conversation = cmd.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? (cmd.Payload?.Clone() ?? new MessageContent()),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            var deliveryProduced = BuildDeliveryProducedEvent(
                DeliveryKind.TextMessage,
                DeliveryStatus.Succeeded,
                null,
                runId: string.Empty,
                turnId: cmd.CorrelationId,
                requestId: cmd.CommandId,
                sourceEventId: cmd.CausationId,
                providerMessageId: result.OutboundDelivery?.ReplyMessageId,
                cardId: string.Empty,
                conversation: cmd.Conversation);
            await PersistDomainEventsAsync([deliveryProduced, completed]);
            Logger.LogInformation(
                "Completed continue command: cmd={CommandId} sent={SentId} conversation={Key}",
                cmd.CommandId, result.SentActivityId, cmd.Conversation?.CanonicalKey);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = cmd.CommandId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.CausationId,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        await PersistDomainEventAsync(failed);
        Logger.LogWarning(
            "Continue command failed: cmd={CommandId} code={Code} kind={Kind}",
            cmd.CommandId, result.ErrorCode, result.FailureKind);
    }

    // Retry policy is driven by FailureKind, not by whether the caller supplied a backoff.
    // Only PermanentAdapterError terminates the command id; every other kind is retriable and
    // carries the supplied retry_after_ms (0 when omitted). This preserves transient recovery
    // paths even when runners report a transient failure without an explicit backoff.
    private static void AssignRetryPolicy(ConversationContinueFailedEvent failed, ConversationTurnResult result)
    {
        if (result.FailureKind == FailureKind.PermanentAdapterError)
        {
            failed.NotRetryable = new Google.Protobuf.WellKnownTypes.Empty();
            return;
        }

        failed.RetryAfterMs = result.RetryAfter is { } retry
            ? (long)retry.TotalMilliseconds
            : 0;
    }

    private static string AuthPrincipalForContinue(ConversationContinueRequestedEvent cmd) =>
        cmd.Kind == PrincipalKind.OnBehalfOfUser
            ? $"user:{cmd.OnBehalfOfUserId}"
            : "bot";

    private static string BuildLlmReplyCommandId(string? correlationId) =>
        $"llm:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildWorkflowDraftRunCommandId(string? correlationId) =>
        $"workflow-draft-run:{correlationId?.Trim() ?? string.Empty}";

    private INyxRelayTextReplyStreamRenderer ResolveNyxRelayTextReplyStreamRenderer() =>
        Services.GetService<INyxRelayTextReplyStreamRenderer>() ??
        new NyxRelayTextReplyStreamRenderer(
            ResolveRunner(),
            NullLogger<NyxRelayTextReplyStreamRenderer>.Instance);

    private IReadOnlyList<IReplyOperationStepRenderer> ResolveReplyOperationStepRenderers()
    {
        var renderers = Services.GetServices<IReplyOperationStepRenderer>().ToArray();
        if (renderers.Length > 0)
            return renderers.Where(renderer => renderer is INyxRelayTextReplyStreamRenderer).ToArray();

        return [ResolveNyxRelayTextReplyStreamRenderer()];
    }

    private Task PublishReplyOperationStepAsync(ReplyOperationStepEvent step, CancellationToken ct) =>
        SendToAsync(Id, step, ct);

    ConversationTurnRuntimeContext IReplyOperationActorContext.BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity,
        string? replyToken,
        long replyTokenExpiresAtUnixMs) =>
        BuildNyxRelayRuntimeContext(correlationId, activity, replyToken, replyTokenExpiresAtUnixMs);

    void IReplyOperationActorContext.RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext) =>
        RestoreRuntimeTransportCredentials(activity, runtimeContext);

    public async Task DispatchReplyOperationCompletionAsync(
        IMessage evt,
        string correlationId,
        string operationName,
        CancellationToken ct)
    {
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (dispatchPort is null)
        {
            Logger.LogWarning(
                "IActorDispatchPort unavailable; cannot dispatch {OperationName} operation signal. correlation={CorrelationId}",
                operationName,
                correlationId);
            return;
        }

        await dispatchPort.DispatchAsync(
                Id,
                new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(evt),
                    Route = EnvelopeRouteSemantics.CreateDirect(Id, Id),
                    Propagation = new EnvelopePropagation { CorrelationId = correlationId },
                },
                ct)
            .ConfigureAwait(false);
    }

    // ADR-0021 §6 / canon §9 — single source of truth for "this LLM reply turn is
    // already finalized". Every reply-ready / dropped / streaming-chunk handler entry
    // uses this so late or duplicate signals uniformly no-op. The dedup key is the
    // `llm:<correlationId>` form appended to ProcessedCommandIds by
    // ApplyTurnCompleted / ApplyContinueFailed when the turn reaches chain.finalized.
    private bool IsLlmReplyTurnFinalized(string? correlationId) =>
        IsReplyTurnFinalized(correlationId);

    private bool IsReplyTurnFinalized(string? correlationId) =>
        State.ProcessedCommandIds.Contains(BuildLlmReplyCommandId(correlationId)) ||
        State.ProcessedCommandIds.Contains(BuildWorkflowDraftRunCommandId(correlationId));

    private string ResolvePendingReplyCommandId(string? correlationId) =>
        FindPendingWorkflowDraftRunRequest(correlationId) is not null
            ? BuildWorkflowDraftRunCommandId(correlationId)
            : BuildLlmReplyCommandId(correlationId);

    private static string BuildDeferredLlmReplyCallbackId(string? correlationId) =>
        $"conversation-llm-dispatch:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildDeferredInboundTurnRetryCallbackId(string? activityId) =>
        $"conversation-inbound-turn-retry:{activityId?.Trim() ?? string.Empty}";

    private async Task ScheduleDeferredLlmReplyDispatchAsync(
        NeedsLlmReplyEvent request,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        await ScheduleSelfDurableTimeoutAsync(
            BuildDeferredLlmReplyCallbackId(request.CorrelationId),
            dueTime <= TimeSpan.Zero ? DeferredLlmDispatchRetryDelay : dueTime,
            new DeferredLlmReplyDispatchRequestedEvent
            {
                CorrelationId = request.CorrelationId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task ScheduleDeferredInboundTurnRetryAsync(
        string activityId,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        await ScheduleSelfDurableTimeoutAsync(
            BuildDeferredInboundTurnRetryCallbackId(activityId),
            dueTime <= TimeSpan.Zero ? DeferredInboundTurnRetryDelay : dueTime,
            new DeferredInboundTurnRetryRequestedEvent
            {
                ActivityId = activityId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task SchedulePendingInboundTurnRetriesAsync(CancellationToken ct)
    {
        // Snapshot to avoid enumerating the live repeated field while downstream scheduling
        // may trigger state mutations (the same invariant SchedulePendingLlmReplyDispatchesAsync
        // already relies on).
        var pending = State.PendingInboundTurns.ToArray();
        if (pending.Length == 0)
            return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entry in pending)
        {
            if (string.IsNullOrWhiteSpace(entry.ActivityId))
                continue;

            var remainingMs = entry.NextRetryUnixMs > 0
                ? entry.NextRetryUnixMs - nowMs
                : 0;
            var delay = remainingMs > 0
                ? TimeSpan.FromMilliseconds(remainingMs)
                : DeferredInboundTurnRetryDelay;
            if (delay < DeferredInboundTurnRetryDelay)
                delay = DeferredInboundTurnRetryDelay;

            await ScheduleDeferredInboundTurnRetryAsync(entry.ActivityId, delay, ct);
        }
    }

    // Refactor (iter17/cluster-038): Old pattern: relay callback continuation lived behind process-local replay/idempotency guards. New principle: pending callback admissions are actor-owned state and are re-dispatched through the actor inbox after activation.
    private async Task DispatchPendingRelayAdmissionTurnsAsync(CancellationToken ct)
    {
        var pending = State.PendingRelayAdmissions.ToArray();
        foreach (var admission in pending)
        {
            if (string.IsNullOrWhiteSpace(admission.ActivityId) ||
                string.IsNullOrWhiteSpace(admission.RelayApiKeyId) ||
                string.IsNullOrWhiteSpace(admission.CallbackJti))
            {
                continue;
            }

            await SendToAsync(
                Id,
                new NyxRelayCallbackTurnRequestedEvent
                {
                    ActivityId = admission.ActivityId,
                    RelayApiKeyId = admission.RelayApiKeyId,
                    CallbackJti = admission.CallbackJti,
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                ct);
        }
    }

    private async Task SchedulePendingLlmReplyDispatchesAsync(CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxAgeMs = (long)PendingLlmReplyRequestMaxAge.TotalMilliseconds;

        // Snapshot: PersistDomainEventAsync below mutates State.PendingLlmReplyRequests
        // via the state matcher, which would invalidate the iterator if we walked the
        // live collection.
        var pending = State.PendingLlmReplyRequests.ToArray();
        foreach (var request in pending)
        {
            var ageMs = request.RequestedAtUnixMs > 0 ? nowMs - request.RequestedAtUnixMs : 0;
            if (request.RequestedAtUnixMs > 0 && ageMs > maxAgeMs)
            {
                Logger.LogInformation(
                    "Dropping stale pending LLM reply request on rehydration: correlation={CorrelationId} ageMs={AgeMs}",
                    request.CorrelationId,
                    ageMs);
                var failed = new ConversationContinueFailedEvent
                {
                    CommandId = BuildLlmReplyCommandId(request.CorrelationId),
                    CorrelationId = request.CorrelationId,
                    CausationId = string.Empty,
                    Kind = FailureKind.PermanentAdapterError,
                    ErrorCode = "stale_pending_request_dropped",
                    ErrorSummary = "Pending LLM reply request exceeded max age and was dropped on actor rehydration.",
                    NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
                    FailedAtUnixMs = nowMs,
                };
                await PersistDomainEventAsync(failed);
                continue;
            }

            await DispatchPendingLlmReplyAsync(request, ct);
        }
    }

    private async Task DispatchPendingWorkflowDraftRunsAsync(CancellationToken ct)
    {
        var pending = State.PendingWorkflowDraftRunRequests.ToArray();
        foreach (var request in pending)
            await DispatchPendingWorkflowDraftRunAsync(request, ct);
    }

    private Task EmitRejectAsync(ConversationContinueRequestedEvent cmd, RejectReason reason, string detail)
    {
        var rejected = new ConversationContinueRejectedEvent
        {
            CommandId = cmd.CommandId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.CausationId,
            Reason = reason,
            ReasonDetail = detail,
            RejectedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return PersistDomainEventAsync(rejected);
    }

    private IConversationTurnRunner ResolveRunner() =>
        Services.GetService<IConversationTurnRunner>() ?? new NullConversationTurnRunner();

    // The post-delivery hook (e.g. clearing the Lark typing reaction) makes a best-effort
    // external call, so it deliberately runs off the turn-completion path. Route it through
    // here so a failure is observed and logged instead of vanishing into a discarded Task.
    // The runner is resolved on the turn by the caller and passed in; only the external
    // call runs detached, and nothing here touches grain state off-turn.
    private async Task ObserveReplyDeliveredAsync(IConversationTurnRunner runner, ChatActivity activity)
    {
        try
        {
            await runner.OnReplyDeliveredAsync(activity, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Post-delivery hook OnReplyDeliveredAsync failed.");
        }
    }

    private ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity,
        string? replyToken = null,
        long replyTokenExpiresAtUnixMs = 0,
        string? nyxUserAccessToken = null)
    {
        var normalizedCorrelationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                      NormalizeOptional(correlationId);
        var normalizedReplyToken = NormalizeOptional(replyToken);
        var replyMessageId = NormalizeOptional(activity?.OutboundDelivery?.ReplyMessageId);
        var accessToken = NormalizeOptional(nyxUserAccessToken) ??
                          NormalizeOptional(activity?.TransportExtras?.NyxUserAccessToken);
        if (normalizedCorrelationId is null || normalizedReplyToken is null || replyMessageId is null)
            return new ConversationTurnRuntimeContext(null, accessToken);

        var expiresAt = replyTokenExpiresAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(replyTokenExpiresAtUnixMs)
            : DateTimeOffset.UtcNow.AddMinutes(30);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return new ConversationTurnRuntimeContext(null, accessToken);

        return new ConversationTurnRuntimeContext(
            new NyxRelayReplyTokenContext(
                normalizedCorrelationId,
                normalizedReplyToken,
                replyMessageId,
                expiresAt,
                accessToken),
            accessToken);
    }

    private async Task<ConversationTurnRuntimeContext> BuildNyxRelayRuntimeContextForReplyAsync(
        LlmReplyReadyEvent evt,
        ChatActivity? pendingActivity,
        CancellationToken ct)
    {
        var activity = pendingActivity ?? evt.Activity;
        var replyToken = NormalizeOptional(evt.ReplyToken);
        var replyTokenExpiresAtUnixMs = evt.ReplyTokenExpiresAtUnixMs;
        var userAccessToken = NormalizeOptional(evt.Activity?.TransportExtras?.NyxUserAccessToken);

        if (Services.GetService<IRuntimeSecretStore>() is { } secretStore)
        {
            if (replyToken is null && evt.RelayReplyTokenRef is { Ref.Length: > 0 } replyTokenRef)
            {
                var resolved = await secretStore.ResolveAsync(
                    new ResolveRuntimeSecretRequest(
                        replyTokenRef.Ref,
                        RelayReplyTokenSecretPurpose,
                        evt.RunId,
                        evt.CorrelationId,
                        "Resolve durable terminal outbox reply credential."),
                    ct);
                replyToken = NormalizeOptional(resolved.Secret);
                replyTokenExpiresAtUnixMs = replyTokenRef.ExpiresAtUnixMs;
            }

            if (userAccessToken is null &&
                evt.RelayUserAccessTokenRef is { Ref.Length: > 0 } userAccessTokenRef)
            {
                var resolved = await secretStore.ResolveAsync(
                    new ResolveRuntimeSecretRequest(
                        userAccessTokenRef.Ref,
                        RelayUserAccessTokenSecretPurpose,
                        evt.RunId,
                        evt.CorrelationId,
                        "Resolve durable terminal outbox user credential."),
                    ct);
                userAccessToken = NormalizeOptional(resolved.Secret);
            }
        }

        return BuildNyxRelayRuntimeContext(
            evt.CorrelationId,
            activity,
            replyToken,
            replyTokenExpiresAtUnixMs,
            userAccessToken);
    }

    private DeliveryProducedEvent BuildDeliveryProducedEvent(
        DeliveryKind kind,
        DeliveryStatus status,
        ChatActivity? activity,
        string? runId,
        string? turnId,
        string? requestId,
        string? sourceEventId,
        string? providerMessageId,
        string? cardId,
        ConversationReference? conversation = null)
    {
        var resolvedConversation = conversation ?? activity?.Conversation ?? State.Conversation;
        return new DeliveryProducedEvent
        {
            RunId = NormalizeOptional(runId) ?? string.Empty,
            TurnId = NormalizeOptional(turnId) ?? string.Empty,
            DeliveryKind = kind,
            Target = BuildDeliveryTarget(activity, resolvedConversation),
            Status = status,
            ProviderMessageId = NormalizeOptional(providerMessageId) ?? string.Empty,
            CardId = NormalizeOptional(cardId) ?? string.Empty,
            RequestId = NormalizeOptional(requestId) ?? string.Empty,
            SourceEventId = NormalizeOptional(sourceEventId) ?? string.Empty,
            ProducedAtVersion = NextCommittedVersion(),
        };
    }

    private static DeliveryTarget BuildDeliveryTarget(
        ChatActivity? activity,
        ConversationReference? conversation)
    {
        var extras = activity?.TransportExtras;
        var outbound = activity?.OutboundDelivery;
        return new DeliveryTarget
        {
            Channel = conversation?.Channel?.Clone() ?? activity?.ChannelId?.Clone() ?? new ChannelId(),
            ConversationKey = conversation?.CanonicalKey ?? string.Empty,
            Platform = NormalizeOptional(extras?.NyxPlatform) ?? conversation?.Channel?.Value ?? activity?.ChannelId?.Value ?? string.Empty,
            AddressId = NormalizeOptional(extras?.NyxLarkChatId) ??
                        NormalizeOptional(extras?.NyxLarkUnionId) ??
                        NormalizeOptional(outbound?.ReplyMessageId) ??
                        string.Empty,
            AddressType = ResolveAddressType(extras),
            ConversationId = NormalizeOptional(extras?.NyxConversationId) ?? conversation?.CanonicalKey ?? string.Empty,
            ReplyMessageId = outbound?.ReplyMessageId ?? string.Empty,
        };
    }

    private static string ResolveAddressType(TransportExtras? extras)
    {
        if (!string.IsNullOrWhiteSpace(extras?.NyxLarkChatId))
            return "chat_id";
        if (!string.IsNullOrWhiteSpace(extras?.NyxLarkUnionId))
            return "union_id";
        return string.Empty;
    }

    // Refactor (iter17/cluster-038): Old pattern: transient relay credentials could ride inside persisted ChatActivity clones. New principle: durable admission/retry/LLM state stores only non-secret relay facts; same-activation credentials stay in runtime context.
    private static ChatActivity? CloneForDurableState(ChatActivity? activity)
    {
        if (activity is null)
            return null;

        var durable = activity.Clone();
        if (durable.TransportExtras is not null)
            durable.TransportExtras.NyxUserAccessToken = string.Empty;
        return durable;
    }

    private static void RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        string? nyxUserAccessToken)
    {
        if (activity is null || NormalizeOptional(nyxUserAccessToken) is not { } accessToken)
            return;

        activity.TransportExtras ??= new TransportExtras();
        activity.TransportExtras.NyxUserAccessToken = accessToken;
    }

    private static void RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var accessToken = NormalizeOptional(runtimeContext.NyxUserAccessToken);
        if (activity is null || accessToken is null)
            return;

        activity.TransportExtras ??= new TransportExtras();
        activity.TransportExtras.NyxUserAccessToken = accessToken;
    }

    private async Task AttachRelayRuntimeSecretReferencesAsync(
        NeedsLlmReplyEvent request,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (!IsRelayActivity(request.Activity) ||
            runtimeContext.NyxRelayReplyToken is not { } replyContext ||
            Services.GetService<IRuntimeSecretStore>() is not { } secretStore)
        {
            return;
        }

        var timeToLive = replyContext.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (timeToLive <= TimeSpan.Zero)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(request.RelayReplyTokenRef?.Ref) &&
                NormalizeOptional(replyContext.ReplyToken) is { } replyToken)
            {
                request.RelayReplyTokenRef = (await secretStore.PutAsync(
                    new StoreRuntimeSecretRequest(
                        RelayReplyTokenSecretPurpose,
                        request.RunId,
                        request.CorrelationId,
                        replyToken,
                        timeToLive,
                        ConsumeOnce: false,
                        AuditReason: "Preserve relay reply credential for actor-dispatch recovery."),
                    ct)).Reference;
            }

            if (string.IsNullOrWhiteSpace(request.RelayUserAccessTokenRef?.Ref) &&
                NormalizeOptional(runtimeContext.NyxUserAccessToken) is { } userAccessToken)
            {
                request.RelayUserAccessTokenRef = (await secretStore.PutAsync(
                    new StoreRuntimeSecretRequest(
                        RelayUserAccessTokenSecretPurpose,
                        request.RunId,
                        request.CorrelationId,
                        userAccessToken,
                        timeToLive,
                        ConsumeOnce: false,
                        AuditReason: "Preserve relay user credential for actor-dispatch recovery."),
                    ct)).Reference;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The same-turn dispatch still carries both raw credentials. A secret-store
            // outage must not turn an otherwise healthy inbound turn into a failure.
            Logger.LogWarning(
                ex,
                "Failed to preserve relay runtime credentials for dispatch recovery: runId={RunId} correlation={CorrelationId}",
                request.RunId,
                request.CorrelationId);
        }
    }

    private async Task AttachWorkflowRuntimeSecretReferencesAsync(
        NeedsWorkflowDraftRunEvent request,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (!IsRelayActivity(request.Activity) ||
            runtimeContext.NyxRelayReplyToken is not { } replyContext ||
            Services.GetService<IRuntimeSecretStore>() is not { } secretStore)
        {
            return;
        }

        var timeToLive = replyContext.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (timeToLive <= TimeSpan.Zero)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(request.RelayReplyTokenRef?.Ref) &&
                NormalizeOptional(replyContext.ReplyToken) is { } replyToken)
            {
                request.RelayReplyTokenRef = (await secretStore.PutAsync(
                    new StoreRuntimeSecretRequest(
                        RelayReplyTokenSecretPurpose,
                        request.RunId,
                        request.CorrelationId,
                        replyToken,
                        timeToLive,
                        ConsumeOnce: false,
                        AuditReason: "Preserve workflow draft-run reply credential for durable recovery."),
                    ct)).Reference;
            }

            if (string.IsNullOrWhiteSpace(request.RelayUserAccessTokenRef?.Ref) &&
                NormalizeOptional(runtimeContext.NyxUserAccessToken) is { } userAccessToken)
            {
                request.RelayUserAccessTokenRef = (await secretStore.PutAsync(
                    new StoreRuntimeSecretRequest(
                        RelayUserAccessTokenSecretPurpose,
                        request.RunId,
                        request.CorrelationId,
                        userAccessToken,
                        timeToLive,
                        ConsumeOnce: false,
                        AuditReason: "Preserve workflow draft-run user credential for durable recovery."),
                    ct)).Reference;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to preserve workflow draft-run runtime credentials: runId={RunId} correlation={CorrelationId}",
                request.RunId,
                request.CorrelationId);
        }
    }

    private async Task RestoreRelayRuntimeCredentialsAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        if (!IsRelayActivity(request.Activity) ||
            Services.GetService<IRuntimeSecretStore>() is not { } secretStore)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ReplyToken) &&
            request.RelayReplyTokenRef is { Ref.Length: > 0 } replyTokenRef)
        {
            var resolved = await secretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    replyTokenRef.Ref,
                    RelayReplyTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    "Recover relay reply credential after actor-dispatch failure."),
                ct);
            if (NormalizeOptional(resolved.Secret) is { } replyToken)
            {
                request.ReplyToken = replyToken;
                request.ReplyTokenExpiresAtUnixMs = replyTokenRef.ExpiresAtUnixMs;
            }
        }

        if (NormalizeOptional(request.Activity?.TransportExtras?.NyxUserAccessToken) is null &&
            request.RelayUserAccessTokenRef is { Ref.Length: > 0 } userAccessTokenRef)
        {
            var resolved = await secretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    userAccessTokenRef.Ref,
                    RelayUserAccessTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    "Recover relay user credential after actor-dispatch failure."),
                ct);
            if (NormalizeOptional(resolved.Secret) is { } userAccessToken)
                RestoreRuntimeTransportCredentials(request.Activity, userAccessToken);
        }
    }

    private async Task RestoreWorkflowRuntimeCredentialsAsync(
        NeedsWorkflowDraftRunEvent request,
        CancellationToken ct)
    {
        if (!IsRelayActivity(request.Activity) ||
            Services.GetService<IRuntimeSecretStore>() is not { } secretStore)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ReplyToken) &&
            request.RelayReplyTokenRef is { Ref.Length: > 0 } replyTokenRef)
        {
            var resolved = await secretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    replyTokenRef.Ref,
                    RelayReplyTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    "Recover workflow draft-run reply credential."),
                ct);
            if (NormalizeOptional(resolved.Secret) is { } replyToken)
            {
                request.ReplyToken = replyToken;
                request.ReplyTokenExpiresAtUnixMs = replyTokenRef.ExpiresAtUnixMs;
            }
        }

        if (string.IsNullOrWhiteSpace(request.NyxUserAccessToken) &&
            request.RelayUserAccessTokenRef is { Ref.Length: > 0 } userAccessTokenRef)
        {
            var resolved = await secretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    userAccessTokenRef.Ref,
                    RelayUserAccessTokenSecretPurpose,
                    request.RunId,
                    request.CorrelationId,
                    "Recover workflow draft-run user credential."),
                ct);
            if (NormalizeOptional(resolved.Secret) is { } userAccessToken)
            {
                request.NyxUserAccessToken = userAccessToken;
                RestoreRuntimeTransportCredentials(request.Activity, userAccessToken);
            }
        }
    }

    private string DescribeReplyTokenSource(LlmReplyReadyEvent evt, ConversationTurnRuntimeContext runtimeContext)
    {
        if (runtimeContext.NyxRelayReplyToken is null)
            return "none";
        if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
            return "run-echo";
        return "runtime-message";
    }

    // Refactor (iter17/cluster-038): Old pattern: active callback_jti claims were checked in singleton ConcurrentDictionary guards. New principle: replay admission checks read only ConversationGAgent typed state.
    private bool HasActiveRelayReplayClaim(string relayApiKeyId, string callbackJti, long nowMs) =>
        State.RelayReplayClaims.Any(claim =>
            string.Equals(claim.RelayApiKeyId, relayApiKeyId, StringComparison.Ordinal) &&
            string.Equals(claim.CallbackJti, callbackJti, StringComparison.Ordinal) &&
            (claim.ExpiresAtUnixMs <= 0 || claim.ExpiresAtUnixMs > nowMs));

    // Refactor (iter17/cluster-038): Old pattern: callback work was recovered from process-local callback context. New principle: callback continuation resolves the admitted activity from persisted actor-owned pending admission state.
    private PendingRelayAdmission? FindPendingRelayAdmission(
        string? relayApiKeyId,
        string? callbackJti,
        string? activityId)
    {
        var normalizedRelayApiKeyId = NormalizeOptional(relayApiKeyId);
        var normalizedCallbackJti = NormalizeOptional(callbackJti);
        var normalizedActivityId = NormalizeOptional(activityId);
        if (normalizedRelayApiKeyId is null || normalizedCallbackJti is null || normalizedActivityId is null)
            return null;

        return State.PendingRelayAdmissions.FirstOrDefault(admission =>
            string.Equals(admission.RelayApiKeyId, normalizedRelayApiKeyId, StringComparison.Ordinal) &&
            string.Equals(admission.CallbackJti, normalizedCallbackJti, StringComparison.Ordinal) &&
            string.Equals(admission.ActivityId, normalizedActivityId, StringComparison.Ordinal));
    }

    private static bool IsRelayActivity(ChatActivity? activity) =>
        activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private static void ApplyRuntimeReplyToken(
        NeedsLlmReplyEvent request,
        ConversationTurnRuntimeContext runtimeContext)
    {
        if (runtimeContext.NyxRelayReplyToken is not { } token)
            return;

        request.ReplyToken = token.ReplyToken;
        request.ReplyTokenExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
    }

    private static void ApplyRuntimeReplyToken(
        NeedsWorkflowDraftRunEvent request,
        ConversationTurnRuntimeContext runtimeContext)
    {
        if (runtimeContext.NyxRelayReplyToken is not { } token)
            return;

        request.ReplyToken = token.ReplyToken;
        request.ReplyTokenExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
    }

    private static void ApplyRuntimeReplyToken(
        LlmReplyStreamChunkEvent chunk,
        ConversationTurnRuntimeContext runtimeContext)
    {
        if (runtimeContext.NyxRelayReplyToken is not { } token)
            return;

        chunk.ReplyToken = token.ReplyToken;
        chunk.ReplyTokenExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
    }

    private async Task PersistMissingRuntimeCredentialFailureAsync(
        string commandId,
        string? correlationId,
        string errorCode,
        string errorSummary,
        long failedAtUnixMs)
    {
        var failed = new ConversationContinueFailedEvent
        {
            CommandId = commandId,
            CorrelationId = correlationId ?? string.Empty,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = failedAtUnixMs,
        };
        await PersistDomainEventAsync(failed);
    }

    private static OutboundDeliveryReceipt? ToOutboundDeliveryReceipt(OutboundDeliveryContext? outboundDelivery)
    {
        var replyMessageId = outboundDelivery?.ReplyMessageId;
        return string.IsNullOrWhiteSpace(replyMessageId)
            ? null
            : new OutboundDeliveryReceipt { ReplyMessageId = replyMessageId };
    }

    // ─── State transitions ───

    private static ConversationGAgentState ApplyTurnCompleted(
        ConversationGAgentState current,
        ConversationTurnCompletedEvent evt)
    {
        var next = current.Clone();
        if (!string.IsNullOrEmpty(evt.ProcessedActivityId))
        {
            AppendBounded(next.ProcessedMessageIds, evt.ProcessedActivityId, ProcessedIdsCap);
            // Successful inbound completion supersedes any pending retry entry.
            RemovePendingInboundTurn(next.PendingInboundTurns, evt.ProcessedActivityId);
            RemovePendingRelayAdmission(next.PendingRelayAdmissions, evt.ProcessedActivityId);
        }
        if (!string.IsNullOrEmpty(evt.CausationCommandId))
        {
            AppendBounded(next.ProcessedCommandIds, evt.CausationCommandId, ProcessedIdsCap);
            RemovePendingLlmReplyRequest(next.PendingLlmReplyRequests, ExtractLlmReplyCorrelationId(evt.CausationCommandId));
            RemovePendingWorkflowDraftRunRequest(
                next.PendingWorkflowDraftRunRequests,
                ExtractWorkflowDraftRunCorrelationId(evt.CausationCommandId));
        }
        if (evt.Conversation != null && next.Conversation == null)
        {
            next.Conversation = evt.Conversation.Clone();
        }
        AppendHistoryBounded(next.RetainedHistory, evt.AppendedHistory, RetainedHistoryMessagesCap);
        NormalizeRecentAttachmentActivities(next.RecentAttachmentActivities, evt.CompletedAtUnixMs);
        next.LastUpdatedUnixMs = evt.CompletedAtUnixMs;
        return next;
    }

    // /clear semantics: the retained transcript window and the recent attachment
    // snapshot together form the conversation memory replayed into LLM turns, so
    // both reset; dedup/lifecycle bookkeeping is unrelated and stays untouched.
    private static ConversationGAgentState ApplyRetainedHistoryCleared(
        ConversationGAgentState current,
        ConversationRetainedHistoryClearedEvent evt)
    {
        var next = current.Clone();
        next.RetainedHistory.Clear();
        next.RecentAttachmentActivities.Clear();
        next.LastUpdatedUnixMs = evt.ClearedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyInboundTurnRetryScheduled(
        ConversationGAgentState current,
        InboundTurnRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (string.IsNullOrEmpty(evt.ActivityId))
            return next;

        var pending = new PendingInboundTurn
        {
            ActivityId = evt.ActivityId,
            Activity = evt.Activity?.Clone(),
            RetryCount = evt.RetryCount,
            FirstFailedUnixMs = evt.FirstFailedUnixMs,
            NextRetryUnixMs = evt.NextRetryUnixMs,
        };
        UpsertPendingInboundTurn(next.PendingInboundTurns, pending);
        RemovePendingRelayAdmission(next.PendingRelayAdmissions, evt.ActivityId);
        next.LastUpdatedUnixMs = evt.ScheduledAtUnixMs > 0 ? evt.ScheduledAtUnixMs : evt.NextRetryUnixMs;
        return next;
    }

    // Refactor (iter17/cluster-038): Old pattern: relay replay/idempotency facts were mutated outside the actor. New principle: admission, replay claim, and pending continuation state mutate together through the event-sourced actor state transition.
    private static ConversationGAgentState ApplyNyxRelayCallbackAdmitted(
        ConversationGAgentState current,
        NyxRelayCallbackAdmittedEvent evt)
    {
        var next = current.Clone();
        var nowMs = evt.AdmittedAtUnixMs > 0
            ? evt.AdmittedAtUnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SweepExpiredRelayReplayClaims(next.RelayReplayClaims, nowMs);
        if (!string.IsNullOrWhiteSpace(evt.RelayApiKeyId) && !string.IsNullOrWhiteSpace(evt.CallbackJti))
        {
            UpsertRelayReplayClaim(next.RelayReplayClaims, new RelayReplayClaim
            {
                RelayApiKeyId = evt.RelayApiKeyId,
                CallbackJti = evt.CallbackJti,
                ActivityId = evt.ActivityId,
                ExpiresAtUnixMs = evt.ClaimExpiresAtUnixMs,
            });
            TrimRelayReplayClaims(next.RelayReplayClaims, RelayReplayClaimsCap);
        }

        if (!string.IsNullOrWhiteSpace(evt.ActivityId))
        {
            UpsertPendingRelayAdmission(next.PendingRelayAdmissions, new PendingRelayAdmission
            {
                ActivityId = evt.ActivityId,
                RelayApiKeyId = evt.RelayApiKeyId,
                CallbackJti = evt.CallbackJti,
                Activity = evt.Activity?.Clone(),
                AdmittedAtUnixMs = nowMs,
            });
            TrimPendingRelayAdmissions(next.PendingRelayAdmissions, PendingRelayAdmissionsCap);
        }

        if (evt.Activity?.Conversation != null && next.Conversation == null)
            next.Conversation = evt.Activity.Conversation.Clone();

        next.LastUpdatedUnixMs = nowMs;
        return next;
    }

    private static ConversationGAgentState ApplyLlmReplyRequested(
        ConversationGAgentState current,
        NeedsLlmReplyEvent evt)
    {
        var next = current.Clone();
        var activityId = evt.Activity?.Id;
        if (!string.IsNullOrWhiteSpace(activityId))
        {
            AppendBounded(next.ProcessedMessageIds, activityId, ProcessedIdsCap);
            // Acceptance into the LLM reply pipeline supersedes any pending inbound retry
            // entry for the same activity. Without this reap, a redelivery that takes the
            // LLM path would leave the stale pending entry in state, where it would be
            // re-scheduled on every activation and silently no-op against the dedup guard.
            RemovePendingInboundTurn(next.PendingInboundTurns, activityId);
            RemovePendingRelayAdmission(next.PendingRelayAdmissions, activityId);
        }

        if (evt.Activity?.Conversation != null && next.Conversation == null)
        {
            next.Conversation = evt.Activity.Conversation.Clone();
        }

        UpsertRecentAttachmentActivity(next.RecentAttachmentActivities, evt.Activity, evt.RequestedAtUnixMs);
        UpsertPendingLlmReplyRequest(next.PendingLlmReplyRequests, evt);
        next.LastUpdatedUnixMs = evt.RequestedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyWorkflowDraftRunRequested(
        ConversationGAgentState current,
        NeedsWorkflowDraftRunEvent evt)
    {
        var next = current.Clone();
        var activityId = evt.Activity?.Id;
        if (!string.IsNullOrWhiteSpace(activityId))
        {
            AppendBounded(next.ProcessedMessageIds, activityId, ProcessedIdsCap);
            RemovePendingInboundTurn(next.PendingInboundTurns, activityId);
            RemovePendingRelayAdmission(next.PendingRelayAdmissions, activityId);
        }

        if (evt.Activity?.Conversation != null && next.Conversation == null)
            next.Conversation = evt.Activity.Conversation.Clone();

        UpsertRecentAttachmentActivity(next.RecentAttachmentActivities, evt.Activity, evt.RequestedAtUnixMs);
        UpsertPendingWorkflowDraftRunRequest(next.PendingWorkflowDraftRunRequests, evt);
        next.LastUpdatedUnixMs = evt.RequestedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyContinueRejected(
        ConversationGAgentState current,
        ConversationContinueRejectedEvent evt)
    {
        var next = current.Clone();
        if (evt.Reason == RejectReason.DuplicateCommand && !string.IsNullOrEmpty(evt.CommandId))
        {
            // DuplicateCommand rejection is emitted *because* the command id is already processed.
            // No state change. Fall through and just stamp the timestamp.
        }
        else if (!string.IsNullOrEmpty(evt.CommandId))
        {
            AppendBounded(next.ProcessedCommandIds, evt.CommandId, ProcessedIdsCap);
        }
        next.LastUpdatedUnixMs = evt.RejectedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyContinueFailed(
        ConversationGAgentState current,
        ConversationContinueFailedEvent evt)
    {
        var next = current.Clone();
        // Only terminal failures (NotRetryable oneof) consume the command id. `retry_after_ms`
        // failures must stay retriable — if we appended them here the next redispatch of the same
        // logical command id would come back as DuplicateCommand instead of executing.
        if (!string.IsNullOrEmpty(evt.CommandId)
            && evt.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
        {
            AppendBounded(next.ProcessedCommandIds, evt.CommandId, ProcessedIdsCap);
            RemovePendingLlmReplyRequest(next.PendingLlmReplyRequests, ExtractLlmReplyCorrelationId(evt.CommandId));
            RemovePendingWorkflowDraftRunRequest(
                next.PendingWorkflowDraftRunRequests,
                ExtractWorkflowDraftRunCorrelationId(evt.CommandId));
        }
        // Inbound terminal failures (e.g. retries exhausted) carry an empty CommandId and set
        // CorrelationId to the activity id; reap the matching pending retry entry so the set
        // does not leak.
        if (string.IsNullOrEmpty(evt.CommandId)
            && !string.IsNullOrEmpty(evt.CorrelationId)
            && evt.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
        {
            RemovePendingInboundTurn(next.PendingInboundTurns, evt.CorrelationId);
            RemovePendingRelayAdmission(next.PendingRelayAdmissions, evt.CorrelationId);
        }
        next.LastUpdatedUnixMs = evt.FailedAtUnixMs;
        return next;
    }

    // ADR-0021 chain.delivered observable: user-visible delivery succeeded via the channel sink.
    private static ConversationGAgentState ApplyLastReplyDelivered(
        ConversationGAgentState current,
        LlmReplyDeliveredEvent evt)
    {
        var next = current.Clone();
        next.LastReplyDelivery = new ReplyDeliveryStatus
        {
            RunId = evt.RunId ?? string.Empty,
            Delivered = new ReplyDeliveryStatus.Types.Delivered
            {
                AckedAtUnixMs = evt.AckedAtUnixMs,
                ChannelMessageId = evt.ChannelMessageId ?? string.Empty,
            },
        };
        // Track the bot's own sent message id so a later group reply targeting it counts as
        // addressing the bot. No-op when empty (e.g. non-card delivery paths).
        ConversationBotMessageLedger.RecordBotSentMessageId(next.BotSentPlatformMessageIds, evt.BotPlatformMessageId);
        if (evt.AckedAtUnixMs > 0)
            next.LastUpdatedUnixMs = evt.AckedAtUnixMs;
        return next;
    }

    // ADR-0021 chain.delivered failure observable: channel sink rejected the reply (4xx/5xx/timeout).
    private static ConversationGAgentState ApplyLastReplyDeliveryFailed(
        ConversationGAgentState current,
        LlmReplyDeliveryFailedEvent evt)
    {
        var next = current.Clone();
        next.LastReplyDelivery = new ReplyDeliveryStatus
        {
            RunId = evt.RunId ?? string.Empty,
            Failed = new ReplyDeliveryStatus.Types.DeliveryFailed
            {
                FailedAtUnixMs = evt.FailedAtUnixMs,
                ErrorCode = evt.ErrorCode ?? string.Empty,
                ErrorMessage = evt.ErrorMessage ?? string.Empty,
            },
        };
        if (evt.FailedAtUnixMs > 0)
            next.LastUpdatedUnixMs = evt.FailedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyDeliveryProduced(
        ConversationGAgentState current,
        DeliveryProducedEvent evt)
    {
        var next = current.Clone();
        AppendDelivery(next, evt);
        if (evt.Target?.ConversationKey is { Length: > 0 } && next.Conversation != null)
            next.Conversation.CanonicalKey = evt.Target.ConversationKey;
        return next;
    }

    // Refactor (iter80/cluster-081-channel-reply-lifecycle-event-state-schema):
    //   Old pattern: ConversationReplyLifecycleChangedEvent carried full ConversationReplyLifecycleState
    //   New principle: event describes transition facts; reducer derives current state from event + actor state
    private static ConversationGAgentState ApplyReplyLifecycleChanged(
        ConversationGAgentState current,
        ConversationReplyLifecycleChangedEvent evt)
    {
        var next = current.Clone();

        var normalizedCorrelationId = NormalizeOptional(evt.CorrelationId);
        if (normalizedCorrelationId is null || evt.Mode == ConversationReplyLifecycleMode.Unspecified)
            return next;

        var lifecycle = FindReplyLifecycle(next.ActiveReplyLifecycles, normalizedCorrelationId, evt.Mode)?.Clone() ??
                        new ConversationReplyLifecycleState
                        {
                            CorrelationId = normalizedCorrelationId,
                            Mode = evt.Mode,
                        };
        ApplyReplyLifecycleTransitionFact(lifecycle, evt);
        next.LastUpdatedUnixMs = evt.ChangedAtUnixMs > 0
            ? evt.ChangedAtUnixMs
            : lifecycle.UpdatedAtUnixMs;
        UpsertReplyLifecycle(next.ActiveReplyLifecycles, lifecycle);
        return next;
    }

    private static void ApplyReplyLifecycleTransitionFact(
        ConversationReplyLifecycleState lifecycle,
        ConversationReplyLifecycleChangedEvent evt)
    {
        if (evt.Phase != ConversationReplyLifecyclePhase.Unspecified)
            lifecycle.Phase = evt.Phase;

        if (evt.HasPlatformMessageIdAssigned)
            lifecycle.PlatformMessageId = evt.PlatformMessageIdAssigned ?? string.Empty;
        if (evt.HasFlushedTextDelta)
            lifecycle.LastFlushedText = evt.FlushedTextDelta ?? string.Empty;
        if (evt.HasEditCountDelta)
            lifecycle.EditCount += evt.EditCountDelta;
        if (evt.HasTerminalReason)
            lifecycle.TerminalReason = evt.TerminalReason ?? string.Empty;
        if (evt.HasNyxRelayOperation)
            lifecycle.NyxRelayInFlightOperation = evt.NyxRelayOperation;
        if (evt.HasOperationSequence)
        {
            if (evt.Mode == ConversationReplyLifecycleMode.NyxRelayText)
                lifecycle.NyxRelayInFlightSequence = evt.OperationSequence;
        }

        if (evt.HasOperationGeneration)
        {
            if (evt.Mode == ConversationReplyLifecycleMode.NyxRelayText)
                lifecycle.NyxRelayOperationGeneration = evt.OperationGeneration;
        }

        if (evt.HasQueuedAccumulatedText)
            lifecycle.PendingAccumulatedText = evt.QueuedAccumulatedText ?? string.Empty;
        if (evt.HasFinalizeText)
            lifecycle.PendingFinalizeText = evt.FinalizeText ?? string.Empty;
        if (evt.HasFinalizeCommandId)
            lifecycle.PendingFinalizeCommandId = evt.FinalizeCommandId ?? string.Empty;
        if (evt.HasNyxRelayTerminalState)
            lifecycle.PendingNyxRelayTerminalState = evt.NyxRelayTerminalState;
        if (evt.HasNyxRelayRetryAttempt)
            lifecycle.NyxRelayRetryAttempt = evt.NyxRelayRetryAttempt;
        if (evt.AppendedHistory.Count > 0)
        {
            lifecycle.PendingAppendedHistory.Clear();
            lifecycle.PendingAppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
        }

        if (evt.ChangedAtUnixMs > 0)
            lifecycle.UpdatedAtUnixMs = evt.ChangedAtUnixMs;
    }

    private static ConversationGAgentState ApplyReplyLifecycleCleared(
        ConversationGAgentState current,
        ConversationReplyLifecycleClearedEvent evt)
    {
        var next = current.Clone();
        RemoveReplyLifecycle(next.ActiveReplyLifecycles, evt.CorrelationId, evt.Mode);
        if (evt.ClearedAtUnixMs > 0)
            next.LastUpdatedUnixMs = evt.ClearedAtUnixMs;
        return next;
    }

    private async Task ClearReplyLifecyclesAsync(
        string? correlationId,
        ChatActivity? activity,
        string reason)
    {
        var normalizedCorrelationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                      NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        await ClearReplyLifecycleAsync(normalizedCorrelationId, ConversationReplyLifecycleMode.NyxRelayText, reason);
    }

    // Clears only an existing lifecycle so duplicate cleanup paths do not emit empty clear events.
    private async Task ClearReplyLifecycleAsync(
        string? correlationId,
        ConversationReplyLifecycleMode mode,
        string reason)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        if (FindReplyLifecycle(normalizedCorrelationId, mode) is null)
            return;

        await PersistDomainEventAsync(new ConversationReplyLifecycleClearedEvent
        {
            CorrelationId = normalizedCorrelationId,
            Mode = mode,
            Reason = reason,
            ClearedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private NeedsLlmReplyEvent? FindPendingLlmReplyRequest(string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return null;

        return State.PendingLlmReplyRequests.FirstOrDefault(request =>
            string.Equals(request.CorrelationId, normalizedCorrelationId, StringComparison.Ordinal));
    }

    private string? ResolvePendingLlmReplyRunId(string? correlationId) =>
        NormalizeOptional(FindPendingLlmReplyRequest(correlationId)?.RunId);

    private NeedsWorkflowDraftRunEvent? FindPendingWorkflowDraftRunRequest(string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return null;

        return State.PendingWorkflowDraftRunRequests.FirstOrDefault(request =>
            string.Equals(request.CorrelationId, normalizedCorrelationId, StringComparison.Ordinal));
    }

    private string? ResolvePendingReplyRunId(string? correlationId) =>
        NormalizeOptional(FindPendingLlmReplyRequest(correlationId)?.RunId) ??
        NormalizeOptional(FindPendingWorkflowDraftRunRequest(correlationId)?.RunId);

    private static void UpsertPendingLlmReplyRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsLlmReplyEvent> field,
        NeedsLlmReplyEvent request)
    {
        RemovePendingLlmReplyRequest(field, request.CorrelationId);
        field.Add(request.Clone());
    }

    private static void RemovePendingLlmReplyRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsLlmReplyEvent> field,
        string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private static void UpsertPendingWorkflowDraftRunRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsWorkflowDraftRunEvent> field,
        NeedsWorkflowDraftRunEvent request)
    {
        RemovePendingWorkflowDraftRunRequest(field, request.CorrelationId);
        field.Add(request.Clone());
    }

    private static void RemovePendingWorkflowDraftRunRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsWorkflowDraftRunEvent> field,
        string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private static void UpsertReplyLifecycle(
        Google.Protobuf.Collections.RepeatedField<ConversationReplyLifecycleState> field,
        ConversationReplyLifecycleState lifecycle)
    {
        RemoveReplyLifecycle(field, lifecycle.CorrelationId, lifecycle.Mode);
        field.Add(lifecycle.Clone());
    }

    private static ConversationReplyLifecycleState? FindReplyLifecycle(
        Google.Protobuf.Collections.RepeatedField<ConversationReplyLifecycleState> field,
        string correlationId,
        ConversationReplyLifecycleMode mode) =>
        field.FirstOrDefault(lifecycle =>
            lifecycle.Mode == mode &&
            string.Equals(lifecycle.CorrelationId, correlationId, StringComparison.Ordinal));

    private static void RemoveReplyLifecycle(
        Google.Protobuf.Collections.RepeatedField<ConversationReplyLifecycleState> field,
        string? correlationId,
        ConversationReplyLifecycleMode mode)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (field[i].Mode == mode &&
                string.Equals(field[i].CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
            {
                field.RemoveAt(i);
            }
        }
    }

    private PendingInboundTurn? FindPendingInboundTurn(string? activityId)
    {
        var normalized = NormalizeOptional(activityId);
        if (normalized is null)
            return null;

        return State.PendingInboundTurns.FirstOrDefault(entry =>
            string.Equals(entry.ActivityId, normalized, StringComparison.Ordinal));
    }

    private static void UpsertPendingInboundTurn(
        Google.Protobuf.Collections.RepeatedField<PendingInboundTurn> field,
        PendingInboundTurn entry)
    {
        RemovePendingInboundTurn(field, entry.ActivityId);
        field.Add(entry.Clone());
    }

    private static void RemovePendingInboundTurn(
        Google.Protobuf.Collections.RepeatedField<PendingInboundTurn> field,
        string? activityId)
    {
        var normalized = NormalizeOptional(activityId);
        if (normalized is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].ActivityId, normalized, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    // Refactor (iter17/cluster-038): Old pattern: replay claim maps were process-local and lost on deactivation. New principle: bounded callback_jti claims are persisted as typed actor state and swept by actor transitions.
    private static void UpsertRelayReplayClaim(
        Google.Protobuf.Collections.RepeatedField<RelayReplayClaim> field,
        RelayReplayClaim entry)
    {
        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].RelayApiKeyId, entry.RelayApiKeyId, StringComparison.Ordinal) &&
                string.Equals(field[i].CallbackJti, entry.CallbackJti, StringComparison.Ordinal))
            {
                field.RemoveAt(i);
            }
        }

        field.Add(entry.Clone());
    }

    private static void SweepExpiredRelayReplayClaims(
        Google.Protobuf.Collections.RepeatedField<RelayReplayClaim> field,
        long nowMs)
    {
        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (field[i].ExpiresAtUnixMs > 0 && field[i].ExpiresAtUnixMs <= nowMs)
                field.RemoveAt(i);
        }
    }

    private static void TrimRelayReplayClaims(
        Google.Protobuf.Collections.RepeatedField<RelayReplayClaim> field,
        int cap)
    {
        while (field.Count > cap)
            field.RemoveAt(0);
    }

    private static void UpsertPendingRelayAdmission(
        Google.Protobuf.Collections.RepeatedField<PendingRelayAdmission> field,
        PendingRelayAdmission entry)
    {
        RemovePendingRelayAdmission(field, entry.ActivityId);
        field.Add(entry.Clone());
    }

    private static void RemovePendingRelayAdmission(
        Google.Protobuf.Collections.RepeatedField<PendingRelayAdmission> field,
        string? activityId)
    {
        var normalizedActivityId = NormalizeOptional(activityId);
        if (normalizedActivityId is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].ActivityId, normalizedActivityId, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private static void TrimPendingRelayAdmissions(
        Google.Protobuf.Collections.RepeatedField<PendingRelayAdmission> field,
        int cap)
    {
        while (field.Count > cap)
            field.RemoveAt(0);
    }

    private static void UpsertRecentAttachmentActivity(
        Google.Protobuf.Collections.RepeatedField<RecentConversationAttachmentActivity> field,
        ChatActivity? activity,
        long acceptedAtUnixMs)
    {
        if (activity?.Content?.Attachments is not { Count: > 0 })
        {
            NormalizeRecentAttachmentActivities(field, acceptedAtUnixMs);
            return;
        }

        var activityId = NormalizeOptional(activity.Id);
        if (activityId is null)
        {
            NormalizeRecentAttachmentActivities(field, acceptedAtUnixMs);
            return;
        }

        RemoveRecentAttachmentActivity(field, activityId);
        field.Add(new RecentConversationAttachmentActivity
        {
            ActivityId = activityId,
            AcceptedAtUnixMs = acceptedAtUnixMs,
            Activity = CloneForDurableState(activity),
        });
        NormalizeRecentAttachmentActivities(field, acceptedAtUnixMs);
    }

    private static void RemoveRecentAttachmentActivity(
        Google.Protobuf.Collections.RepeatedField<RecentConversationAttachmentActivity> field,
        string activityId)
    {
        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].ActivityId, activityId, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private static IEnumerable<RecentConversationAttachmentActivity> SelectRecentAttachmentActivities(
        ConversationGAgentState state,
        long nowMs)
    {
        var cutoff = nowMs > 0
            ? nowMs - (long)RecentAttachmentActivityWindow.TotalMilliseconds
            : 0;
        return state.RecentAttachmentActivities
            .Where(entry =>
                entry.Activity?.Content?.Attachments is { Count: > 0 } &&
                entry.AcceptedAtUnixMs > 0 &&
                (cutoff <= 0 || entry.AcceptedAtUnixMs >= cutoff))
            .TakeLast(RecentAttachmentActivityCap)
            .Select(entry => entry.Clone());
    }

    private static void NormalizeRecentAttachmentActivities(
        Google.Protobuf.Collections.RepeatedField<RecentConversationAttachmentActivity> field,
        long nowMs)
    {
        var cutoff = nowMs > 0
            ? nowMs - (long)RecentAttachmentActivityWindow.TotalMilliseconds
            : 0;
        for (var i = field.Count - 1; i >= 0; i--)
        {
            var entry = field[i];
            if (entry.Activity?.Content?.Attachments is not { Count: > 0 } ||
                entry.AcceptedAtUnixMs <= 0 ||
                (cutoff > 0 && entry.AcceptedAtUnixMs < cutoff))
            {
                field.RemoveAt(i);
            }
        }

        while (field.Count > RecentAttachmentActivityCap)
            field.RemoveAt(0);
    }

    private static string? ExtractLlmReplyCorrelationId(string? commandId)
    {
        var normalizedCommandId = NormalizeOptional(commandId);
        if (normalizedCommandId is null ||
            !normalizedCommandId.StartsWith("llm:", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeOptional(normalizedCommandId["llm:".Length..]);
    }

    private static string? ExtractWorkflowDraftRunCorrelationId(string? commandId)
    {
        var normalizedCommandId = NormalizeOptional(commandId);
        if (normalizedCommandId is null ||
            !normalizedCommandId.StartsWith("workflow-draft-run:", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeOptional(normalizedCommandId["workflow-draft-run:".Length..]);
    }

    private static void AppendBounded(
        Google.Protobuf.Collections.RepeatedField<string> field,
        string value,
        int cap)
    {
        field.Add(value);
        while (field.Count > cap)
            field.RemoveAt(0);
    }

    private static void AppendHistoryBounded(
        Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field,
        IEnumerable<ConversationHistoryEntry> entries,
        int cap)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Role))
                continue;
            field.Add(entry.Clone());
        }

        NormalizeHistoryWindow(field, cap);
    }

    private static void NormalizeHistoryWindow(
        Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field,
        int cap)
    {
        while (field.Count > cap)
            RemoveOldestHistoryUnit(field);

        DropOrphanToolResults(field);
    }

    private static void RemoveOldestHistoryUnit(
        Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field)
    {
        if (field.Count == 0)
            return;

        var first = field[0];
        if (IsAssistantToolCallMessage(first))
        {
            var callIds = first.ToolCalls.Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
            field.RemoveAt(0);
            RemoveToolResults(field, callIds);
            return;
        }

        if (IsToolResultMessage(first))
        {
            var callId = first.ToolCallId;
            field.RemoveAt(0);
            if (!string.IsNullOrWhiteSpace(callId))
                RemoveAssistantToolCall(field, callId);
            return;
        }

        field.RemoveAt(0);
    }

    private static void RemoveToolResults(
        Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field,
        HashSet<string> callIds)
    {
        if (callIds.Count == 0)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (IsToolResultMessage(field[i]) && callIds.Contains(field[i].ToolCallId))
                field.RemoveAt(i);
        }
    }

    private static void RemoveAssistantToolCall(
        Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field,
        string callId)
    {
        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (IsAssistantToolCallMessage(field[i]) &&
                field[i].ToolCalls.Any(call => string.Equals(call.Id, callId, StringComparison.Ordinal)))
            {
                field.RemoveAt(i);
            }
        }
    }

    private static void DropOrphanToolResults(Google.Protobuf.Collections.RepeatedField<ConversationHistoryEntry> field)
    {
        var openCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < field.Count; i++)
        {
            var entry = field[i];
            if (IsAssistantToolCallMessage(entry))
            {
                foreach (var call in entry.ToolCalls)
                {
                    if (!string.IsNullOrWhiteSpace(call.Id))
                        openCallIds.Add(call.Id);
                }
                continue;
            }

            if (!IsToolResultMessage(entry))
                continue;

            if (string.IsNullOrWhiteSpace(entry.ToolCallId) || !openCallIds.Remove(entry.ToolCallId))
            {
                field.RemoveAt(i);
                i--;
            }
        }
    }

    private static bool IsAssistantToolCallMessage(ConversationHistoryEntry entry) =>
        string.Equals(entry.Role, "assistant", StringComparison.Ordinal) && entry.ToolCalls.Count > 0;

    private static bool IsToolResultMessage(ConversationHistoryEntry entry) =>
        string.Equals(entry.Role, "tool", StringComparison.Ordinal);

    private static void AppendDelivery(ConversationGAgentState state, DeliveryProducedEvent produced)
    {
        var entry = ToDeliveryLedgerEntry(produced);
        state.RecentDeliveries.Add(entry);
        while (state.RecentDeliveries.Count > RecentDeliveriesCap)
            state.RecentDeliveries.RemoveAt(0);

        if (entry.Status == DeliveryStatus.Succeeded)
            state.LastSuccessfulDelivery = entry.Clone();
    }

    private static DeliveryLedgerEntry ToDeliveryLedgerEntry(DeliveryProducedEvent produced) =>
        new()
        {
            DeliveryKind = produced.DeliveryKind,
            Status = produced.Status,
            Target = produced.Target?.Clone() ?? new DeliveryTarget(),
            ProviderMessageId = produced.ProviderMessageId ?? string.Empty,
            CardId = produced.CardId ?? string.Empty,
            RequestId = produced.RequestId ?? string.Empty,
            SourceEventId = produced.SourceEventId ?? string.Empty,
            ProducedAtVersion = produced.ProducedAtVersion,
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;

    private long NextCommittedVersion(int batchOffset) =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + batchOffset + 1;

    private void AssignDeliveryProducedVersions(IReadOnlyList<IMessage> events)
    {
        var currentVersion = (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before assigning delivery event versions."))
            .CurrentVersion;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is DeliveryProducedEvent delivery)
                delivery.ProducedAtVersion = currentVersion + i + 1;
        }
    }
}
