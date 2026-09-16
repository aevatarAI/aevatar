using System.Globalization;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Integration.AI;

[GAgent(WorkflowRoleConventions.DefaultAgentKind)]
public class WorkflowRoleGAgent(
    IAgentToolExecutionPort toolExecutionPort,
    ILLMProviderFactory? llmProviderFactory = null,
    IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
    IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
    IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
    IEnumerable<IAgentToolSource>? toolSources = null,
    IRemoteToolApprovalPort? remoteToolApprovalPort = null,
    IToolSetRegistry? toolSetRegistry = null,
    IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null,
    RoleChatExecutionOptions? chatExecutionOptions = null,
    TimeProvider? timeProvider = null,
    ISecretVault? chatToolRecoverySecretVault = null,
    IAgentToolDiscoveryService? toolDiscoveryService = null)
    : RoleGAgent(
        toolExecutionPort,
        llmProviderFactory,
        additionalHooks,
        agentMiddlewares,
        llmMiddlewares,
        toolSources,
        remoteToolApprovalPort,
        timeProvider: timeProvider,
        chatExecutionOptions: chatExecutionOptions,
        chatToolRecoverySecretVault: chatToolRecoverySecretVault)
{
    public const string WorkflowAssistantRoleAgentKind = "workflow.assistant-role";
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";
    private const string WorkflowCompletionRetryCallbackPrefix = "workflow-llm-completion-retry";
    private const int WorkflowCompletionRetryInitialDelayMs = 250;
    private const int WorkflowCompletionRetryMaxDelayMs = 30_000;
    private readonly IToolSetRegistry? _toolSetRegistry = toolSetRegistry;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider = callerAccessTokenProvider;
    private readonly TimeProvider _workflowTimeProvider = timeProvider ?? TimeProvider.System;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService =
        toolDiscoveryService ?? AgentToolDiscoveryService.Instance;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await DeliverPendingWorkflowCompletionsAsync(ct);
    }

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleWorkflowRoleInitialize(WorkflowRoleInitializeEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = evt.RoleId ?? string.Empty,
            RoleName = evt.RoleName ?? string.Empty,
            ProviderName = evt.ProviderName ?? string.Empty,
            Model = evt.Model ?? string.Empty,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            MaxTokens = evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages,
            EventModules = evt.EventModules ?? string.Empty,
            EventRoutes = evt.EventRoutes ?? string.Empty,
        };
        if (evt.HasTemperature)
            initialize.Temperature = evt.Temperature;

        return HandleInitializeRoleAgent(initialize);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleWorkflowLlmExecutionIntent(WorkflowLlmExecutionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var chatRequest = BuildChatRequestFromWorkflowIntent(intent);
        LogWorkflowLlmInputFileRefs(intent, chatRequest);
        await HandleWorkflowIntentAsync(intent, chatRequest, publishStarted: true);
    }

    private async Task HandleWorkflowIntentAsync(
        WorkflowLlmExecutionIntent intent,
        ChatRequestEvent chatRequest,
        bool publishStarted,
        RecoveredChatTurn? recovery = null,
        AgentToolExecutionContext? recoveryToolContext = null,
        LLMControlContext? recoveryLlmControl = null)
    {
        var timeoutMs = ResolveLlmTimeoutMs(intent.TimeoutMs);
        using var timeoutCts = CreateTurnDeadlineCancellationSource(timeoutMs);
        var streamCt = timeoutCts.Token;
        try
        {
            var llmControl = recoveryLlmControl ??
                             LLMControlContextMapper.FromPayload(chatRequest.LlmControl);
            var toolContext = recoveryToolContext ??
                              llmControl.ToToolContext(
                                  AgentToolExecutionContextMapper.FromPayload(chatRequest.ToolContext));
            if (recoveryToolContext is null)
            {
                toolContext = await ResolveInitialDurableToolContextAsync(
                    chatRequest,
                    toolContext,
                    streamCt);
            }
            if (publishStarted)
            {
                if (!await TryEstablishWorkflowTurnAuthorityAsync(
                        chatRequest,
                        toolContext,
                        streamCt))
                {
                    return;
                }

                await EnsureSessionTextStartedAsync(chatRequest.SessionId, streamCt);
                streamCt.ThrowIfCancellationRequested();
                await PublishAsync(new WorkflowLlmInvocationStartedEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    RoleActorId = Id,
                }, TopologyAudience.Parent, streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            var replayRecord = await ExecuteWorkflowIntentStreamingChatAsync(
                intent,
                chatRequest,
                streamCt,
                recovery,
                toolContext,
                llmControl,
                turnAuthorityEstablished:
                    publishStarted ||
                    recovery?.Stage != RoleChatRecoveryCheckpointStage.ContinuationPrepared);
            if (replayRecord is null)
                return;
            streamCt.ThrowIfCancellationRequested();
            LogWorkflowLlmToolCalls(intent, replayRecord.ToolCalls, replayRecord.ToolReceipts);
            var pendingApproval = DetectPendingApproval(
                replayRecord.ToolReceipts,
                replayRecord.ToolCalls,
                chatRequest);
            if (pendingApproval != null)
            {
                pendingApproval.WorkflowLlmContinuation = BuildApprovalContinuation(
                    intent,
                    chatRequest.SessionId);
                await SuspendForToolApprovalAsync(pendingApproval, streamCt);
                streamCt.ThrowIfCancellationRequested();
                return;
            }

            // O1 (06-19-workflow-run-observatory): the committed RoleChatSessionCompletedEvent is the only
            // committed fact carrying both tool_calls (arguments) and tool_receipts (result/success/error);
            // persist the receipts (previously dropped) so the run-artifact fact builder can enrich tool detail.
            streamCt.ThrowIfCancellationRequested();
            await PersistRoleChatSessionCompletionAsync(
                chatRequest,
                replayRecord.Content,
                replayRecord.ReasoningContent,
                replayRecord.ToolCalls,
                replayRecord.ContentParts,
                replayRecord.ContentEmitted,
                replayRecord.Usage,
                replayRecord.Model,
                replayRecord.ToolReceipts,
                replayRecord.ToolResults,
                outcome: replayRecord.Outcome,
                failureCode: replayRecord.FailureCode,
                safeMessage: replayRecord.SafeMessage,
                authorizationRequired: replayRecord.AuthorizationRequired,
                ct: streamCt);
        }
        catch (Exception ex) when (HasCommittedSessionCompletion(chatRequest.SessionId))
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Workflow post-commit work failed after terminal authority was acquired. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                intent.RunId,
                intent.StepId,
                intent.SessionId);
            await DeliverWorkflowCompletionAsync(
                chatRequest.SessionId,
                State.Sessions[chatRequest.SessionId],
                CancellationToken.None);
        }
        catch (ChatToolPostExternalCheckpointException ex)
        {
            if (await TryHandlePostExternalToolCheckpointFailureAsync(chatRequest.SessionId, ex))
                return;

            throw;
        }
        catch (CommittedStatePublicationException)
        {
            throw;
        }
        catch (Exception) when (
            timeoutCts.IsCancellationRequested &&
            !HasCommittedSessionCompletion(chatRequest.SessionId))
        {
            await PersistRoleChatSessionCompletionAsync(
                chatRequest,
                content: string.Empty,
                reasoningContent: string.Empty,
                toolCalls: [],
                contentParts: [],
                contentEmitted: false,
                outcome: RoleChatSessionOutcome.Failed,
                failureCode: "LLM_TIMEOUT",
                safeMessage: "The LLM turn exceeded its deadline. Please try again.",
                clearMatchingPendingApproval: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[{Role}] Workflow LLM intent failed. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                intent.RunId,
                intent.StepId,
                intent.SessionId);
            await PersistWorkflowFailureAsync(
                chatRequest,
                "LLM_REQUEST_FAILED",
                ResolveWorkflowFailureMessage(ex),
                recovery,
                partialReplay: ResolvePartialReplay(ex),
                model: ResolveWorkflowModel(chatRequest, recoveryLlmControl));
        }
    }

    public override async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (request.WorkflowLlmToolApprovalContinuation is null)
        {
            await base.HandleChatRequest(request);
            return;
        }

        await HandleWorkflowApprovalContinuationAsync(request);
    }

    protected override Task HandleRecoveredChatTurnAsync(
        ChatRequestEvent request,
        RoleChatRecoveryCheckpoint checkpoint,
        RecoveredChatTurn recovery,
        AgentToolExecutionContext recoveryToolContext,
        LLMControlContext recoveryLlmControl)
    {
        var continuation = checkpoint.WorkflowLlmApprovalContinuation;
        if (continuation is null)
        {
            return base.HandleRecoveredChatTurnAsync(
                request,
                checkpoint,
                recovery,
                recoveryToolContext,
                recoveryLlmControl);
        }

        request.WorkflowLlmToolApprovalContinuation = continuation.Clone();
        if (recovery.Stage == RoleChatRecoveryCheckpointStage.ContinuationPrepared)
        {
            return HandleWorkflowApprovalContinuationAsync(
                request,
                recoveryToolContext,
                recoveryLlmControl,
                recovery,
                deliverFromContinuationSession: false);
        }

        return HandleWorkflowIntentAsync(
            BuildContinuationIntent(continuation),
            request,
            publishStarted: false,
            recovery: recovery,
            recoveryToolContext: recoveryToolContext,
            recoveryLlmControl: recoveryLlmControl);
    }

    protected override async Task<(IAgentTool Tool, AgentToolExecutionContext ExecutionContext)>
        ResolveApprovedToolExecutionAsync(
        PendingToolApprovalState pending,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var continuation = pending.WorkflowLlmContinuation;
        if (continuation is null)
            return await base.ResolveApprovedToolExecutionAsync(pending, toolContext, ct);

        var effectiveContext = string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken)
            ? await RefreshCallerTokenAsync(toolContext, ct)
            : toolContext;
        ct.ThrowIfCancellationRequested();
        var catalog = await BuildRequestToolCatalogAsync(
            ToToolScope(continuation),
            effectiveContext,
            continuation.ToolCatalogPolicyVersion,
            ct);
        ct.ThrowIfCancellationRequested();
        var tool = catalog.ExactTools.GetValueOrDefault(pending.ToolName)
                   ?? throw new InvalidOperationException(
                       $"Approved workflow tool '{pending.ToolName}' is no longer available.");
        return (tool, effectiveContext);
    }

    protected override async Task<AgentToolExecutionContext?> TryResolveRecoveryExecutionContextAsync(
        RoleChatRecoveryCheckpoint checkpoint,
        CancellationToken ct)
    {
        var durable = checkpoint.CallerDurableCredential;
        if (checkpoint.RequiresRuntimeCredential &&
            IsDurableAgentKeyCredential(durable))
        {
            // A durable unattended credential owns an exact Agent Key. Never replace it
            // with an OAuth token issued from an accompanying human authority.
            return await base.TryResolveRecoveryExecutionContextAsync(checkpoint, ct);
        }

        if (checkpoint.RequiresRuntimeCredential &&
            durable?.SourceKind == DurableCallerCredentialSourceKind.ScheduledDispatch &&
            durable.ScheduledCallerNyxIdAuthority is { } authority &&
            !string.IsNullOrWhiteSpace(authority.Platform) &&
            !string.IsNullOrWhiteSpace(authority.ExternalUserId) &&
            !string.IsNullOrWhiteSpace(authority.Scope))
        {
            var context = AgentToolExecutionContextMapper.FromRecoveryPayload(checkpoint.RecoveryContext) with
            {
                ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    authority.Platform,
                    authority.Tenant,
                    authority.ExternalUserId,
                    authority.Scope),
                SenderBinding = AgentToolExecutionContextMapper.FromRecoveryPayload(checkpoint.RecoveryContext)
                    .SenderBinding with
                {
                    BindingId = string.IsNullOrWhiteSpace(authority.BindingId)
                        ? AgentToolExecutionContextMapper.FromRecoveryPayload(checkpoint.RecoveryContext)
                            .SenderBinding.BindingId
                        : authority.BindingId,
                },
            };
            return await RefreshCallerTokenAsync(context, ct);
        }

        if (checkpoint.RequiresRuntimeCredential)
        {
            var context = AgentToolExecutionContextMapper.FromRecoveryPayload(
                checkpoint.RecoveryContext) with
            {
                ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
            };
            if (context.NyxIdAuthority.IsComplete &&
                !string.IsNullOrWhiteSpace(context.NyxIdAuthority.Scope))
            {
                return await RefreshCallerTokenAsync(context, ct);
            }
        }

        return await base.TryResolveRecoveryExecutionContextAsync(checkpoint, ct);
    }

    private async Task<AgentToolExecutionContext> ResolveInitialDurableToolContextAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext context,
        CancellationToken ct)
    {
        if (!IsDurableAgentKeyCredential(request.CallerDurableCredential))
        {
            return context;
        }

        var resolved = await base.TryResolveRecoveryExecutionContextAsync(
            new RoleChatRecoveryCheckpoint
            {
                RequiresRuntimeCredential = true,
                CallerDurableCredential = request.CallerDurableCredential.Clone(),
                RecoveryContext = context.ToRecoveryPayload(),
            },
            ct);
        if (resolved == null)
        {
            throw new InvalidOperationException(
                "Workflow durable caller credential is unavailable or no longer matches its exact vault descriptor.");
        }

        return request.CallerDurableCredential.SourceKind ==
               DurableCallerCredentialSourceKind.ChannelRegistration
            ? resolved with { CredentialSource = AgentToolCredentialSource.ChannelRegistration }
            : resolved;
    }

    protected override async Task<IAgentTool?> ResolveRecoveryToolAsync(
        RoleChatRecoveryCheckpoint checkpoint,
        RoleChatToolIntentState intent,
        AgentToolExecutionContext executionContext,
        CancellationToken ct)
    {
        var continuation = checkpoint.WorkflowLlmApprovalContinuation;
        if (continuation is null)
            return await base.ResolveRecoveryToolAsync(checkpoint, intent, executionContext, ct);

        var catalog = await BuildRequestToolCatalogAsync(
            ToToolScope(continuation),
            executionContext,
            continuation.ToolCatalogPolicyVersion,
            ct);
        ct.ThrowIfCancellationRequested();
        return catalog.ExactTools.GetValueOrDefault(intent.ToolName);
    }

    protected override async Task OnRoleChatSessionTerminalCommittedAsync(
        string sessionId,
        CancellationToken ct)
    {
        await base.OnRoleChatSessionTerminalCommittedAsync(sessionId, ct);
        if (State.Sessions.TryGetValue(sessionId, out var session) &&
            IsWorkflowLlmCompletionDeliveryPending(session))
        {
            await DeliverWorkflowCompletionAsync(sessionId, session.Clone(), ct);
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleWorkflowLlmCompletionDeliveryRetryFired(
        WorkflowLlmCompletionDeliveryRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (string.IsNullOrWhiteSpace(retry.SessionId) ||
            !State.Sessions.TryGetValue(retry.SessionId, out var session) ||
            session.WorkflowLlmCompletionDeliveryContext is null ||
            !string.Equals(
                ResolveWorkflowLlmCompletionDeliveryId(
                    session.WorkflowLlmCompletionDeliveryContext),
                retry.DeliveryId,
                StringComparison.Ordinal))
        {
            return;
        }

        var matchesScheduledAttempt =
            session.WorkflowLlmCompletionDeliveryStatus ==
                WorkflowLlmCompletionDeliveryStatus.RetryScheduled &&
            retry.Attempt == session.WorkflowLlmCompletionDeliveryAttempt;
        var matchesScheduledNextAttemptRecovery =
            session.WorkflowLlmCompletionDeliveryStatus ==
                WorkflowLlmCompletionDeliveryStatus.RetryScheduled &&
            retry.Attempt == session.WorkflowLlmCompletionDeliveryAttempt + 1;
        var matchesScheduleBeforeCommitRecovery =
            session.WorkflowLlmCompletionDeliveryStatus ==
                WorkflowLlmCompletionDeliveryStatus.Prepared &&
            retry.Attempt == session.WorkflowLlmCompletionDeliveryAttempt + 1;
        if (!matchesScheduledAttempt &&
            !matchesScheduledNextAttemptRecovery &&
            !matchesScheduleBeforeCommitRecovery)
        {
            return;
        }

        if (matchesScheduledNextAttemptRecovery || matchesScheduleBeforeCommitRecovery)
        {
            await PersistDomainEventAsync(new WorkflowLlmCompletionDeliveryRetryScheduledEvent
            {
                SessionId = retry.SessionId,
                DeliveryId = retry.DeliveryId,
                Attempt = retry.Attempt,
                CallbackId = BuildWorkflowCompletionRetryCallbackId(
                    retry.SessionId,
                    retry.DeliveryId),
                RetryAt = Timestamp.FromDateTimeOffset(_workflowTimeProvider.GetUtcNow()),
            });
            session = State.Sessions[retry.SessionId];
        }

        await DeliverWorkflowCompletionAsync(
            retry.SessionId,
            session.Clone(),
            CancellationToken.None,
            retry.Attempt);
    }

    [EventHandler]
    public async Task HandleReconcileWorkflowLlmCompletion(
        ReconcileWorkflowLlmCompletionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.SessionId) ||
            !State.Sessions.TryGetValue(command.SessionId, out var session) ||
            !session.Completed ||
            session.WorkflowLlmCompletionDeliveryContext is not { } context ||
            !string.Equals(context.RunId, command.RunId, StringComparison.Ordinal) ||
            !string.Equals(context.StepId, command.StepId, StringComparison.Ordinal) ||
            !string.Equals(context.SessionId, command.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        var alreadyDispatched = session.WorkflowLlmCompletionDeliveryStatus ==
                                WorkflowLlmCompletionDeliveryStatus.Dispatched;
        if (!alreadyDispatched && !IsWorkflowLlmCompletionDeliveryPending(session))
            return;

        Logger.LogWarning(
            "Redeliver committed workflow LLM completion after parent reconciliation. actor={ActorId} run={RunId} step={StepId} session={SessionId} execution={ExecutionId} observedParentStateVersion={ObservedParentStateVersion} alreadyDispatched={AlreadyDispatched}",
            Id,
            command.RunId,
            command.StepId,
            command.SessionId,
            command.ExecutionId,
            command.ObservedParentStateVersion,
            alreadyDispatched);
        await DeliverWorkflowCompletionAsync(
            command.SessionId,
            session.Clone(),
            CancellationToken.None,
            allowCommittedRedelivery: alreadyDispatched);
    }

    private async Task DeliverPendingWorkflowCompletionsAsync(CancellationToken ct)
    {
        var pending = State.Sessions
            .Where(static entry =>
                entry.Value.Completed &&
                entry.Value.WorkflowLlmCompletionDeliveryContext is not null &&
                IsWorkflowLlmCompletionDeliveryPending(entry.Value))
            .OrderBy(static entry => entry.Value.Sequence)
            .Select(static entry => (entry.Key, State: entry.Value.Clone()))
            .ToArray();

        foreach (var (sessionId, session) in pending)
            await DeliverWorkflowCompletionAsync(sessionId, session, ct);
    }

    private async Task DeliverWorkflowCompletionAsync(
        string roleSessionId,
        RoleChatSessionState session,
        CancellationToken ct,
        int? deliveryAttempt = null,
        bool allowCommittedRedelivery = false)
    {
        var context = session.WorkflowLlmCompletionDeliveryContext?.Clone();
        if (!session.Completed ||
            context is null ||
            (!IsWorkflowLlmCompletionDeliveryPending(session) &&
             !(allowCommittedRedelivery &&
               session.WorkflowLlmCompletionDeliveryStatus ==
               WorkflowLlmCompletionDeliveryStatus.Dispatched)))
        {
            return;
        }

        var deliveryId = ResolveWorkflowLlmCompletionDeliveryId(context);
        var attempt = Math.Max(
            session.WorkflowLlmCompletionDeliveryAttempt,
            deliveryAttempt ?? 0);
        var completion = BuildWorkflowCompletionFromCommittedSession(
            context.RunId,
            context.StepId,
            context.SessionId,
            session);
        using var deliveryDeadlineCts = CreatePostTurnProcessingCancellationSource();
        using var deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            deliveryDeadlineCts.Token);
        var deliveryCt = deliveryCts.Token;
        try
        {
            await PublishAsync(
                    completion,
                    TopologyAudience.Parent,
                    deliveryCt,
                    new EventEnvelopePublishOptions
                    {
                        Delivery = new EventEnvelopeDeliveryOptions
                        {
                            OperationId = string.Create(
                                CultureInfo.InvariantCulture,
                                $"workflow-llm-terminal:{deliveryId}:outcome:{(int)session.Outcome}"),
                        },
                    })
                .WaitAsync(deliveryCt);
        }
        catch (OperationCanceledException ex) when (
            deliveryDeadlineCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            if (allowCommittedRedelivery)
            {
                Logger.LogWarning(
                    ex,
                    "Reconciled workflow LLM completion redelivery exceeded its deadline; parent reconciliation will retry. actor={ActorId} session={SessionId} delivery={DeliveryId}",
                    Id,
                    roleSessionId,
                    deliveryId);
                return;
            }

            Logger.LogWarning(
                ex,
                "Workflow LLM completion delivery exceeded its deadline; scheduling durable retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                roleSessionId,
                deliveryId,
                attempt);
            await ScheduleWorkflowCompletionRetryAsync(
                roleSessionId,
                deliveryId,
                attempt,
                CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            if (allowCommittedRedelivery)
            {
                Logger.LogWarning(
                    ex,
                    "Reconciled workflow LLM completion redelivery failed; parent reconciliation will retry. actor={ActorId} session={SessionId} delivery={DeliveryId}",
                    Id,
                    roleSessionId,
                    deliveryId);
                return;
            }

            Logger.LogWarning(
                ex,
                "Workflow LLM completion delivery failed; scheduling durable retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                roleSessionId,
                deliveryId,
                attempt);
            await ScheduleWorkflowCompletionRetryAsync(
                roleSessionId,
                deliveryId,
                attempt,
                CancellationToken.None);
            return;
        }

        if (session.WorkflowLlmCompletionDeliveryStatus ==
            WorkflowLlmCompletionDeliveryStatus.Dispatched)
        {
            return;
        }

        try
        {
            await PersistDomainEventAsync(new WorkflowLlmCompletionDeliveryDispatchedEvent
            {
                SessionId = roleSessionId,
                DeliveryId = deliveryId,
                Attempt = attempt,
                DispatchedAtUnixTimeMs =
                    _workflowTimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Workflow LLM completion dispatch acknowledgement failed; scheduling deduplicated retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                roleSessionId,
                deliveryId,
                attempt);
            await ScheduleWorkflowCompletionRetryAsync(
                roleSessionId,
                deliveryId,
                attempt,
                CancellationToken.None);
        }
    }

    private async Task ScheduleWorkflowCompletionRetryAsync(
        string sessionId,
        string deliveryId,
        int failedAttempt,
        CancellationToken ct)
    {
        var currentAttempt = State.Sessions.TryGetValue(sessionId, out var current)
            ? current.WorkflowLlmCompletionDeliveryAttempt
            : 0;
        var attempt = Math.Max(currentAttempt, failedAttempt) + 1;
        var retryDelayMs = CalculateWorkflowCompletionRetryDelayMs(attempt);
        var dueTime = TimeSpan.FromMilliseconds(retryDelayMs);
        var retryAt = _workflowTimeProvider.GetUtcNow().Add(dueTime);
        var callbackId = BuildWorkflowCompletionRetryCallbackId(sessionId, deliveryId);
        var retry = new WorkflowLlmCompletionDeliveryRetryFiredEvent
        {
            SessionId = sessionId,
            DeliveryId = deliveryId,
            Attempt = attempt,
        };
        var retryOptions = BuildWorkflowCompletionRetryOptions(callbackId, attempt);
        try
        {
            var scheduled = await TrySchedulePostTurnDurableTimeoutAsync(
                callbackId,
                dueTime,
                retry,
                retryOptions,
                ct);
            if (!scheduled)
            {
                Logger.LogWarning(
                    "Workflow LLM completion retry scheduling exceeded its deadline; preserving Prepared outbox for activation recovery. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                    Id,
                    sessionId,
                    deliveryId,
                    attempt);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Workflow LLM completion retry scheduling failed; preserving Prepared outbox for activation recovery. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                sessionId,
                deliveryId,
                attempt);
            return;
        }

        await PersistDomainEventAsync(new WorkflowLlmCompletionDeliveryRetryScheduledEvent
        {
            SessionId = sessionId,
            DeliveryId = deliveryId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(retryAt),
        }, ct);
    }

    private static string BuildWorkflowCompletionRetryCallbackId(
        string sessionId,
        string deliveryId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            WorkflowCompletionRetryCallbackPrefix,
            sessionId,
            deliveryId);

    private static EventEnvelopePublishOptions BuildWorkflowCompletionRetryOptions(
        string callbackId,
        int attempt) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    callbackId,
                    attempt.ToString(CultureInfo.InvariantCulture)),
            },
        };

    private static long CalculateWorkflowCompletionRetryDelayMs(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 7);
        var delayMs = WorkflowCompletionRetryInitialDelayMs * (1L << exponent);
        return Math.Min(delayMs, WorkflowCompletionRetryMaxDelayMs);
    }

    private WorkflowLlmInvocationCompletedEvent BuildWorkflowCompletionFromCommittedSession(
        string runId,
        string stepId,
        string sessionId,
        RoleChatSessionState session)
    {
        var succeeded = session.Outcome == RoleChatSessionOutcome.Completed;
        var completed = new WorkflowLlmInvocationCompletedEvent
        {
            RunId = runId,
            StepId = stepId,
            SessionId = sessionId,
            RoleActorId = Id,
            Success = succeeded,
            Content = session.FinalContent ?? string.Empty,
            ReasoningContent = session.FinalReasoningContent ?? string.Empty,
            Usage = ToWorkflowUsageMetrics(session.Usage, session.Model),
            Error = succeeded
                ? string.Empty
                : BuildCommittedWorkflowFailureError(session),
        };
        var managedHandoff = ToWorkflowManagedHandoffOutcome(session.ToolReceipts);
        if (managedHandoff is not null)
            completed.ManagedHandoff = managedHandoff;
        if (session.AuthorizationRequired is { } authorizationRequired)
        {
            var requirement = new WorkflowInteractiveAuthorizationRequirement
            {
                ServiceSlug = authorizationRequired.ServiceSlug,
                ReasonCode = authorizationRequired.ReasonCode,
                SafeMessage = authorizationRequired.SafeMessage,
                RequestedScopes = { authorizationRequired.RequestedScopes },
            };
            if (authorizationRequired.KeyCreate is not null)
            {
                requirement.KeyCreate = new WorkflowInteractiveKeyCreateRequirement
                {
                    Name = authorizationRequired.KeyCreate.Name,
                    Platform = authorizationRequired.KeyCreate.Platform,
                    AllowedServiceIds = { authorizationRequired.KeyCreate.AllowedServiceIds },
                };
            }
            completed.AuthorizationRequirement = requirement;
        }
        return completed;
    }

    private static string BuildCommittedWorkflowFailureError(RoleChatSessionState session)
    {
        var safeMessage = SanitizeWorkflowFailureMessage(session.SafeMessage);
        return string.IsNullOrWhiteSpace(session.FailureCode)
            ? safeMessage
            : $"{session.FailureCode.Trim().ToLowerInvariant()}: {safeMessage}";
    }

    private async Task HandleWorkflowApprovalContinuationAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext? recoveryToolContext = null,
        LLMControlContext? recoveryLlmControl = null,
        RecoveredChatTurn? recovery = null,
        bool deliverFromContinuationSession = true)
    {
        var continuation = request.WorkflowLlmToolApprovalContinuation;
        if (deliverFromContinuationSession)
        {
            request.WorkflowLlmCompletionDeliveryContext ??=
                ToWorkflowLlmCompletionDeliveryContext(continuation);
        }
        else
        {
            request.WorkflowLlmCompletionDeliveryContext = null;
        }
        if (HasCommittedSessionCompletion(request.SessionId))
        {
            Logger.LogInformation(
                "[{Role}] Ignoring stale workflow approval continuation after terminal commit. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                continuation.RunId,
                continuation.StepId,
                request.SessionId);
            await DeliverWorkflowCompletionAsync(
                request.SessionId,
                State.Sessions[request.SessionId],
                CancellationToken.None);
            return;
        }

        var timeoutMs = ResolveLlmTimeoutMs(continuation.TimeoutMs);
        using var timeoutCts = CreateTurnDeadlineCancellationSource(timeoutMs);
        try
        {
            var toolContext = recoveryToolContext ?? await RefreshCallerTokenAsync(
                    AgentToolExecutionContextMapper.FromPayload(request.ToolContext),
                    timeoutCts.Token);
            timeoutCts.Token.ThrowIfCancellationRequested();
            toolContext = toolContext with
            {
                Chat = WorkflowChatContext(
                    continuation.RunId,
                    continuation.SessionId,
                    continuation.StepId),
            };
            request.ToolContext = toolContext.ToPayload();
            request.LlmControl = recoveryLlmControl?.ToPayload() ??
                                 BuildContinuationLlmControl(continuation, toolContext);
            var intent = BuildContinuationIntent(continuation);
            var replay = await ExecuteWorkflowIntentStreamingChatAsync(
                intent,
                request,
                timeoutCts.Token,
                recovery,
                recoveryToolContext,
                recoveryLlmControl);
            if (replay is null)
                return;
            timeoutCts.Token.ThrowIfCancellationRequested();
            var pendingApproval = DetectPendingApproval(
                replay.ToolReceipts,
                replay.ToolCalls,
                request);
            if (pendingApproval != null)
            {
                pendingApproval.WorkflowLlmContinuation = CloneApprovalContinuationForDirectParent(
                    continuation,
                    request.SessionId);
                await SuspendForToolApprovalAsync(pendingApproval, timeoutCts.Token);
                timeoutCts.Token.ThrowIfCancellationRequested();
                return;
            }

            timeoutCts.Token.ThrowIfCancellationRequested();
            await PersistRoleChatSessionCompletionAsync(
                request,
                replay.Content,
                replay.ReasoningContent,
                replay.ToolCalls,
                replay.ContentParts,
                replay.ContentEmitted,
                replay.Usage,
                replay.Model,
                replay.ToolReceipts,
                replay.ToolResults,
                outcome: replay.Outcome,
                failureCode: replay.FailureCode,
                safeMessage: replay.SafeMessage,
                authorizationRequired: replay.AuthorizationRequired,
                ct: timeoutCts.Token);
        }
        catch (Exception ex) when (HasCommittedSessionCompletion(request.SessionId))
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Workflow approval post-commit work failed after terminal authority was acquired. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                continuation.RunId,
                continuation.StepId,
                continuation.SessionId);
            await DeliverWorkflowCompletionAsync(
                request.SessionId,
                State.Sessions[request.SessionId],
                CancellationToken.None);
        }
        catch (ChatToolPostExternalCheckpointException ex)
        {
            if (await TryHandlePostExternalToolCheckpointFailureAsync(request.SessionId, ex))
                return;

            throw;
        }
        catch (CommittedStatePublicationException)
        {
            throw;
        }
        catch (Exception) when (
            timeoutCts.IsCancellationRequested &&
            !HasCommittedSessionCompletion(request.SessionId))
        {
            await PersistRoleChatSessionCompletionAsync(
                request,
                content: string.Empty,
                reasoningContent: string.Empty,
                toolCalls: [],
                contentParts: [],
                contentEmitted: false,
                outcome: RoleChatSessionOutcome.Failed,
                failureCode: "APPROVAL_TOOL_TIMEOUT",
                safeMessage: "The approval continuation exceeded its deadline. Please try again.",
                clearMatchingPendingApproval: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Workflow approval continuation failed. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                continuation.RunId,
                continuation.StepId,
                continuation.SessionId);
            await PersistWorkflowFailureAsync(
                request,
                "APPROVAL_CONTINUATION_FAILED",
                ResolveWorkflowFailureMessage(ex),
                recovery,
                partialReplay: ResolvePartialReplay(ex),
                model: ResolveWorkflowModel(request, recoveryLlmControl),
                clearMatchingPendingApproval: true);
        }
    }

    private async Task<AgentToolExecutionContext> RefreshCallerTokenAsync(
        AgentToolExecutionContext context,
        CancellationToken ct)
    {
        var durable = context.DurableNyxIdCredential;
        if (IsDurableAgentKeyCredential(durable))
        {
            var resolved = await base.TryResolveRecoveryExecutionContextAsync(
                new RoleChatRecoveryCheckpoint
                {
                    RequiresRuntimeCredential = true,
                    CallerDurableCredential = durable!.Clone(),
                    RecoveryContext = context.ToRecoveryPayload(),
                },
                ct);
            if (resolved == null)
            {
                throw new InvalidOperationException(
                    "The workflow Agent Key is unavailable or no longer matches its exact vault descriptor.");
            }

            return durable!.SourceKind == DurableCallerCredentialSourceKind.ChannelRegistration
                ? resolved with { CredentialSource = AgentToolCredentialSource.ChannelRegistration }
                : resolved;
        }

        var authority = context.NyxIdAuthority;
        if (!authority.IsComplete || string.IsNullOrWhiteSpace(authority.Scope))
            return context;
        if (_callerAccessTokenProvider is null)
            throw new InvalidOperationException(
                "Workflow caller NyxID access token provider is unavailable.");

        var token = await _callerAccessTokenProvider.IssueAsync(new WorkflowCallerNyxIdAuthority
        {
            Platform = authority.Platform,
            Tenant = authority.Tenant ?? string.Empty,
            ExternalUserId = authority.ExternalUserId,
            Scope = authority.Scope,
            BindingId = context.SenderBinding.BindingId ?? string.Empty,
        }, ct);
        return context with
        {
            Credentials = new AgentToolCredentials(
                token,
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        };
    }

    private static bool IsDurableAgentKeyCredential(
        DurableCallerCredentialRef? credential) =>
        DurableCallerAgentKeyContract.Matches(credential);

    private async Task PersistWorkflowFailureAsync(
        ChatRequestEvent request,
        string failureCode,
        string safeMessage,
        RecoveredChatTurn? recovery = null,
        WorkflowIntentReplayRecord? partialReplay = null,
        string? model = null,
        bool clearMatchingPendingApproval = false)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            var delivery = request.WorkflowLlmCompletionDeliveryContext;
            await PublishAsync(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = delivery?.RunId ?? string.Empty,
                StepId = delivery?.StepId ?? string.Empty,
                SessionId = delivery?.SessionId ?? string.Empty,
                RoleActorId = Id,
                Success = false,
                Error = safeMessage,
            }, TopologyAudience.Parent);
            return;
        }

        var recoveredResults = recovery?.ToolResults ?? [];
        await PersistRoleChatSessionCompletionAsync(
            request,
            content: partialReplay?.Content ?? string.Empty,
            reasoningContent: partialReplay?.ReasoningContent ?? string.Empty,
            toolCalls: partialReplay?.ToolCalls ?? recoveredResults
                .Select(static result => result.ToolCall)
                .ToArray(),
            contentParts: partialReplay?.ContentParts ?? [],
            contentEmitted: partialReplay?.ContentEmitted == true,
            usage: partialReplay?.Usage,
            model: partialReplay?.Model ?? model,
            toolReceipts: partialReplay?.ToolReceipts ?? recoveredResults
                .Where(static result => result.Receipt is not null)
                .Select(static result => result.Receipt!.Clone())
                .ToArray(),
            toolResults: partialReplay?.ToolResults ?? recoveredResults
                .Select(ToToolResultEvent)
                .ToArray(),
            outcome: RoleChatSessionOutcome.Failed,
            failureCode: failureCode,
            safeMessage: safeMessage,
            authorizationRequired: partialReplay?.AuthorizationRequired,
            clearMatchingPendingApproval: clearMatchingPendingApproval);
    }

    private static WorkflowLlmToolApprovalContinuation BuildApprovalContinuation(
        WorkflowLlmExecutionIntent intent,
        string directParentRoleChatSessionId = "")
    {
        var continuation = new WorkflowLlmToolApprovalContinuation
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            Model = intent.Model ?? string.Empty,
            UserMemoryPrompt = intent.UserMemoryPrompt ?? string.Empty,
            RoutePreference = intent.RoutePreference ?? string.Empty,
            TimeoutMs = intent.TimeoutMs,
            DirectParentRoleChatSessionId = directParentRoleChatSessionId,
            ToolCatalogPolicyVersion = intent.ToolCatalogPolicyVersion ?? string.Empty,
            RestrictToolSets = intent.AgentToolScope?.RestrictToolSets == true,
            RestrictAllowedToolNames = intent.AgentToolScope?.RestrictAllowedToolNames == true,
        };
        if (intent.HasMaxToolRounds)
            continuation.MaxToolRounds = intent.MaxToolRounds;
        if (intent.AgentToolScope != null)
        {
            continuation.ToolSetRefs.Add(intent.AgentToolScope.ToolSetRefs);
            continuation.AllowedToolNames.Add(intent.AgentToolScope.AllowedToolNames);
        }
        return continuation;
    }

    private static WorkflowLlmToolApprovalContinuation CloneApprovalContinuationForDirectParent(
        WorkflowLlmToolApprovalContinuation continuation,
        string directParentRoleChatSessionId)
    {
        var next = continuation.Clone();
        next.DirectParentRoleChatSessionId = directParentRoleChatSessionId;
        return next;
    }

    private static WorkflowLlmCompletionDeliveryContext ToWorkflowLlmCompletionDeliveryContext(
        WorkflowLlmExecutionIntent intent) =>
        new()
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
        };

    private static WorkflowLlmCompletionDeliveryContext ToWorkflowLlmCompletionDeliveryContext(
        WorkflowLlmToolApprovalContinuation continuation) =>
        new()
        {
            RunId = continuation.RunId ?? string.Empty,
            StepId = continuation.StepId ?? string.Empty,
            SessionId = continuation.SessionId ?? string.Empty,
        };

    private static WorkflowAgentToolScope ToToolScope(
        WorkflowLlmToolApprovalContinuation continuation)
    {
        var scope = new WorkflowAgentToolScope
        {
            RestrictToolSets = continuation.RestrictToolSets,
            RestrictAllowedToolNames = continuation.RestrictAllowedToolNames,
        };
        scope.ToolSetRefs.Add(continuation.ToolSetRefs);
        scope.AllowedToolNames.Add(continuation.AllowedToolNames);
        return scope;
    }

    private static WorkflowLlmExecutionIntent BuildContinuationIntent(
        WorkflowLlmToolApprovalContinuation continuation) =>
        new()
        {
            RunId = continuation.RunId,
            StepId = continuation.StepId,
            SessionId = continuation.SessionId,
            TimeoutMs = continuation.TimeoutMs,
            AgentToolScope = ToToolScope(continuation),
            ToolCatalogPolicyVersion = continuation.ToolCatalogPolicyVersion,
        };

    private static LLMControlContextPayload BuildContinuationLlmControl(
        WorkflowLlmToolApprovalContinuation continuation,
        AgentToolExecutionContext toolContext)
    {
        var control = new LLMControlContextPayload
        {
            NyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken ?? string.Empty,
            NyxIdOrgToken = toolContext.Credentials.NyxIdOrgToken ?? string.Empty,
            SenderNyxIdAccessToken = toolContext.Credentials.SenderNyxIdAccessToken ?? string.Empty,
            ModelOverride = continuation.Model,
            NyxIdRoutePreference = continuation.RoutePreference,
            UserMemoryPrompt = continuation.UserMemoryPrompt,
        };
        if (continuation.HasMaxToolRounds)
            control.MaxToolRoundsOverride = continuation.MaxToolRounds;
        return control;
    }

    private static ChatRequestEvent BuildChatRequestFromWorkflowIntent(WorkflowLlmExecutionIntent intent)
    {
        var workflowRuntimeContext = new AgentWorkflowRuntimeContext(
            Normalize(intent.WorkflowRuntimeContext?.ParentActorId),
            Normalize(intent.WorkflowRuntimeContext?.ParentRunId),
            Normalize(intent.WorkflowRuntimeContext?.ParentStepId),
            Normalize(intent.WorkflowRuntimeContext?.RootRunId),
            Math.Max(0, intent.WorkflowRuntimeContext?.Depth ?? 0));
        var toolContext = WorkflowCallerCredentialToolContextMapper.FromCredential(
            intent.CallerCredential,
            workflowRuntimeContext);
        if (!string.IsNullOrWhiteSpace(intent.RoutePreference))
        {
            toolContext = toolContext with
            {
                Routing = toolContext.Routing with
                {
                    NyxIdRoutePreference = intent.RoutePreference.Trim(),
                },
            };
        }
        toolContext = ApplyToolVisibility(intent.AgentToolScope, toolContext);
        toolContext = WorkflowRunScopeToolContextMapper.Apply(intent.ScopeId, toolContext);
        toolContext = ApplySchedule(intent.ScheduleId, toolContext);
        toolContext = toolContext with
        {
            InvocationSurface = AgentToolInvocationSurface.WorkflowLlmToolLoop,
            Chat = WorkflowChatContext(intent.RunId, intent.SessionId, intent.StepId),
            InputFileRefs = intent.InputFileRefs.Select(ToChatFileRef).ToArray(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = intent.Prompt ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            TimeoutMs = intent.TimeoutMs,
            WorkflowLlmCompletionDeliveryContext =
                ToWorkflowLlmCompletionDeliveryContext(intent),
            WorkflowLlmToolApprovalContinuation = BuildApprovalContinuation(intent),
            CallerDurableCredential = intent.CallerCredential?.DurableCallerCredential?.Clone(),
            ToolContext = AgentToolExecutionContextMapper.ToPayload(toolContext),
            LlmControl = new LLMControlContextPayload
            {
                NyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken ?? string.Empty,
                ModelOverride = intent.Model ?? string.Empty,
                NyxIdRoutePreference = toolContext.Routing.NyxIdRoutePreference ?? string.Empty,
                UserMemoryPrompt = intent.UserMemoryPrompt ?? string.Empty,
                SenderNyxIdAccessToken = intent.SenderNyxIdAccessToken ?? string.Empty,
            },
        };
        if (intent.HasMaxToolRounds)
            request.LlmControl.MaxToolRoundsOverride = intent.MaxToolRounds;
        request.InputParts.Add(intent.InputFileRefs.Select(ToChatContentPart));
        CopyWorkflowIntentMetadata(intent.Headers, request.Metadata);
        CopyWorkflowIntentMetadata(intent.Annotations, request.Metadata);
        return request;
    }

    private static AgentChatInvocationContext WorkflowChatContext(
        string? runId,
        string? sessionId,
        string? stepId) =>
        new(
            AgentChatInvocationSurface.WorkflowChat,
            Normalize(runId),
            Normalize(sessionId),
            null,
            Normalize(stepId),
            null);

    private static ChatContentPart ToChatContentPart(WorkflowFileRef fileRef)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        return new ChatContentPart
        {
            Kind = ResolveChatContentPartKind(fileRef.MediaType),
            Uri = ResolveFileRefUri(fileRef),
            MediaType = Normalize(fileRef.MediaType) ?? string.Empty,
            Name = Normalize(fileRef.FileName) ?? string.Empty,
            FileRef = ToChatFileRef(fileRef),
        };
    }

    private static Aevatar.AI.Abstractions.ChatFileRef ToChatFileRef(WorkflowFileRef fileRef) =>
        new()
        {
            FileId = Normalize(fileRef.FileId) ?? string.Empty,
            ArtifactId = Normalize(fileRef.ArtifactId) ?? string.Empty,
            SourceKind = ToChatFileSourceKind(fileRef.SourceKind),
            SourceMessageId = Normalize(fileRef.SourceMessageId) ?? string.Empty,
            SourceResourceKey = Normalize(fileRef.SourceResourceKey) ?? string.Empty,
            FileName = Normalize(fileRef.FileName) ?? string.Empty,
            MediaType = Normalize(fileRef.MediaType) ?? string.Empty,
            SizeBytes = fileRef.SizeBytes,
            Sha256 = Normalize(fileRef.Sha256) ?? string.Empty,
            CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
            ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
            OwnerRunId = Normalize(fileRef.OwnerRunId) ?? string.Empty,
            OwnerScopeId = Normalize(fileRef.OwnerScopeId) ?? string.Empty,
        };

    private void LogWorkflowLlmInputFileRefs(
        WorkflowLlmExecutionIntent intent,
        ChatRequestEvent request)
    {
        var firstIntentFileRef = intent.InputFileRefs.FirstOrDefault();
        var requestFileRefParts = request.InputParts
            .Where(static part => part.FileRef is not null)
            .ToArray();
        var firstRequestPart = requestFileRefParts.FirstOrDefault();
        var firstRequestFileRef = firstRequestPart?.FileRef;
        var toolContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);
        var firstContextFileRef = toolContext.InputFileRefs.FirstOrDefault();

        Logger.LogWarning(
            "Workflow role LLM input file refs prepared. role={Role} runId={RunId} stepId={StepId} sessionId={SessionId} intentInputFileRefCount={IntentInputFileRefCount} requestInputPartCount={RequestInputPartCount} requestFileRefPartCount={RequestFileRefPartCount} toolContextInputFileRefCount={ToolContextInputFileRefCount} firstIntentFileId={FirstIntentFileId} firstIntentArtifactId={FirstIntentArtifactId} firstIntentMediaType={FirstIntentMediaType} firstRequestPartKind={FirstRequestPartKind} firstRequestFileId={FirstRequestFileId} firstRequestArtifactId={FirstRequestArtifactId} firstRequestMediaType={FirstRequestMediaType} firstContextFileId={FirstContextFileId} firstContextArtifactId={FirstContextArtifactId} firstContextMediaType={FirstContextMediaType}",
            RoleName,
            intent.RunId ?? string.Empty,
            intent.StepId ?? string.Empty,
            intent.SessionId ?? string.Empty,
            intent.InputFileRefs.Count,
            request.InputParts.Count,
            requestFileRefParts.Length,
            toolContext.InputFileRefs.Count,
            firstIntentFileRef?.FileId ?? string.Empty,
            firstIntentFileRef?.ArtifactId ?? string.Empty,
            firstIntentFileRef?.MediaType ?? string.Empty,
            firstRequestPart?.Kind.ToString() ?? string.Empty,
            firstRequestFileRef?.FileId ?? string.Empty,
            firstRequestFileRef?.ArtifactId ?? string.Empty,
            firstRequestFileRef?.MediaType ?? string.Empty,
            firstContextFileRef?.FileId ?? string.Empty,
            firstContextFileRef?.ArtifactId ?? string.Empty,
            firstContextFileRef?.MediaType ?? string.Empty);
    }

    private void LogWorkflowLlmToolCalls(
        WorkflowLlmExecutionIntent intent,
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<AgentToolReceipt> toolReceipts)
    {
        Logger.LogWarning(
            "Workflow role LLM tool calls completed. role={Role} runId={RunId} stepId={StepId} sessionId={SessionId} toolCallCount={ToolCallCount} toolNames={ToolNames} toolReceiptCount={ToolReceiptCount} receiptToolNames={ReceiptToolNames} documentExtractCalled={DocumentExtractCalled} documentExtractReceiptCount={DocumentExtractReceiptCount}",
            RoleName,
            intent.RunId ?? string.Empty,
            intent.StepId ?? string.Empty,
            intent.SessionId ?? string.Empty,
            toolCalls.Count,
            JoinToolNames(toolCalls.Select(static toolCall => toolCall.Name)),
            toolReceipts.Count,
            JoinToolNames(toolReceipts.Select(static receipt => receipt.ToolName)),
            toolCalls.Any(static toolCall => string.Equals(toolCall.Name, "document_extract", StringComparison.OrdinalIgnoreCase)) ||
            toolReceipts.Any(static receipt => string.Equals(receipt.ToolName, "document_extract", StringComparison.OrdinalIgnoreCase)),
            toolReceipts.Count(static receipt => string.Equals(receipt.ToolName, "document_extract", StringComparison.OrdinalIgnoreCase)));
    }

    private static string JoinToolNames(IEnumerable<string?> toolNames) =>
        string.Join(',', toolNames
            .Select(Normalize)
            .Where(static toolName => toolName is not null)
            .Distinct(StringComparer.Ordinal));

    private static Aevatar.AI.Abstractions.ChatFileSourceKind ToChatFileSourceKind(
        WorkflowFileSourceKind sourceKind) =>
        sourceKind switch
        {
            WorkflowFileSourceKind.ChatInput => Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
            WorkflowFileSourceKind.FormUpload => Aevatar.AI.Abstractions.ChatFileSourceKind.FormUpload,
            WorkflowFileSourceKind.ConnectedServiceResource =>
                Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
            WorkflowFileSourceKind.ExternalResource => Aevatar.AI.Abstractions.ChatFileSourceKind.ExternalResource,
            WorkflowFileSourceKind.Generated => Aevatar.AI.Abstractions.ChatFileSourceKind.Generated,
            _ => Aevatar.AI.Abstractions.ChatFileSourceKind.Unspecified,
        };

    private static ChatContentPartKind ResolveChatContentPartKind(string? mediaType)
    {
        var normalized = Normalize(mediaType);
        if (normalized is null)
            return ChatContentPartKind.Unspecified;
        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Image;
        if (normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Audio;
        if (normalized.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Video;
        return ChatContentPartKind.Unspecified;
    }

    private static string ResolveFileRefUri(WorkflowFileRef fileRef) =>
        Normalize(fileRef.ArtifactId) ??
        (string.IsNullOrWhiteSpace(fileRef.FileId)
            ? string.Empty
            : $"workflow-file://{fileRef.FileId.Trim()}");

    private static AgentToolExecutionContext ApplyToolVisibility(
        WorkflowAgentToolScope? scope,
        AgentToolExecutionContext toolContext)
    {
        if (scope == null || (!scope.RestrictAllowedToolNames && scope.AllowedToolNames.Count == 0))
            return toolContext;

        return toolContext with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(scope.AllowedToolNames),
        };
    }

    private static AgentToolExecutionContext ApplySchedule(
        string? scheduleId,
        AgentToolExecutionContext toolContext)
    {
        var normalizedScheduleId = Normalize(scheduleId);
        return normalizedScheduleId is null
            ? toolContext
            : toolContext with { Schedule = new AgentToolScheduleContext(normalizedScheduleId) };
    }

    private static void CopyWorkflowIntentMetadata(
        IEnumerable<KeyValuePair<string, string>> source,
        IDictionary<string, string> target)
    {
        foreach (var pair in source)
        {
            if (string.Equals(pair.Key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal))
                continue;

            target[pair.Key] = pair.Value;
        }
    }

    private async Task<WorkflowIntentReplayRecord?> ExecuteWorkflowIntentStreamingChatAsync(
        WorkflowLlmExecutionIntent intent,
        ChatRequestEvent request,
        CancellationToken streamCt,
        RecoveredChatTurn? recovery = null,
        AgentToolExecutionContext? recoveryToolContext = null,
        LLMControlContext? recoveryLlmControl = null,
        bool turnAuthorityEstablished = false)
    {
        var inputParts = ResolveWorkflowRequestInputParts(request);
        var llmControl = recoveryLlmControl ?? LLMControlContextMapper.FromPayload(request.LlmControl);
        var toolContext = recoveryToolContext ??
                          llmControl.ToToolContext(AgentToolExecutionContextMapper.FromPayload(request.ToolContext));
        if (!turnAuthorityEstablished &&
            !await TryEstablishWorkflowTurnAuthorityAsync(request, toolContext, streamCt))
        {
            return null;
        }
        await EnsureSessionTextStartedAsync(request.SessionId, streamCt);
        streamCt.ThrowIfCancellationRequested();
        var turnCatalog = await BuildRequestToolCatalogAsync(
            intent.AgentToolScope,
            toolContext,
            intent.ToolCatalogPolicyVersion,
            streamCt);
        streamCt.ThrowIfCancellationRequested();
        var firstIntentFileRef = intent.InputFileRefs.FirstOrDefault();
        var firstToolContextFileRef = toolContext.InputFileRefs.FirstOrDefault();
        Logger.LogWarning(
            "Workflow LLM request tool catalog resolved. runId={RunId} stepId={StepId} sessionId={SessionId} intentInputFileRefCount={IntentInputFileRefCount} requestInputPartCount={RequestInputPartCount} toolContextInputFileRefCount={ToolContextInputFileRefCount} toolSetRefCount={ToolSetRefCount} ownedToolCount={ExactToolCount} schemaBytes={SchemaBytes} catalogDigest={CatalogDigest} ownedToolNames={ExactToolNames} firstIntentFileId={FirstIntentFileId} firstIntentArtifactId={FirstIntentArtifactId} firstIntentMediaType={FirstIntentMediaType} firstToolContextFileId={FirstToolContextFileId} firstToolContextArtifactId={FirstToolContextArtifactId} firstToolContextMediaType={FirstToolContextMediaType}",
            intent.RunId ?? string.Empty,
            intent.StepId ?? string.Empty,
            intent.SessionId ?? string.Empty,
            intent.InputFileRefs.Count,
            inputParts.Count,
            toolContext.InputFileRefs.Count,
            intent.AgentToolScope?.ToolSetRefs.Count ?? 0,
            turnCatalog.Proof.ToolCount,
            turnCatalog.Proof.SchemaBytes,
            turnCatalog.Proof.CatalogDigest,
            string.Join(',', turnCatalog.ExactTools.Keys),
            firstIntentFileRef?.FileId ?? string.Empty,
            firstIntentFileRef?.ArtifactId ?? string.Empty,
            firstIntentFileRef?.MediaType ?? string.Empty,
            firstToolContextFileRef?.FileId ?? string.Empty,
            firstToolContextFileRef?.ArtifactId ?? string.Empty,
            firstToolContextFileRef?.MediaType ?? string.Empty);
        toolContext = AddRequestToolsToVisibility(toolContext, turnCatalog.ExactTools.Keys);
        var metadata = request.Metadata.Count > 0
            ? AgentToolExecutionContextMapper.StripOwnedControlKeys(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal))
            : null;

        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        var toolCalls = new WorkflowToolCallAccumulator();
        var toolCallLifecycles = new List<WorkflowToolCallLifecycle>();
        var recoveredToolResults = recovery?.ToolResults ?? [];
        foreach (var recoveredToolResult in recoveredToolResults)
            toolCalls.TrackDelta(recoveredToolResult.ToolCall);
        var toolReceipts = recoveredToolResults
            .Where(static result => result.Receipt is not null)
            .Select(static result => result.Receipt!.Clone())
            .ToList();
        var toolResults = recoveredToolResults
            .Select(ToToolResultEvent)
            .ToList();
        var contentParts = new List<ContentPart>();
        TokenUsage? usage = null;
        var sessionDeltas = CreateSessionDeltaBatcher(
            request.SessionId,
            publishParentDeltas: false);

        WorkflowIntentReplayRecord CaptureReplay()
        {
            var normalizedToolResults = toolResults
                .Select(result =>
                {
                    var normalized = result.Clone();
                    var receipt = toolReceipts.LastOrDefault(candidate =>
                        string.Equals(candidate.CallId, normalized.CallId, StringComparison.Ordinal));
                    if (receipt is null)
                        return normalized;

                    normalized.Success = receipt.Status == AgentToolReceiptStatus.Success;
                    normalized.Error = normalized.Success
                        ? string.Empty
                        : receipt.ErrorMessage ?? string.Empty;
                    normalized.Receipt = receipt.Clone();
                    return normalized;
                })
                .ToArray();
            var authorizationRequired = toolReceipts
                .LastOrDefault(receipt =>
                    receipt.Status == AgentToolReceiptStatus.AuthorizationRequired &&
                    receipt.AuthorizationRequired != null)
                ?.AuthorizationRequired
                .Clone();
            return new WorkflowIntentReplayRecord(
                fullContent.ToString(),
                fullReasoning.ToString(),
                MergeCompletedToolCalls(toolCalls.BuildToolCalls(), toolCallLifecycles),
                toolReceipts.Select(static receipt => receipt.Clone()).ToArray(),
                normalizedToolResults,
                contentParts.ToArray(),
                Usage: usage,
                Model: ResolveWorkflowModel(request, llmControl),
                ContentEmitted: fullContent.Length > 0,
                Outcome: authorizationRequired is null
                    ? RoleChatSessionOutcome.Completed
                    : RoleChatSessionOutcome.Blocked,
                FailureCode: authorizationRequired is null ? string.Empty : "AUTHORIZATION_REQUIRED",
                SafeMessage: authorizationRequired?.SafeMessage ?? string.Empty,
                AuthorizationRequired: authorizationRequired);
        }

        var stream = recovery?.Transcript is { Count: > 0 } recoveryTranscript
            ? ContinueChatStreamAsync(
                inputParts,
                recoveryTranscript,
                request.SessionId,
                llmControl,
                toolContext,
                turnCatalog,
                metadata,
                streamCt)
            : ChatStreamAsync(
                inputParts,
                request.SessionId,
                llmControl,
                toolContext,
                turnCatalog,
                metadata,
                streamCt);
        try
        {
            await foreach (var chunk in stream)
            {
                if (chunk.LLMInvocationStarted != null)
                {
                    await sessionDeltas.FlushAsync(CancellationToken.None);
                    var started = chunk.LLMInvocationStarted;
                    await PersistSessionProgressAsync(
                        request.SessionId,
                        progress =>
                        {
                            progress.ModelStarted = new RoleChatModelStartedProgress
                            {
                                OperationId = started.OperationId,
                                Round = started.Round,
                                Model = started.Model,
                                Provider = started.Provider,
                                InputSummary = started.InputSummary,
                                ToolCatalogProof = turnCatalog.Proof.ToPayload(),
                                ToolCatalogPolicyVersion = intent.ToolCatalogPolicyVersion ?? string.Empty,
                            };
                            progress.ModelStarted.AvailableToolNames.Add(started.AvailableToolNames);
                        },
                        CancellationToken.None);
                    continue;
                }

                if (chunk.LLMInvocationCompleted != null)
                {
                    await sessionDeltas.FlushAsync(CancellationToken.None);
                    var completed = chunk.LLMInvocationCompleted;
                    await PersistSessionProgressAsync(
                        request.SessionId,
                        progress => progress.ModelCompleted = new RoleChatModelCompletedProgress
                        {
                            OperationId = completed.OperationId,
                            Round = completed.Round,
                            Model = completed.Model,
                            Content = completed.Content,
                            ReasoningContent = completed.ReasoningContent,
                            Usage = ToTokenUsagePayload(completed.Usage),
                            FinishReason = completed.FinishReason,
                            Success = completed.Success,
                            Error = completed.Error,
                        },
                        CancellationToken.None);
                    streamCt.ThrowIfCancellationRequested();
                    continue;
                }

                streamCt.ThrowIfCancellationRequested();

                if (chunk.Usage != null)
                    usage = chunk.Usage;

                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    fullContent.Append(chunk.DeltaContent);
                    await sessionDeltas.AppendTextAsync(chunk.DeltaContent, streamCt);
                }

                if (chunk.DeltaContentPart != null)
                {
                    await sessionDeltas.FlushAsync(streamCt);
                    contentParts.Add(chunk.DeltaContentPart);
                    await PersistSessionProgressAsync(
                        request.SessionId,
                        progress => progress.Media = new RoleChatMediaProgress
                        {
                            AgentId = Id,
                            Part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart),
                        },
                        streamCt);
                    streamCt.ThrowIfCancellationRequested();
                }

                if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
                {
                    fullReasoning.Append(chunk.DeltaReasoningContent);
                    await sessionDeltas.AppendReasoningAsync(chunk.DeltaReasoningContent, streamCt);
                }

                if (chunk.DeltaToolCall != null)
                    toolCalls.TrackDelta(chunk.DeltaToolCall);

                if (chunk.ToolCallStarted != null)
                {
                    await sessionDeltas.FlushAsync(CancellationToken.None);
                    var started = chunk.ToolCallStarted;
                    CaptureToolCallLifecycle(toolCallLifecycles, started);
                    await PersistSessionProgressAsync(
                        request.SessionId,
                        progress => progress.ToolStarted = new RoleChatToolStartedProgress
                        {
                            CallId = started.ToolCall.Id,
                            ToolName = started.ToolCall.Name,
                            Presentation = ToolPresentationDescriptors.Snapshot(
                                started.Presentation,
                                started.ToolCall.Name),
                            OperationId = started.OperationId,
                        },
                        CancellationToken.None);
                    streamCt.ThrowIfCancellationRequested();
                }

                if (chunk.ToolCallCompleted != null)
                {
                    await sessionDeltas.FlushAsync(CancellationToken.None);
                    var completed = chunk.ToolCallCompleted;
                    var toolResult = new ToolResultEvent
                    {
                        CallId = completed.CallId,
                        ResultJson = completed.ResultJson,
                        Success = completed.Receipt?.Status == AgentToolReceiptStatus.Success,
                        Error = completed.Error,
                    };
                    if (completed.Receipt != null)
                        toolResult.Receipt = completed.Receipt.Clone();
                    if (toolResults.All(existing => !existing.Equals(toolResult)))
                        toolResults.Add(toolResult);
                    await PersistSessionProgressAsync(
                        request.SessionId,
                        progress => progress.ToolCompleted = new RoleChatToolCompletedProgress
                        {
                            Result = toolResult.Clone(),
                            ToolName = completed.ToolName,
                            OperationId = completed.OperationId,
                            SafeArgumentsJson = ResolveSafeToolCallArguments(
                                completed,
                                toolCallLifecycles,
                                toolCalls.BuildToolCalls()),
                        },
                        CancellationToken.None);
                    MarkToolCallCompleted(toolCallLifecycles, completed);
                    streamCt.ThrowIfCancellationRequested();
                }

                var receipt = chunk.ToolCallCompleted?.Receipt ?? chunk.ToolReceipt;
                if (receipt != null && toolReceipts.All(existing => !existing.Equals(receipt)))
                    toolReceipts.Add(receipt.Clone());
            }

            streamCt.ThrowIfCancellationRequested();
            await sessionDeltas.FlushAsync(streamCt);
        }
        catch (OperationCanceledException) when (streamCt.IsCancellationRequested)
        {
            await PersistCancelledToolCallsAsync(
                request.SessionId,
                sessionDeltas,
                toolCallLifecycles);
            throw;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and
                not ChatToolPostExternalCheckpointException and
                not CommittedStatePublicationException)
        {
            throw new WorkflowIntentStreamingException(CaptureReplay(), ex);
        }

        return CaptureReplay();
    }

    private async Task<bool> TryEstablishWorkflowTurnAuthorityAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        if (!State.Sessions.TryGetValue(request.SessionId, out var trackedSession))
        {
            await EstablishTurnAuthorityAsync(request, trackedSession: null, toolContext, ct);
            ct.ThrowIfCancellationRequested();
            return true;
        }

        if (trackedSession.Completed)
        {
            await DeliverWorkflowCompletionAsync(
                request.SessionId,
                trackedSession.Clone(),
                CancellationToken.None);
            return false;
        }

        if (await TryRequestCheckpointRecoveryAsync(
                request.SessionId,
                trackedSession,
                CancellationToken.None))
        {
            return false;
        }

        await TryFinalizeIncompleteSessionAsync(
            request.SessionId,
            trackedSession.LastProgressSequence);
        return false;
    }

    private async Task<AgentTurnToolCatalog> BuildRequestToolCatalogAsync(
        WorkflowAgentToolScope? scope,
        AgentToolExecutionContext toolContext,
        string? toolCatalogPolicyVersion,
        CancellationToken ct)
    {
        var isCurrentPolicy = WorkflowToolCatalogPolicies.IsCurrent(toolCatalogPolicyVersion);
        if (isCurrentPolicy && scope is null)
        {
            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation,
                "Current workflow tool catalog policy requires an explicit agent tool scope."));
        }

        var budget = isCurrentPolicy
            ? AgentTurnToolCatalogBudget.WorkflowOrAdmin
            : new AgentTurnToolCatalogBudget(int.MaxValue, int.MaxValue);

        var registeredTools = Tools.GetAll()
            .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToArray();
        var allowedStaticNames = (scope is not null &&
                                  (scope.RestrictAllowedToolNames || scope.AllowedToolNames.Count > 0)
                ? scope.AllowedToolNames
                : registeredTools.Select(static tool => tool.Name))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedStaticTools = registeredTools
            .Where(tool => allowedStaticNames.Contains(tool.Name))
            .ToArray();

        var toolSetRefs = (scope?.ToolSetRefs ?? [])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<IAgentTool> requestTools = [];
        if (toolSetRefs.Length > 0)
        {
            if (_toolSetRegistry is null)
            {
                Logger.LogWarning(
                    "Workflow tool catalog restricted because the requested tool-set registry is unavailable. toolSetRefCount={ToolSetRefCount}",
                    toolSetRefs.Length);
                return AgentTurnToolCatalogFactory.RestrictedEmpty(
                    budget,
                    [new AgentProfileTurnDiagnostic(
                        AgentProfileTurnDiagnosticCode.ToolSetUnavailable,
                        "workflow_tool_set_registry_unavailable")]);
            }

            var sources = new List<IAgentToolSource>();
            foreach (var toolSetRef in toolSetRefs)
            {
                ToolSetResolveResult resolved;
                try
                {
                    resolved = _toolSetRegistry.Resolve(toolSetRef);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ToolSetResolutionException(
                        new ToolSetResolveError(
                            ToolSetResolveError.ResolutionFailedCode,
                            toolSetRef,
                            $"Tool set '{toolSetRef}' could not be resolved.",
                            _toolSetRegistry.GetRegisteredNames()),
                        ex);
                }

                if (!resolved.IsSuccess)
                    throw new ToolSetResolutionException(resolved.Error!);

                sources.AddRange(resolved.Sources);
            }

            var discovery = await _toolDiscoveryService
                .DiscoverAsync(
                    sources.Distinct<IAgentToolSource>(ReferenceEqualityComparer.Instance),
                    toolContext,
                    ct)
                .ConfigureAwait(false);
            if (!discovery.IsSuccess)
            {
                Logger.LogWarning(
                    "Workflow tool catalog discovery failed closed. code={FailureCode} tool={ToolName} source={SourceType} conflictingSource={ConflictingSourceType}",
                    discovery.Failure!.Code,
                    discovery.Failure.ToolName,
                    discovery.Failure.SourceType,
                    discovery.Failure.ConflictingSourceType);
                throw new AgentToolDiscoveryException(discovery.Failure);
            }

            requestTools = discovery.Tools;
        }

        var selections = selectedStaticTools
            .Concat(requestTools)
            .Select(static tool => new AgentTurnToolSelection(
                tool,
                AgentTurnToolOrigin.Workflow))
            .ToArray();
        var allowedNames = allowedStaticNames
            .Concat(requestTools.Select(static tool => tool.Name));
        var catalog = new AgentTurnToolCatalog(
            allowedNames,
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null,
            exactToolSelections: selections,
            hasUnresolvedConnectedServiceSelectors: false,
            requiredToolInvocation: null,
            budget: budget);
        Logger.LogInformation(
            "Workflow turn tool catalog frozen. toolCount={ToolCount} schemaBytes={SchemaBytes} digest={CatalogDigest}",
            catalog.Proof.ToolCount,
            catalog.Proof.SchemaBytes,
            catalog.Proof.CatalogDigest);
        return catalog;
    }

    private static AgentToolExecutionContext AddRequestToolsToVisibility(
        AgentToolExecutionContext toolContext,
        IEnumerable<string> toolNames)
    {
        if (!toolContext.ToolVisibility.IsRestricted)
            return toolContext;

        return toolContext with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                toolContext.ToolVisibility.AllowedToolNames!.Concat(toolNames)),
        };
    }

    private static IReadOnlyList<ContentPart> ResolveWorkflowRequestInputParts(ChatRequestEvent request)
    {
        if (request.InputParts.Count > 0)
        {
            var parts = new List<ContentPart>();
            if (!string.IsNullOrWhiteSpace(request.Prompt))
                parts.Add(ContentPart.TextPart(request.Prompt));
            parts.AddRange(ContentPartProtoMapper.FromProtoList(request.InputParts));
            return parts;
        }

        return [ContentPart.TextPart(request.Prompt ?? string.Empty)];
    }

    private static string SanitizeWorkflowFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private static string ResolveWorkflowFailureMessage(Exception exception) =>
        SanitizeWorkflowFailureMessage(
            exception is WorkflowIntentStreamingException streaming
                ? streaming.InnerException?.Message
                : exception.Message);

    private static WorkflowIntentReplayRecord? ResolvePartialReplay(Exception exception) =>
        (exception as WorkflowIntentStreamingException)?.PartialReplay;

    private string ResolveWorkflowModel(
        ChatRequestEvent request,
        LLMControlContext? control = null) =>
        Normalize((control ?? LLMControlContextMapper.FromPayload(request.LlmControl)).ModelOverride) ??
        EffectiveConfig.Model ??
        string.Empty;

    private static ToolResultEvent ToToolResultEvent(RecoveredChatToolResult recovered)
    {
        var result = new ToolResultEvent
        {
            CallId = recovered.ToolCall.Id,
            ResultJson = recovered.Result,
            Success = recovered.Success,
            Error = recovered.Success
                ? string.Empty
                : recovered.Receipt?.ErrorMessage ?? recovered.SafeErrorCode,
        };
        if (recovered.Receipt is not null)
            result.Receipt = recovered.Receipt.Clone();
        return result;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record WorkflowIntentReplayRecord(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ToolCall> ToolCalls,
        IReadOnlyList<AgentToolReceipt> ToolReceipts,
        IReadOnlyList<ToolResultEvent> ToolResults,
        IReadOnlyList<ContentPart> ContentParts,
        TokenUsage? Usage,
        string? Model,
        bool ContentEmitted,
        RoleChatSessionOutcome Outcome,
        string FailureCode,
        string SafeMessage,
        NyxIdAuthorizationRequiredEvent? AuthorizationRequired);

    private sealed class WorkflowIntentStreamingException(
        WorkflowIntentReplayRecord partialReplay,
        Exception innerException)
        : Exception(innerException.Message, innerException)
    {
        public WorkflowIntentReplayRecord PartialReplay { get; } = partialReplay;
    }

    private static WorkflowManagedHandoffOutcome? ToWorkflowManagedHandoffOutcome(
        IReadOnlyList<AgentToolReceipt> toolReceipts)
    {
        var handoff = toolReceipts
            .Select(static receipt => receipt.ManagedWorkflowHandoff)
            .LastOrDefault(static receipt => receipt != null && !string.IsNullOrWhiteSpace(receipt.InvocationId));
        if (handoff == null)
            return null;

        return new WorkflowManagedHandoffOutcome
        {
            ParentActorId = handoff.ParentActorId ?? string.Empty,
            ParentRunId = handoff.ParentRunId ?? string.Empty,
            ParentStepId = handoff.ParentStepId ?? string.Empty,
            InvocationId = handoff.InvocationId ?? string.Empty,
            ChildRunId = handoff.ChildRunId ?? string.Empty,
            StreamTopic = handoff.StreamTopic ?? string.Empty,
        };
    }

    private static WorkflowUsageMetrics? ToWorkflowUsageMetrics(TokenUsage? usage, string? model) =>
        usage == null
            ? null
            : new WorkflowUsageMetrics
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = model ?? string.Empty,
            };

    private static WorkflowUsageMetrics? ToWorkflowUsageMetrics(
        TokenUsagePayload? usage,
        string? model) =>
        usage == null
            ? null
            : new WorkflowUsageMetrics
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = model ?? string.Empty,
            };

    private static TokenUsagePayload? ToTokenUsagePayload(TokenUsage? usage) =>
        usage is null
            ? null
            : new TokenUsagePayload
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };

    private static string ResolveSafeToolCallArguments(
        ToolCallCompletedChunk completed,
        IReadOnlyList<WorkflowToolCallLifecycle> lifecycles,
        IReadOnlyList<ToolCall> toolCalls)
    {
        if (completed.Receipt is not null &&
            ShouldRedactToolCallArguments(completed.CallId, [completed.Receipt]))
        {
            return string.Empty;
        }

        var lifecycle = FindToolCallLifecycle(lifecycles, completed.OperationId, completed.CallId);
        var argumentsJson = lifecycle?.ArgumentsJson ?? toolCalls.LastOrDefault(toolCall =>
            string.Equals(toolCall.Id, completed.CallId, StringComparison.Ordinal))?.ArgumentsJson;
        return SecretScrubber.ScrubJson(argumentsJson);
    }

    private async Task PersistCancelledToolCallsAsync(
        string sessionId,
        RoleChatSessionDeltaBatcher sessionDeltas,
        IReadOnlyList<WorkflowToolCallLifecycle> lifecycles)
    {
        var pending = lifecycles.Where(static lifecycle => !lifecycle.Completed).ToArray();
        if (pending.Length == 0)
            return;

        await sessionDeltas.FlushAsync(CancellationToken.None);
        foreach (var lifecycle in pending)
        {
            var result = new ToolResultEvent
            {
                CallId = lifecycle.CallId,
                Success = false,
                Error = "Tool execution was cancelled.",
            };
            await PersistSessionProgressAsync(
                sessionId,
                progress => progress.ToolCompleted = new RoleChatToolCompletedProgress
                {
                    Result = result,
                    ToolName = lifecycle.ToolName,
                    OperationId = lifecycle.OperationId,
                    SafeArgumentsJson = SecretScrubber.ScrubJson(lifecycle.ArgumentsJson),
                },
                CancellationToken.None);
            lifecycle.Completed = true;
        }
    }

    private static void CaptureToolCallLifecycle(
        List<WorkflowToolCallLifecycle> lifecycles,
        ToolCallStartedChunk started)
    {
        var existing = FindToolCallLifecycle(
            lifecycles,
            started.OperationId,
            started.ToolCall.Id);
        if (existing is null)
        {
            lifecycles.Add(new WorkflowToolCallLifecycle(
                started.OperationId,
                started.ToolCall.Id,
                started.ToolCall.Name,
                started.ToolCall.ArgumentsJson));
            return;
        }

        existing.ToolName = started.ToolCall.Name;
        existing.ArgumentsJson = started.ToolCall.ArgumentsJson;
    }

    private static void MarkToolCallCompleted(
        IReadOnlyList<WorkflowToolCallLifecycle> lifecycles,
        ToolCallCompletedChunk completed)
    {
        var lifecycle = FindToolCallLifecycle(
            lifecycles,
            completed.OperationId,
            completed.CallId);
        if (lifecycle is not null)
            lifecycle.Completed = true;
    }

    private static WorkflowToolCallLifecycle? FindToolCallLifecycle(
        IReadOnlyList<WorkflowToolCallLifecycle> lifecycles,
        string? operationId,
        string? callId)
    {
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            return lifecycles.LastOrDefault(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        }

        return string.IsNullOrWhiteSpace(callId)
            ? null
            : lifecycles.LastOrDefault(candidate =>
                string.Equals(candidate.CallId, callId, StringComparison.Ordinal));
    }

    private static IReadOnlyList<ToolCall> MergeCompletedToolCalls(
        IReadOnlyList<ToolCall> accumulated,
        IReadOnlyList<WorkflowToolCallLifecycle> lifecycles)
    {
        var merged = accumulated.Select(CloneToolCall).ToList();
        foreach (var lifecycle in lifecycles)
        {
            if (!string.IsNullOrWhiteSpace(lifecycle.CallId) &&
                merged.Any(candidate =>
                    string.Equals(candidate.Id, lifecycle.CallId, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(new ToolCall
            {
                Id = lifecycle.CallId,
                Name = lifecycle.ToolName,
                ArgumentsJson = lifecycle.ArgumentsJson,
            });
        }

        return merged;
    }

    private static ToolCall CloneToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };

    private sealed class WorkflowToolCallLifecycle(
        string operationId,
        string callId,
        string toolName,
        string argumentsJson)
    {
        public string OperationId { get; } = operationId;
        public string CallId { get; } = callId;
        public string ToolName { get; set; } = toolName;
        public string ArgumentsJson { get; set; } = argumentsJson;
        public bool Completed { get; set; }
    }

    private sealed class WorkflowToolCallAccumulator
    {
        private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _anonymousCounter;
        private string? _activeAnonymousKey;
        private string? _lastKnownKey;

        public void TrackDelta(ToolCall delta)
        {
            ArgumentNullException.ThrowIfNull(delta);

            var aggregate = ResolveAggregate(delta);
            if (!string.IsNullOrWhiteSpace(delta.Name))
                aggregate.Name = delta.Name;
            if (!string.IsNullOrEmpty(delta.ArgumentsJson))
                aggregate.Arguments.Append(delta.ArgumentsJson);
        }

        public IReadOnlyList<ToolCall> BuildToolCalls()
        {
            var result = new List<ToolCall>(_order.Count);
            foreach (var key in _order)
            {
                if (!_aggregates.TryGetValue(key, out var aggregate))
                    continue;

                result.Add(new ToolCall
                {
                    Id = aggregate.Id,
                    Name = aggregate.Name ?? string.Empty,
                    ArgumentsJson = aggregate.Arguments.ToString(),
                });
            }

            return result;
        }

        private ToolCallAggregate ResolveAggregate(ToolCall delta)
        {
            if (!string.IsNullOrWhiteSpace(delta.Id))
                return ResolveKnownIdAggregate(delta.Id);

            if (!string.IsNullOrWhiteSpace(_lastKnownKey) &&
                _aggregates.TryGetValue(_lastKnownKey, out var knownAggregate))
            {
                return knownAggregate;
            }

            return ResolveAnonymousAggregate();
        }

        private ToolCallAggregate ResolveKnownIdAggregate(string id)
        {
            var knownKey = $"id:{id}";
            if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
            {
                _activeAnonymousKey = null;
                _lastKnownKey = knownKey;
                return promoted;
            }

            _activeAnonymousKey = null;
            if (!_aggregates.TryGetValue(knownKey, out var aggregate))
            {
                aggregate = new ToolCallAggregate(id);
                _aggregates[knownKey] = aggregate;
                _order.Add(knownKey);
            }

            _lastKnownKey = knownKey;
            return aggregate;
        }

        private ToolCallAggregate ResolveAnonymousAggregate()
        {
            if (!string.IsNullOrWhiteSpace(_activeAnonymousKey) &&
                _aggregates.TryGetValue(_activeAnonymousKey, out var activeAggregate))
            {
                return activeAggregate;
            }

            _anonymousCounter++;
            var anonymousKey = $"anon:{_anonymousCounter}";
            var anonymousId = $"stream-tool-call-{_anonymousCounter}";
            var aggregate = new ToolCallAggregate(anonymousId);
            _aggregates[anonymousKey] = aggregate;
            _order.Add(anonymousKey);
            _activeAnonymousKey = anonymousKey;
            return aggregate;
        }

        private bool TryPromoteActiveAnonymousAggregate(
            string knownKey,
            string knownId,
            out ToolCallAggregate aggregate)
        {
            aggregate = default!;

            if (string.IsNullOrWhiteSpace(_activeAnonymousKey))
                return false;

            if (!_aggregates.TryGetValue(_activeAnonymousKey, out var anonymousAggregate))
                return false;

            if (_aggregates.ContainsKey(knownKey))
                return false;

            anonymousAggregate.Id = knownId;
            _aggregates.Remove(_activeAnonymousKey);
            _aggregates[knownKey] = anonymousAggregate;
            ReplaceOrderKey(_activeAnonymousKey, knownKey);
            aggregate = anonymousAggregate;
            return true;
        }

        private void ReplaceOrderKey(string sourceKey, string targetKey)
        {
            for (var index = 0; index < _order.Count; index++)
            {
                if (!string.Equals(_order[index], sourceKey, StringComparison.Ordinal))
                    continue;

                _order[index] = targetKey;
                return;
            }
        }

        private sealed class ToolCallAggregate(string id)
        {
            public string Id { get; set; } = id;
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
