// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs stable ids, lengths, status, and redaction markers for observability
// ─────────────────────────────────────────────────────────────

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
[GAgent("ai.role-agent")]
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent, IVoicePresenceRuntimeStateOwner,
    IChatToolCheckpointPort
{
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const int MaxTrackedSessions = 128;
    private const string OrphanedSessionFailureCode = "SESSION_ORPHANED";
    private const string UncertainSessionFailureCode = "SESSION_OUTCOME_UNCERTAIN";
    private const string CompletionNotificationRetryCallbackPrefix = "role-chat-completion-retry";
    private const int CompletionNotificationRetryInitialDelayMs = 250;
    private const int CompletionNotificationRetryMaxDelayMs = 30_000;
    private static readonly TimeSpan ToolRecoveryPayloadLifetime = TimeSpan.FromHours(24);
    private string _appliedEventModules = string.Empty;
    private string _appliedEventRoutes = string.Empty;
    private IServiceProvider? _appliedModuleServices;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly int _maxTurnDeadlineMs;
    private readonly int _postCommitConfigRefreshTimeoutMs;
    private readonly int _postTurnProcessingTimeoutMs;
    private readonly IChatToolRecoveryPayloadStore? _chatToolRecoveryPayloadStore;
    private readonly ISecretVault? _chatToolRecoverySecretVault;
    // Per-turn NyxID token, stashed before ChatStreamAsync so chartered direct-chat subclasses can
    // hand it to per-turn context consumers (DecorateSystemPrompt has no context param). The base
    // role agent itself never resolves capability overlays — see CurrentTurnNyxIdAccessToken.
    private string? _currentTurnNyxIdAccessToken;

    /// <summary>
    /// The NyxID access token of the turn currently streaming, or null outside a turn. Exposed for
    /// chartered direct-chat subclasses (e.g. the NyxID chat actor's System Skill Overlay seam);
    /// never persisted or logged, cleared when the turn ends.
    /// </summary>
    protected string? CurrentTurnNyxIdAccessToken => _currentTurnNyxIdAccessToken;

    protected virtual TimeProvider ChatRequestTimeProvider => _timeProvider;

    protected override AgentToolApprovalContinuationMode ToolApprovalContinuationMode =>
        AgentToolApprovalContinuationMode.ActorOwned;

    protected override IChatToolCheckpointPort ChatToolCheckpointPort => this;

    public RoleGAgent(
        IAgentToolExecutionPort toolExecutionPort,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort = null,
        TimeProvider? timeProvider = null,
        RoleChatExecutionOptions? chatExecutionOptions = null,
        ISecretVault? chatToolRecoverySecretVault = null)
        : base(
            toolExecutionPort,
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            llmMiddlewares,
            toolSources)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        RemoteToolApprovalPort = remoteToolApprovalPort;
        RemoteToolApprovalNotificationPort = remoteToolApprovalNotificationPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var executionOptions = chatExecutionOptions ?? new RoleChatExecutionOptions();
        _maxTurnDeadlineMs = executionOptions.MaxTurnDeadlineMs;
        _postCommitConfigRefreshTimeoutMs = executionOptions.PostCommitConfigRefreshTimeoutMs;
        _postTurnProcessingTimeoutMs = executionOptions.PostTurnProcessingTimeoutMs;
        _chatToolRecoveryPayloadStore = chatToolRecoverySecretVault is null
            ? null
            : new SecretVaultChatToolRecoveryPayloadStore(chatToolRecoverySecretVault);
        _chatToolRecoverySecretVault = chatToolRecoverySecretVault;
    }

    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    // Refactor (iter15/cluster-028):
    //   Old pattern: workflow artifact facts derived role identity by parsing child actor id prefixes.
    //   New principle: role identity is a typed actor-owned fact persisted on RoleGAgent state.
    public string RoleId { get; private set; } = "";

    protected IRemoteToolApprovalPort? RemoteToolApprovalPort { get; }

    protected IRemoteToolApprovalNotificationPort? RemoteToolApprovalNotificationPort { get; }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        RestoreHistoryFromCommittedSessions();
        await DeliverPendingCompletionNotificationsAsync(ct);
        if (State.PendingApproval is { } pendingApproval)
            await PublishPendingToolApprovalAsync(pendingApproval.Clone(), ct);
        await RequestIncompleteSessionFinalizationAsync(ct);
    }

    public async Task<IReadOnlyList<PreparedChatToolOperation>> PrepareBatchAsync(
        ChatToolBatchIntent batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var payloadStore = _chatToolRecoveryPayloadStore
                           ?? throw new InvalidOperationException(
                               "Durable chat tool recovery payload storage is unavailable.");
        if (string.IsNullOrWhiteSpace(Id) ||
            string.IsNullOrWhiteSpace(batch.SessionId) ||
            !State.Sessions.TryGetValue(batch.SessionId, out var session) ||
            session.Completed)
        {
            throw new InvalidOperationException("The chat tool batch has no active actor-owned session.");
        }

        var checkpoint = session.RecoveryCheckpoint?.Clone() ?? new RoleChatRecoveryCheckpoint();
        if (checkpoint.Stage != RoleChatRecoveryCheckpointStage.ModelReady)
            throw new InvalidOperationException("The chat tool batch cannot be prepared from the current recovery stage.");
        var prepared = batch.Operations
            .Select((intent, index) => PrepareCheckpointOperation(batch, intent, index, checkpoint.Generation))
            .ToArray();
        var expiresAt = ResolveRecoveryPayloadExpiry(checkpoint);
        var expectedGeneration = checkpoint.Generation;
        checkpoint.Generation = expectedGeneration + 1;
        checkpoint.Stage = RoleChatRecoveryCheckpointStage.ToolBatchPrepared;
        checkpoint.Round = batch.Round;
        checkpoint.PendingOperationId = string.Empty;

        foreach (var operation in prepared)
        {
            var argumentsReference = await payloadStore.StoreAsync(
                Id,
                batch.SessionId,
                operation.OperationId,
                ChatToolRecoveryPayloadKind.Arguments,
                operation.ToolCall.ArgumentsJson,
                expiresAt,
                ct).ConfigureAwait(false);
            var intent = new RoleChatToolIntentState
            {
                OperationId = operation.OperationId,
                ToolCallId = operation.ToolCall.Id,
                ToolName = operation.ToolCall.Name,
                ArgumentsSha256 = AgentToolArgumentsDigest.ComputeSha256(operation.ToolCall.ArgumentsJson),
                ReplayPolicy = operation.ReplayPolicy,
                RecoveryContext = operation.ExecutionContext.ToRecoveryPayload(),
                Presentation = operation.Presentation.Clone(),
                ArgumentsReference = argumentsReference,
                Round = operation.Round,
            };
            var existing = checkpoint.ToolIntents
                .Select((candidate, index) => (candidate, index))
                .FirstOrDefault(entry => string.Equals(
                    entry.candidate.OperationId,
                    operation.OperationId,
                    StringComparison.Ordinal));
            if (existing.candidate is null)
                checkpoint.ToolIntents.Add(intent);
            else
                checkpoint.ToolIntents[existing.index] = intent;
        }

        if (prepared.Length > 0)
            checkpoint.RecoveryContext = prepared[0].ExecutionContext.ToRecoveryPayload();
        ValidateCheckpointUpdate(batch.SessionId, expectedGeneration, checkpoint);
        await PersistDomainEventAsync(
            new RoleChatRecoveryCheckpointUpdatedEvent
            {
                SessionId = batch.SessionId,
                ExpectedGeneration = expectedGeneration,
                Checkpoint = checkpoint,
            },
            ct).ConfigureAwait(false);
        return prepared;
    }

    public async Task CommitCompletionAsync(
        PreparedChatToolOperation operation,
        ToolExecutionResult result,
        CancellationToken ct = default)
    {
        try
        {
            await CommitCompletionCoreAsync(operation, result, storedResult: null, ct)
                .ConfigureAwait(false);
        }
        catch (ChatToolPostExternalCheckpointException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChatToolPostExternalCheckpointException(
                ex.Message,
                ex is ChatToolRecoveryPayloadMaterialException,
                ex);
        }
    }

    private async Task CommitCompletionCoreAsync(
        PreparedChatToolOperation operation,
        ToolExecutionResult result,
        StoredChatToolRecoveryResult? storedResult,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var payloadStore = _chatToolRecoveryPayloadStore
                           ?? throw new InvalidOperationException(
                               "Durable chat tool recovery payload storage is unavailable.");
        if (!State.Sessions.TryGetValue(operation.SessionId, out var session) ||
            session.Completed ||
            session.RecoveryCheckpoint is not { } storedCheckpoint)
        {
            throw new InvalidOperationException("The prepared chat tool operation is no longer active.");
        }

        var intent = storedCheckpoint.ToolIntents.SingleOrDefault(candidate =>
            string.Equals(candidate.OperationId, operation.OperationId, StringComparison.Ordinal));
        if (intent is null ||
            !string.Equals(intent.ToolCallId, operation.ToolCall.Id, StringComparison.Ordinal) ||
            !string.Equals(intent.ToolName, operation.ToolCall.Name, StringComparison.Ordinal) ||
            !string.Equals(
                intent.ArgumentsSha256,
                AgentToolArgumentsDigest.ComputeSha256(operation.ToolCall.ArgumentsJson),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The prepared chat tool operation does not match actor state.");
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = ResolveRecoveryPayloadExpiry(storedCheckpoint);
        if (expiresAt <= now)
        {
            throw new ChatToolRecoveryPayloadMaterialException(
                "The chat tool recovery payload lifetime has expired.");
        }

        var sealedResult = storedResult ?? await payloadStore.TryResolveStoredResultAsync(
            Id,
            operation.SessionId,
            operation.OperationId,
            now,
            ct).ConfigureAwait(false);
        var checkpoint = storedCheckpoint.Clone();
        var expectedGeneration = checkpoint.Generation;
        checkpoint.Generation = expectedGeneration + 1;
        var approvalRequired = sealedResult is null &&
                               result.Receipt?.Status == AgentToolReceiptStatus.ApprovalRequired;
        if (!approvalRequired)
        {
            var recoveryResult = sealedResult ?? await payloadStore.StoreResultAsync(
                    Id,
                    operation.SessionId,
                    operation.OperationId,
                    new ChatToolRecoveryResultPayload(
                        result.Result,
                        !result.IsError,
                        result.Receipt?.ErrorCode ?? string.Empty,
                        result.Receipt),
                    expiresAt,
                    ct)
                .ConfigureAwait(false);
            var completion = new RoleChatToolCompletionState
            {
                OperationId = operation.OperationId,
                ResultSha256 = AgentToolArgumentsDigest.ComputeSha256(recoveryResult.Payload.ResultJson),
                CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                ResultReference = recoveryResult.Reference.Clone(),
                Success = recoveryResult.Payload.Success,
                SafeErrorCode = recoveryResult.Payload.SafeErrorCode,
            };
            var existing = checkpoint.ToolCompletions
                .Select((candidate, index) => (candidate, index))
                .FirstOrDefault(entry => string.Equals(
                    entry.candidate.OperationId,
                    operation.OperationId,
                    StringComparison.Ordinal));
            if (existing.candidate is null)
                checkpoint.ToolCompletions.Add(completion);
            else
                checkpoint.ToolCompletions[existing.index] = completion;
        }

        if (storedCheckpoint.Stage == RoleChatRecoveryCheckpointStage.WaitingApproval &&
            !string.Equals(storedCheckpoint.PendingOperationId, operation.OperationId, StringComparison.Ordinal))
        {
            checkpoint.Stage = RoleChatRecoveryCheckpointStage.WaitingApproval;
            checkpoint.PendingOperationId = storedCheckpoint.PendingOperationId;
        }
        else if (approvalRequired)
        {
            checkpoint.Stage = RoleChatRecoveryCheckpointStage.WaitingApproval;
            checkpoint.PendingOperationId = operation.OperationId;
        }
        else
        {
            var completedOperationIds = checkpoint.ToolCompletions
                .Select(static candidate => candidate.OperationId)
                .ToHashSet(StringComparer.Ordinal);
            var hasUnresolvedCurrentBatch = checkpoint.ToolIntents.Any(candidate =>
                candidate.Round == operation.Round &&
                !completedOperationIds.Contains(candidate.OperationId));
            checkpoint.Stage = hasUnresolvedCurrentBatch
                ? RoleChatRecoveryCheckpointStage.ToolBatchPrepared
                : RoleChatRecoveryCheckpointStage.ModelReady;
            checkpoint.PendingOperationId = string.Empty;
        }

        ValidateCheckpointUpdate(operation.SessionId, expectedGeneration, checkpoint);
        var checkpointUpdated = new RoleChatRecoveryCheckpointUpdatedEvent
        {
            SessionId = operation.SessionId,
            ExpectedGeneration = expectedGeneration,
            Checkpoint = checkpoint,
        };
        if (approvalRequired)
        {
            var pending = BuildPendingApproval(operation, intent, result, checkpoint, session);
            await PersistDomainEventsAsync(
            [
                checkpointUpdated,
                new PendingToolApprovalPersistedEvent { Pending = pending },
            ], ct).ConfigureAwait(false);
        }
        else
        {
            await PersistDomainEventAsync(
                checkpointUpdated,
                ct).ConfigureAwait(false);
        }
    }

    private static PreparedChatToolOperation PrepareCheckpointOperation(
        ChatToolBatchIntent batch,
        ChatToolOperationIntent intent,
        int index,
        long checkpointGeneration)
    {
        var material = $"{batch.SessionId}\n{checkpointGeneration}\n{batch.Round}\n{index}\n{intent.ToolCall.Id}";
        var operationId = "tool:v2:operation:" +
                          Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        var request = intent.ExecutionContext.Request with
        {
            CallId = intent.ToolCall.Id,
            OperationId = operationId,
            IdempotencyKey = intent.ReplayPolicy == AgentToolReplayPolicy.IdempotentRetryable
                ? operationId
                : intent.ExecutionContext.Request.IdempotencyKey,
        };
        return new PreparedChatToolOperation(
            batch.SessionId,
            batch.Round,
            operationId,
            new ToolCall
            {
                Id = intent.ToolCall.Id,
                Name = intent.ToolCall.Name,
                ArgumentsJson = intent.ToolCall.ArgumentsJson,
            },
            intent.ExecutionContext with { Request = request },
            intent.ReplayPolicy,
            intent.Presentation.Clone());
    }

    private PendingToolApprovalState BuildPendingApproval(
        PreparedChatToolOperation operation,
        RoleChatToolIntentState intent,
        ToolExecutionResult result,
        RoleChatRecoveryCheckpoint checkpoint,
        RoleChatSessionState session)
    {
        var receipt = result.Receipt;
        if (receipt?.Status != AgentToolReceiptStatus.ApprovalRequired ||
            string.IsNullOrWhiteSpace(receipt.ApprovalRequestId))
        {
            throw new InvalidOperationException("The approval-required tool result has no durable approval identity.");
        }

        var context = AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext) with
        {
            ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
            Request = AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext).Request with
            {
                RequestId = operation.SessionId,
                CallId = operation.ToolCall.Id,
                OperationId = operation.OperationId,
                IdempotencyKey = operation.OperationId,
            },
        };
        return new PendingToolApprovalState
        {
            RequestId = receipt.ApprovalRequestId,
            SessionId = operation.SessionId,
            ToolName = operation.ToolCall.Name,
            ToolCallId = operation.ToolCall.Id,
            ArgumentsJson = operation.ToolCall.ArgumentsJson,
            IsDestructive = receipt.IsDestructive,
            ToolContext = context.ToPayload(),
            ScopeId = session.ScopeId,
            WorkflowLlmContinuation = checkpoint.WorkflowLlmApprovalContinuation?.Clone(),
            OperationId = operation.OperationId,
        };
    }

    private void ValidateCheckpointUpdate(
        string sessionId,
        long expectedGeneration,
        RoleChatRecoveryCheckpoint nextCheckpoint)
    {
        if (!State.Sessions.TryGetValue(sessionId, out var session) ||
            session.Completed ||
            session.RecoveryCheckpoint is not { } currentCheckpoint ||
            currentCheckpoint.Generation != expectedGeneration ||
            nextCheckpoint.Generation != expectedGeneration + 1 ||
            !IsAllowedCheckpointTransition(currentCheckpoint.Stage, nextCheckpoint.Stage) ||
            !IsValidRecoveryCheckpoint(nextCheckpoint))
        {
            throw new InvalidOperationException("The chat recovery checkpoint transition is stale or invalid.");
        }
    }

    private void ValidateCurrentCheckpoint(
        string sessionId,
        long expectedGeneration,
        RoleChatRecoveryCheckpointStage expectedStage)
    {
        if (!State.Sessions.TryGetValue(sessionId, out var session) ||
            session.Completed ||
            session.RecoveryCheckpoint is not { } checkpoint ||
            checkpoint.Generation != expectedGeneration ||
            checkpoint.Stage != expectedStage)
        {
            throw new InvalidOperationException("The chat recovery checkpoint is stale or invalid.");
        }
    }

    private static bool IsAllowedCheckpointTransition(
        RoleChatRecoveryCheckpointStage current,
        RoleChatRecoveryCheckpointStage next) =>
        current switch
        {
            RoleChatRecoveryCheckpointStage.ModelReady =>
                next == RoleChatRecoveryCheckpointStage.ToolBatchPrepared,
            RoleChatRecoveryCheckpointStage.ToolBatchPrepared =>
                next is RoleChatRecoveryCheckpointStage.ToolBatchPrepared or
                    RoleChatRecoveryCheckpointStage.ModelReady or
                    RoleChatRecoveryCheckpointStage.WaitingApproval,
            RoleChatRecoveryCheckpointStage.WaitingApproval =>
                next is RoleChatRecoveryCheckpointStage.WaitingApproval or
                    RoleChatRecoveryCheckpointStage.ContinuationPrepared,
            _ => false,
        };

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell.
    //   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state); transport handles 仅作 volatile process-local lease.
    public bool TryGetVoicePresenceRuntimeState(string moduleName, out VoicePresenceRuntimeState runtimeState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        if (State.VoicePresence.TryGetValue(moduleName, out var stored))
        {
            runtimeState = stored.Clone();
            return true;
        }

        runtimeState = new VoicePresenceRuntimeState();
        return false;
    }

    public bool TryGetVoiceSessionDefaults(string moduleName, out VoiceSessionDefaults defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        if (State.VoiceSessionDefaults.TryGetValue(moduleName, out var stored))
        {
            defaults = stored.Clone();
            return true;
        }

        defaults = new VoiceSessionDefaults();
        return false;
    }

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell.
    //   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state); transport handles 仅作 volatile process-local lease.
    public async Task PersistVoicePresenceRuntimeStateAsync(
        string moduleName,
        VoicePresenceRuntimeState runtimeState,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(runtimeState);

        await PersistDomainEventAsync(new VoicePresenceRuntimeStateChangedEvent
        {
            ModuleName = moduleName,
            State = runtimeState.Clone(),
        }, ct);
    }

    [EventHandler]
    public async Task HandleInitializeRoleAgent(InitializeRoleAgentEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleVoicePresenceEnableRequested(VoicePresenceEnableRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var moduleName = NormalizeModuleExtensionText(command.ModuleName);
        if (string.IsNullOrWhiteSpace(moduleName))
            throw new InvalidOperationException("module_name is required.");

        var defaults = command.VoiceSessionDefaults?.Clone() ?? new VoiceSessionDefaults();
        var runtimeState = CreateEnabledVoicePresenceRuntimeState(defaults, command.RemoteAudioSupport);
        await PersistDomainEventsAsync(
        [
            new VoicePresenceEnabledEvent
            {
                ModuleName = moduleName,
                VoiceSessionDefaults = defaults.Clone(),
                RuntimeState = runtimeState.Clone(),
            },
            new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = moduleName,
                State = runtimeState.Clone(),
            },
        ]);
    }

    /// <summary>Handles tool approval decisions from the frontend or NyxID remote.</summary>
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleToolApprovalDecision(ToolApprovalDecisionEvent evt)
    {
        // ─── Multi-turn continuation ───
        var continuationTurnId = ResolveApprovalContinuationTurnId(evt.ContinuationTurnId);
        var pending = State.PendingApproval;
        var matchesPendingRequest = pending?.RequestId == evt.RequestId;
        var continuationAlreadyTerminal = HasCommittedSessionCompletion(continuationTurnId);
        var pendingSessionAlreadyTerminal = matchesPendingRequest &&
                                            HasCommittedSessionCompletion(pending!.SessionId);
        if (continuationAlreadyTerminal || pendingSessionAlreadyTerminal)
        {
            if (matchesPendingRequest)
            {
                await PersistDomainEventAsync(new ClearPendingApprovalEvent
                {
                    RequestId = evt.RequestId,
                });
            }

            Logger.LogInformation(
                "[{Role}] Ignoring stale approval decision after terminal authority was committed. request={RequestId} continuationSession={ContinuationSessionId} pendingSession={PendingSessionId}",
                RoleName,
                evt.RequestId,
                continuationTurnId,
                pending?.SessionId);
            return;
        }

        if (pending == null || pending.RequestId != evt.RequestId)
        {
            await PersistApprovalRequestNotPendingAsync(continuationTurnId);
            return;
        }

        var approvalResumeTimeoutMs = ResolveLlmTimeoutMs(
            pending.WorkflowLlmContinuation?.TimeoutMs ?? 0);
        using var approvalResumeTimeoutCts =
            CreateTurnDeadlineCancellationSource(approvalResumeTimeoutMs);
        var approvalResumeCt = approvalResumeTimeoutCts.Token;
        AgentToolExecutionOutcome? toolOutcome = null;

        try
        {
            // Cancellation of the durable callback is part of this approval turn and must not
            // run outside the same host-owned deadline as token refresh/tool execution.
            await CancelApprovalTimeoutAsync(pending, approvalResumeCt);
            approvalResumeCt.ThrowIfCancellationRequested();

            if (!evt.Approved)
            {
                await PersistApprovalTerminalFailureThenClearPendingAsync(
                    pending,
                    "approval_denied",
                    string.IsNullOrWhiteSpace(evt.Reason)
                        ? "Tool approval denied."
                        : evt.Reason,
                    continuationTurnId);
                return;
            }

            Logger.LogInformation(
                "[{Role}] Tool approval APPROVED. Executing tool={Tool} request={RequestId}",
                RoleName, pending.ToolName, pending.RequestId);

            // Refactor (issue1414/cluster-004):
            //   Old pattern: pending approval state could rehydrate stable tool/caller context from metadata.
            //   New principle: typed ToolContext/LlmControl are the only tool control authority.
            // Refactor (issue1253-first):
            //   Old pattern: Approval resume rebuilt control context from a durable annotation bag.
            //   New principle: Use typed pending.ToolContext only; metadata is never a control source.
            var pendingToolContext = ResolvePendingToolContext(pending);
            if (State.Sessions.TryGetValue(pending.SessionId, out var pendingSession) &&
                pendingSession.RecoveryCheckpoint is { } pendingCheckpoint)
            {
                var recoveredPendingContext = await TryResolveRecoveryExecutionContextAsync(
                    pendingCheckpoint,
                    approvalResumeCt).ConfigureAwait(false);
                if (pendingCheckpoint.RequiresRuntimeCredential && recoveredPendingContext is null)
                {
                    throw new InvalidOperationException(
                        "The approved tool credential can no longer be resolved.");
                }

                pendingToolContext = recoveredPendingContext ?? pendingToolContext;
            }
            pendingToolContext = pendingToolContext with
            {
                ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
                Request = pendingToolContext.Request with
                {
                    RequestId = pending.SessionId,
                    CallId = pending.ToolCallId,
                    OperationId = pending.OperationId,
                    IdempotencyKey = pending.OperationId,
                },
            };
            var approvedExecution = await ResolveApprovedToolExecutionAsync(
                    pending,
                    pendingToolContext,
                    approvalResumeCt)
                .WaitAsync(approvalResumeCt);
            pendingToolContext = approvedExecution.ExecutionContext;
            using (AgentToolContextScope.Push(pendingToolContext))
            {
                var payloadStore = _chatToolRecoveryPayloadStore
                                   ?? throw new InvalidOperationException(
                                       "Durable chat tool recovery payload storage is unavailable.");
                var storedResult = await payloadStore.TryResolveStoredResultAsync(
                    Id,
                    pending.SessionId,
                    pending.OperationId,
                    _timeProvider.GetUtcNow(),
                    approvalResumeCt).ConfigureAwait(false);
                if (storedResult is null)
                {
                    toolOutcome = await _toolExecutionPort.ExecuteAsync(
                            new AgentToolExecutionRequest(
                                approvedExecution.Tool,
                                pending.ArgumentsJson,
                                pendingToolContext.WithCallId(pending.ToolCallId),
                                AgentToolApprovalContinuationMode.ActorOwned,
                                new AgentToolApprovalGrant(
                                    pendingToolContext.ExecutionOwner.Clone(),
                                    pending.RequestId,
                                    pendingToolContext.Request.RequestId ?? string.Empty,
                                    pending.ToolName,
                                    pending.ToolCallId,
                                    AgentToolArgumentsDigest.ComputeSha256(pending.ArgumentsJson)),
                                AgentToolExecutionAttemptKind.ActorRecovery),
                            approvalResumeCt)
                        .WaitAsync(approvalResumeCt);
                    if (string.Equals(
                            toolOutcome.FailureCode,
                            "outcome_uncertain",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await TryPersistApprovalOutcomeUncertainThenClearPendingAsync(
                            pending,
                            string.IsNullOrWhiteSpace(toolOutcome.SafeMessage)
                                ? "The outcome of the approved tool operation could not be proven."
                                : toolOutcome.SafeMessage);
                        return;
                    }

                    if (toolOutcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                        AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete))
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(toolOutcome.SafeMessage)
                                ? toolOutcome.FailureCode
                                : toolOutcome.SafeMessage);
                    }

                    storedResult = await payloadStore.StoreResultAsync(
                        Id,
                        pending.SessionId,
                        pending.OperationId,
                        new ChatToolRecoveryResultPayload(
                            toolOutcome.ResultJson,
                            true,
                            toolOutcome.FailureCode,
                            toolOutcome.Receipt),
                        ResolveRecoveryPayloadExpiry(State.Sessions[pending.SessionId].RecoveryCheckpoint!),
                        approvalResumeCt).ConfigureAwait(false);

                    Logger.LogInformation(
                        "[{Role}] Tool executed. result length={Len} request={RequestId}",
                        RoleName, toolOutcome.ResultJson.Length, pending.RequestId);
                }
                else
                {
                    Logger.LogInformation(
                        "[{Role}] Adopted the deterministic approved-tool result. request={RequestId} operation={OperationId}",
                        RoleName, pending.RequestId, pending.OperationId);
                }

                if (!storedResult.Payload.Success)
                {
                    throw new ChatToolRecoveryPayloadMaterialException(
                        "The deterministic approved-tool result is not a successful terminal result.");
                }

                var continuationCheckpoint = BuildApprovalContinuationCheckpoint(
                    pending,
                    storedResult,
                    continuationTurnId);
                await PersistDomainEventsAsync(
                [
                    new RoleChatRecoveryCheckpointUpdatedEvent
                    {
                        SessionId = pending.SessionId,
                        ExpectedGeneration = continuationCheckpoint.ExpectedGeneration,
                        Checkpoint = continuationCheckpoint.Checkpoint,
                    },
                    new ClearPendingApprovalEvent { RequestId = pending.RequestId },
                ], approvalResumeCt);
                approvalResumeCt.ThrowIfCancellationRequested();

                Logger.LogInformation(
                    "[{Role}] Dispatching continuation chat. request={RequestId}",
                    RoleName, pending.RequestId);

                var continuationRequest = new RoleChatRecoveryContinuationRequested
                {
                    SessionId = pending.SessionId,
                    OperationId = continuationCheckpoint.Checkpoint.PendingOperationId,
                    ExpectedCheckpointGeneration = continuationCheckpoint.Checkpoint.Generation,
                };
                approvalResumeCt.ThrowIfCancellationRequested();
                await PublishAsync(
                    continuationRequest,
                    TopologyAudience.Self,
                    approvalResumeCt).WaitAsync(approvalResumeCt);
                approvalResumeCt.ThrowIfCancellationRequested();

                Logger.LogInformation(
                    "[{Role}] Continuation dispatched. request={RequestId}",
                    RoleName, pending.RequestId);
            }
        }
        catch (ChatToolRecoveryPayloadMaterialException ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Approved-tool recovery material is permanently unavailable. request={RequestId} session={SessionId}",
                RoleName,
                pending.RequestId,
                pending.SessionId);
            await TryPersistApprovalOutcomeUncertainThenClearPendingAsync(
                pending,
                "The durable result required to recover the approved tool operation is unavailable or invalid.");
        }
        catch (Exception ex) when (toolOutcome is { TerminalInvoked: false, Retryable: true })
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Approval continuation remains pending after retryable pre-terminal failure. request={RequestId} failureCode={FailureCode}",
                RoleName,
                pending.RequestId,
                toolOutcome!.FailureCode);
            throw;
        }
        catch (Exception ex) when (HasCommittedSessionCompletion(continuationTurnId))
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Approval post-commit work failed after terminal authority was acquired. request={RequestId} session={SessionId}",
                RoleName,
                pending.RequestId,
                continuationTurnId);
        }
        catch (Exception ex) when (approvalResumeTimeoutCts.IsCancellationRequested)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Approval processing exceeded the host deadline. request={RequestId} timeoutMs={TimeoutMs}",
                RoleName,
                pending.RequestId,
                approvalResumeTimeoutMs);

            await TryPersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_tool_timeout",
                "The approval continuation exceeded its deadline. Please try again.",
                continuationTurnId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "[{Role}] Approval continuation FAILED. request={RequestId}",
                RoleName, pending.RequestId);

            await TryPersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_continuation_failed",
                "The approval continuation failed. Please try again.",
                continuationTurnId);

            throw; // Re-throw so the SSE endpoint sees the error
        }
    }

    // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
    //   Old pattern: local timeout called a remote approval handler that blocked this actor turn with polling.
    //   New principle: submit once, persist remote binding, and resume from self status-check events.
    /// <summary>Handles local approval timeout by submitting one remote approval request and scheduling status continuation.</summary>
    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleToolApprovalTimeout(ToolApprovalTimeoutFiredEvent evt)
    {
        var pending = State.PendingApproval;
        if (pending == null || pending.RequestId != evt.RequestId)
            return;

        Logger.LogInformation(
            "[{Role}] Tool approval local timeout. Escalating to NyxID remote. request={RequestId}",
            RoleName, evt.RequestId);

        if (RemoteToolApprovalPort == null)
        {
            Logger.LogWarning("[{Role}] No remote approval port configured. Clearing pending. request={RequestId}",
                RoleName, evt.RequestId);
            await PersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_timeout",
                "Tool approval timed out and no remote approval port is configured.");
            return;
        }

        try
        {
            var pendingToolContext = ResolvePendingToolContext(pending);
            var notificationSupport = await CheckRemoteApprovalNotificationSupportAsync(pendingToolContext);
            if (!notificationSupport.Supported)
            {
                await PersistApprovalTerminalFailureThenClearPendingAsync(
                    pending,
                    "approval_unsupported_channel",
                    notificationSupport.Reason ?? "Remote approval notification is not supported for this delivery target.");
                return;
            }

            var request = new RemoteToolApprovalRequest(
                pending.RequestId,
                pending.ToolName,
                pending.ToolCallId,
                pending.ArgumentsJson,
                ToolApprovalMode.Auto,
                pending.IsDestructive);
            var submission = await RemoteToolApprovalPort.SubmitAsync(request, CancellationToken.None);
            var callbackId = BuildRemoteApprovalStatusCallbackId(pending.RequestId, submission.RemoteApprovalId, 1);
            await PersistDomainEventAsync(new RemoteToolApprovalSubmittedEvent
            {
                RequestId = pending.RequestId,
                RemoteApprovalId = submission.RemoteApprovalId,
                StatusCheckAttempt = 1,
                ExpiresAtUnixMs = ResolveRemoteApprovalDeadlineUnixMs(submission.ExpiresAt),
            });

            await TryNotifyRemoteApprovalSubmittedAsync(request, submission, pendingToolContext);

            await ScheduleRemoteApprovalStatusCheckAsync(
                pending.RequestId,
                pending.SessionId,
                submission.RemoteApprovalId,
                1,
                callbackId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] NyxID remote approval submit failed. request={RequestId}",
                RoleName, evt.RequestId);
            await PersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_timeout",
                "Remote approval submission failed. Please try again.");
        }
    }

    private async Task<RemoteToolApprovalNotificationSupport> CheckRemoteApprovalNotificationSupportAsync(
        AgentToolExecutionContext toolContext)
    {
        var notificationPort = RemoteToolApprovalNotificationPort;
        if (notificationPort is null)
        {
            return RemoteToolApprovalNotificationSupport.SupportedResult;
        }

        return await notificationPort.CheckSupportAsync(toolContext, CancellationToken.None);
    }

    private async Task TryNotifyRemoteApprovalSubmittedAsync(
        RemoteToolApprovalRequest request,
        RemoteToolApprovalSubmission submission,
        AgentToolExecutionContext toolContext)
    {
        var notificationPort = RemoteToolApprovalNotificationPort;
        if (notificationPort is null)
            return;

        try
        {
            await notificationPort.NotifyAsync(
                new RemoteToolApprovalNotification(request, submission, toolContext),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Remote approval notification failed. request={RequestId}, remote={RemoteApprovalId}",
                RoleName,
                request.RequestId,
                submission.RemoteApprovalId);
        }
    }

    // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
    //   Old pattern: remote approval status was polled inside one tool-call stack with Task.Delay.
    //   New principle: each status read is one actor self-message turn with request/remote-id stale checks.
    /// <summary>Handles one remote approval status check turn.</summary>
    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRemoteApprovalStatusCheck(ToolApprovalRemoteStatusCheckFiredEvent evt)
    {
        var pending = State.PendingApproval;
        if (pending == null ||
            pending.RequestId != evt.RequestId ||
            pending.SessionId != evt.SessionId ||
            pending.RemoteApprovalId != evt.RemoteApprovalId ||
            pending.RemoteStatusCheckAttempt != evt.Attempt)
        {
            return;
        }

        if (RemoteToolApprovalPort == null)
        {
            await PersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_timeout",
                "Tool approval timed out and no remote approval port is configured.");
            return;
        }

        RemoteToolApprovalStatusSnapshot snapshot;
        try
        {
            snapshot = await RemoteToolApprovalPort.GetStatusAsync(
                new RemoteToolApprovalStatusQuery(
                    pending.RequestId,
                    pending.RemoteApprovalId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] NyxID remote approval status check failed. request={RequestId}",
                RoleName, evt.RequestId);
            snapshot = new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Unknown,
                "Remote approval status check failed. Please try again.");
        }

        switch (snapshot.Status)
        {
            case RemoteToolApprovalStatus.Approved:
                await HandleToolApprovalDecision(new ToolApprovalDecisionEvent
                {
                    RequestId = pending.RequestId,
                    ContinuationTurnId = ResolveApprovalContinuationTurnId(null),
                    Approved = true,
                    Reason = snapshot.Reason ?? "Approved via NyxID remote.",
                });
                return;

            case RemoteToolApprovalStatus.Rejected:
            case RemoteToolApprovalStatus.Expired:
            case RemoteToolApprovalStatus.Cancelled:
                await PersistApprovalTerminalFailureThenClearPendingAsync(
                    pending,
                    snapshot.Status switch
                    {
                        RemoteToolApprovalStatus.Expired => "approval_timeout",
                        RemoteToolApprovalStatus.Cancelled => "approval_cancelled",
                        _ => "approval_denied",
                    },
                    string.IsNullOrWhiteSpace(snapshot.Reason)
                        ? "Tool approval timed out or was denied remotely."
                        : snapshot.Reason);
                return;

            case RemoteToolApprovalStatus.Pending:
            case RemoteToolApprovalStatus.Unknown:
                if (HasRemoteApprovalTimedOut(pending, evt.Attempt))
                {
                    await PersistApprovalTerminalFailureThenClearPendingAsync(
                        pending,
                        "approval_timeout",
                        string.IsNullOrWhiteSpace(snapshot.Reason)
                            ? "NyxID remote approval timed out."
                            : snapshot.Reason);
                    return;
                }

                var nextAttempt = evt.Attempt + 1;
                var callbackId = BuildRemoteApprovalStatusCallbackId(pending.RequestId, pending.RemoteApprovalId, nextAttempt);
                await PersistDomainEventAsync(new RemoteToolApprovalSubmittedEvent
                {
                    RequestId = pending.RequestId,
                    RemoteApprovalId = pending.RemoteApprovalId,
                    StatusCheckAttempt = nextAttempt,
                    ExpiresAtUnixMs = snapshot.ExpiresAt?.ToUnixTimeMilliseconds() ??
                                      pending.RemoteApprovalExpiresAtUnixMs,
                });
                await ScheduleRemoteApprovalStatusCheckAsync(
                    pending.RequestId,
                    pending.SessionId,
                    pending.RemoteApprovalId,
                    nextAttempt,
                    callbackId);
                return;
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleSystemSkillOverlayRefresh(SystemSkillOverlayRefreshFiredEvent evt)
    {
        // Retired (issue #2498): the overlay is now sourced by the host-level ISystemSkillOverlayProvider,
        // not materialized per-actor. This no-op absorbs any durable refresh timeout still queued for
        // grains activated before this change; no new refreshes are ever scheduled.
        return Task.CompletedTask;
    }

    // ─── Approval continuation constants ───

    private const int ApprovalLocalTimeoutSeconds = 15;
    private const int RemoteApprovalStatusCheckSeconds = 2;
    private const int RemoteApprovalWindowSeconds = 45;
    private const int RemoteApprovalMaxStatusCheckAttempts =
        (RemoteApprovalWindowSeconds + RemoteApprovalStatusCheckSeconds - 1) / RemoteApprovalStatusCheckSeconds;

    // ─── Approval helpers ───

    protected PendingToolApprovalState? DetectPendingApproval(
        IReadOnlyList<AgentToolReceipt> toolReceipts,
        IReadOnlyList<ToolCall> toolCalls,
        ChatRequestEvent request)
    {
        var receipt = toolReceipts
            .LastOrDefault(static candidate =>
                candidate.Status == AgentToolReceiptStatus.ApprovalRequired &&
                !string.IsNullOrWhiteSpace(candidate.ApprovalRequestId));
        if (receipt is null)
            return null;

        var intent = State.Sessions.TryGetValue(request.SessionId, out var session)
            ? session.RecoveryCheckpoint?.ToolIntents.LastOrDefault(candidate =>
                string.Equals(candidate.ToolCallId, receipt.CallId, StringComparison.Ordinal))
            : null;
        var persistedContext = intent is null
            ? ResolveToolContext(
                request,
                request.SessionId ?? string.Empty,
                receipt.CallId ?? string.Empty)
            : AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext) with
            {
                Request = AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext).Request with
                {
                    CallId = intent.ToolCallId,
                    OperationId = intent.OperationId,
                    IdempotencyKey = intent.ReplayPolicy == AgentToolReplayPolicy.IdempotentRetryable
                        ? intent.OperationId
                        : AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext)
                            .Request.IdempotencyKey,
                },
                ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
            };

        return new PendingToolApprovalState
        {
            RequestId = receipt.ApprovalRequestId,
            SessionId = request.SessionId ?? string.Empty,
            ToolName = receipt.ToolName ?? string.Empty,
            ToolCallId = receipt.CallId ?? string.Empty,
            ArgumentsJson = ResolveToolArguments(toolCalls, receipt.CallId),
            IsDestructive = receipt.IsDestructive,
            ToolContext = persistedContext.ToPayload(),
            ScopeId = request.ScopeId ?? string.Empty,
            OperationId = intent?.OperationId ?? string.Empty,
        };
    }

    protected async Task SuspendForToolApprovalAsync(
        PendingToolApprovalState pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ct.ThrowIfCancellationRequested();
        if (!MatchesPendingApproval(State.PendingApproval, pending))
        {
            await PersistDomainEventAsync(new PendingToolApprovalPersistedEvent { Pending = pending }, ct);
            ct.ThrowIfCancellationRequested();
        }
        await PublishPendingToolApprovalAsync(pending, ct);
    }

    private async Task PublishPendingToolApprovalAsync(
        PendingToolApprovalState pending,
        CancellationToken ct)
    {
        await PublishAsync(new ToolApprovalRequestEvent
        {
            RequestId = pending.RequestId,
            SessionId = pending.SessionId,
            ToolName = pending.ToolName,
            ToolCallId = pending.ToolCallId,
            ArgumentsJson = pending.ArgumentsJson,
            IsDestructive = pending.IsDestructive,
            ApprovalMode = "yield",
            TimeoutSeconds = ApprovalLocalTimeoutSeconds,
        }, TopologyAudience.Parent, ct);
        ct.ThrowIfCancellationRequested();
        await ScheduleApprovalTimeoutAsync(pending, ct);
        ct.ThrowIfCancellationRequested();
    }

    private static bool MatchesPendingApproval(
        PendingToolApprovalState? current,
        PendingToolApprovalState candidate) =>
        current is not null &&
        string.Equals(current.RequestId, candidate.RequestId, StringComparison.Ordinal) &&
        string.Equals(current.SessionId, candidate.SessionId, StringComparison.Ordinal) &&
        string.Equals(current.OperationId, candidate.OperationId, StringComparison.Ordinal) &&
        string.Equals(current.ToolCallId, candidate.ToolCallId, StringComparison.Ordinal) &&
        Equals(current.WorkflowLlmContinuation, candidate.WorkflowLlmContinuation);

    protected virtual Task<(IAgentTool Tool, AgentToolExecutionContext ExecutionContext)>
        ResolveApprovedToolExecutionAsync(
        PendingToolApprovalState pending,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var tool = Tools.Get(pending.ToolName)
                   ?? throw new InvalidOperationException($"Tool '{pending.ToolName}' not found");
        return Task.FromResult((tool, toolContext));
    }

    protected virtual Task OnApprovalTerminalFailureAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static string ResolveToolArguments(IReadOnlyList<ToolCall> toolCalls, string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId))
            return "{}";

        var toolCall = toolCalls.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, callId, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(toolCall?.ArgumentsJson)
            ? "{}"
            : toolCall.ArgumentsJson;
    }

    // Stored lease from the last scheduled timeout, kept in-memory for cancellation.
    // Not persisted — if the actor deactivates, the durable callback runtime handles
    // re-delivery; the actor's HandleToolApprovalTimeout idempotently checks pending state.
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _approvalTimeoutLease;

    private async Task ScheduleApprovalTimeoutAsync(
        PendingToolApprovalState pending,
        CancellationToken ct = default)
    {
        var callbackId = $"tool-approval-timeout-{pending.RequestId}";
        pending.TimeoutCallbackId = callbackId;
        try
        {
            _approvalTimeoutLease = await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromSeconds(ApprovalLocalTimeoutSeconds),
                new ToolApprovalTimeoutFiredEvent
                {
                    RequestId = pending.RequestId,
                    SessionId = pending.SessionId,
                },
                ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to schedule approval timeout", RoleName);
        }
    }

    private async Task CancelApprovalTimeoutAsync(
        PendingToolApprovalState pending,
        CancellationToken ct)
    {
        if (_approvalTimeoutLease == null)
            return;

        try
        {
            await CancelDurableCallbackAsync(_approvalTimeoutLease, ct);
            _approvalTimeoutLease = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to cancel approval timeout", RoleName);
        }
    }

    private static string BuildRemoteApprovalStatusCallbackId(
        string requestId,
        string remoteApprovalId,
        int attempt) =>
        $"tool-approval-remote-status-{requestId}-{remoteApprovalId}-{attempt}";

    private static long ResolveRemoteApprovalDeadlineUnixMs(DateTimeOffset? expiresAt)
    {
        return expiresAt?.ToUnixTimeMilliseconds() ??
               DateTimeOffset.UtcNow.AddSeconds(RemoteApprovalWindowSeconds).ToUnixTimeMilliseconds();
    }

    private static bool HasRemoteApprovalTimedOut(PendingToolApprovalState pending, int currentAttempt)
    {
        if (currentAttempt >= RemoteApprovalMaxStatusCheckAttempts)
            return true;

        return pending.RemoteApprovalExpiresAtUnixMs > 0 &&
               DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= pending.RemoteApprovalExpiresAtUnixMs;
    }

    private async Task ScheduleRemoteApprovalStatusCheckAsync(
        string requestId,
        string sessionId,
        string remoteApprovalId,
        int attempt,
        string callbackId)
    {
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromSeconds(RemoteApprovalStatusCheckSeconds),
                new ToolApprovalRemoteStatusCheckFiredEvent
                {
                    RequestId = requestId,
                    SessionId = sessionId,
                    RemoteApprovalId = remoteApprovalId,
                    Attempt = attempt,
                });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to schedule remote approval status check", RoleName);
        }
    }

    private static string BuildContinuationPrompt(PendingToolApprovalState pending, string? toolResult)
    {
        return $"[System continuation] The user approved the tool call '{pending.ToolName}'. " +
               $"The tool was executed and returned the following result:\n\n" +
               $"{toolResult ?? "(no output)"}\n\n" +
               "Please continue with the original task based on this result.";
    }

    private static LLMControlContextPayload ToRecoverySafeLlmControl(
        LLMControlContextPayload? llmControl)
    {
        var source = LLMControlContextMapper.FromPayload(llmControl);
        return new LLMControlContext(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            source.ModelOverride,
            source.NyxIdRoutePreference,
            source.MaxToolRoundsOverride,
            source.UserMemoryPrompt).ToPayload();
    }

    private static bool HasRuntimeCredential(
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext) =>
        !string.IsNullOrWhiteSpace(llmControl.NyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(llmControl.NyxIdOrgToken) ||
        !string.IsNullOrWhiteSpace(llmControl.SenderNyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(toolContext.Credentials.NyxIdOrgToken) ||
        !string.IsNullOrWhiteSpace(toolContext.Credentials.SenderNyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(toolContext.Credentials.SourceReadableNyxIdAccessToken);

    private static DateTimeOffset ResolveRecoveryPayloadExpiry(
        RoleChatRecoveryCheckpoint checkpoint)
    {
        if (checkpoint.PayloadExpiresAtUnixMs <= 0)
        {
            throw new InvalidOperationException(
                "The chat recovery checkpoint has no deterministic payload expiry.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(checkpoint.PayloadExpiresAtUnixMs);
    }

    private (long ExpectedGeneration, RoleChatRecoveryCheckpoint Checkpoint)
        BuildApprovalContinuationCheckpoint(
            PendingToolApprovalState pending,
            StoredChatToolRecoveryResult storedResult,
            string continuationSessionId)
    {
        if (!State.Sessions.TryGetValue(pending.SessionId, out var session) ||
            session.Completed ||
            session.RecoveryCheckpoint is not { } storedCheckpoint ||
            storedCheckpoint.Stage != RoleChatRecoveryCheckpointStage.WaitingApproval ||
            string.IsNullOrWhiteSpace(pending.OperationId) ||
            !string.Equals(
                storedCheckpoint.PendingOperationId,
                pending.OperationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The approved tool operation no longer matches the actor-owned checkpoint.");
        }

        var intent = storedCheckpoint.ToolIntents.SingleOrDefault(candidate =>
            string.Equals(candidate.OperationId, pending.OperationId, StringComparison.Ordinal));
        if (intent is null)
            throw new InvalidOperationException("The approved tool operation intent is unavailable.");

        ValidateCurrentCheckpoint(
            pending.SessionId,
            storedCheckpoint.Generation,
            RoleChatRecoveryCheckpointStage.WaitingApproval);

        var checkpoint = storedCheckpoint.Clone();
        var expectedGeneration = checkpoint.Generation;
        checkpoint.Generation++;
        checkpoint.Stage = RoleChatRecoveryCheckpointStage.ContinuationPrepared;
        checkpoint.PendingOperationId = pending.OperationId;
        checkpoint.ContinuationSessionId = continuationSessionId;
        checkpoint.WorkflowLlmApprovalContinuation = pending.WorkflowLlmContinuation?.Clone();
        var completion = new RoleChatToolCompletionState
        {
            OperationId = pending.OperationId,
            ResultSha256 = AgentToolArgumentsDigest.ComputeSha256(storedResult.Payload.ResultJson),
            CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ResultReference = storedResult.Reference.Clone(),
            Success = storedResult.Payload.Success,
            SafeErrorCode = storedResult.Payload.SafeErrorCode,
        };
        var existingIndex = checkpoint.ToolCompletions
            .Select((candidate, index) => (candidate, index))
            .FirstOrDefault(entry => string.Equals(
                entry.candidate.OperationId,
                pending.OperationId,
                StringComparison.Ordinal));
        if (existingIndex.candidate is null)
            checkpoint.ToolCompletions.Add(completion);
        else
            checkpoint.ToolCompletions[existingIndex.index] = completion;

        ValidateCheckpointUpdate(pending.SessionId, expectedGeneration, checkpoint);
        return (expectedGeneration, checkpoint);
    }

    private static IReadOnlyDictionary<string, string> ScrubPendingApprovalMetadata(
        IReadOnlyDictionary<string, string>? metadata) =>
        AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata);

    private AgentToolExecutionContext ResolveToolContext(
        ChatRequestEvent request,
        string requestId,
        string toolCallId)
    {
        // Refactor (issue1414/cluster-004):
        //   Old pattern: active ChatRequestEvent.Metadata could be promoted into tool execution control.
        //   New principle: active request control comes only from typed ToolContext/LlmControl fields.
        var context = AgentToolExecutionContextMapper.FromPayload(request.ToolContext);

        context = LLMControlContextMapper.FromPayload(request.LlmControl).ToToolContext(context);
        context = context with
        {
            Request = context.Request with
            {
                RequestId = NormalizeToolContextValue(requestId) ?? context.Request.RequestId,
                CallId = NormalizeToolContextValue(toolCallId) ?? context.Request.CallId,
            },
            Credentials = AgentToolCredentials.Empty,
            ExternalMetadata = ScrubPendingApprovalMetadata(context.ExternalMetadata),
            ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
        };

        return context;
    }

    private static AgentToolExecutionContext ResolvePendingToolContext(PendingToolApprovalState pending)
    {
        // Refactor (iter290/cluster-002-invocation-trusted-context-metadata-bag):
        //   Old pattern: pending approval Metadata remained the primary resume context.
        //   New principle: pending ToolContext is authoritative; missing legacy context resolves to empty.
        var context = pending.ToolContext != null
            ? AgentToolExecutionContextMapper.FromPayload(pending.ToolContext)
            : AgentToolExecutionContext.Empty;

        return context with
        {
            Credentials = AgentToolCredentials.Empty,
            ExternalMetadata = ScrubPendingApprovalMetadata(context.ExternalMetadata),
        };
    }

    private static string? NormalizeToolContextValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    // ─── Pending approval state transitions ───

    private static RoleGAgentState ApplyPendingApproval(
        RoleGAgentState current,
        PendingToolApprovalPersistedEvent evt)
    {
        var next = current.Clone();
        next.PendingApproval = evt.Pending;
        return next;
    }

    private static RoleGAgentState ApplyClearPendingApproval(
        RoleGAgentState current,
        ClearPendingApprovalEvent evt)
    {
        if (current.PendingApproval == null)
            return current;
        if (!string.IsNullOrWhiteSpace(evt.RequestId) &&
            current.PendingApproval.RequestId != evt.RequestId)
            return current;

        var next = current.Clone();
        next.PendingApproval = null;
        return next;
    }

    private static RoleGAgentState ApplyRemoteApprovalSubmitted(
        RoleGAgentState current,
        RemoteToolApprovalSubmittedEvent evt)
    {
        if (current.PendingApproval == null ||
            current.PendingApproval.RequestId != evt.RequestId)
        {
            return current;
        }

        var next = current.Clone();
        next.PendingApproval.RemoteApprovalId = evt.RemoteApprovalId;
        next.PendingApproval.RemoteStatusCheckAttempt = evt.StatusCheckAttempt;
        next.PendingApproval.RemoteApprovalExpiresAtUnixMs = evt.ExpiresAtUnixMs;
        return next;
    }

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell.
    //   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state); transport handles 仅作 volatile process-local lease.
    private static RoleGAgentState ApplyVoicePresenceRuntimeStateChanged(
        RoleGAgentState current,
        VoicePresenceRuntimeStateChangedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ModuleName))
            return current;

        var next = current.Clone();
        next.VoicePresence[evt.ModuleName] = evt.State?.Clone() ?? new VoicePresenceRuntimeState();
        return next;
    }

    private static RoleGAgentState ApplyVoicePresenceEnabled(
        RoleGAgentState current,
        VoicePresenceEnabledEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ModuleName))
            return current;

        var moduleName = NormalizeModuleExtensionText(evt.ModuleName);
        var next = current.Clone();
        next.VoiceSessionDefaults[moduleName] = evt.VoiceSessionDefaults?.Clone() ?? new VoiceSessionDefaults();
        next.VoicePresence[moduleName] = evt.RuntimeState?.Clone() ?? new VoicePresenceRuntimeState
        {
            Initialized = true,
            RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
        };
        // Mount the voice runtime module so VoiceModuleSignal (session-lease / transport-attach /
        // provider events) actually reaches a handler. Without this the capability projection
        // materializes (Initialized=true, pre-check passes) but the pluggable VoicePresenceModule is
        // never in the actor's pipeline, so the dispatched lease request matches no handler, is
        // silently dropped, and /ws/voice times out after 5s. The next state apply diffs
        // _appliedEventModules and re-runs SetModulesAsync to mount it.
        next.EventModules = AppendModuleExtension(next.EventModules, moduleName);
        return next;
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<InitializeRoleAgentEvent>(ApplyInitializeRoleAgent)
            .On<SystemSkillOverlayMaterializedEvent>(ApplySystemSkillOverlayMaterialized)
            .On<RoleChatSessionStartedEvent>(ApplyChatSessionStarted)
            .On<RoleChatRecoveryCheckpointUpdatedEvent>(ApplyChatRecoveryCheckpointUpdated)
            .On<RoleChatSessionProgressedEvent>(ApplyChatSessionProgressed)
            .On<AgentProfileTurnAuthorityCommittedEvent>(ApplyAgentProfileTurnAuthorityCommitted)
            .On<RoleChatSessionCompletedEvent>(ApplyChatSessionCompleted)
            .On<RoleChatCompletionNotificationRetryScheduledEvent>(ApplyCompletionNotificationRetryScheduled)
            .On<RoleChatCompletionNotificationDispatchedEvent>(ApplyCompletionNotificationDispatched)
            .On<RoleChatCompletionNotificationExpiredEvent>(ApplyCompletionNotificationExpired)
            .On<WorkflowLlmCompletionDeliveryRetryScheduledEvent>(ApplyWorkflowLlmCompletionDeliveryRetryScheduled)
            .On<WorkflowLlmCompletionDeliveryDispatchedEvent>(ApplyWorkflowLlmCompletionDeliveryDispatched)
            .On<PendingToolApprovalPersistedEvent>(ApplyPendingApproval)
            .On<RemoteToolApprovalSubmittedEvent>(ApplyRemoteApprovalSubmitted)
            .On<ClearPendingApprovalEvent>(ApplyClearPendingApproval)
            .On<VoicePresenceEnabledEvent>(ApplyVoicePresenceEnabled)
            .On<VoicePresenceRuntimeStateChangedEvent>(ApplyVoicePresenceRuntimeStateChanged)
            .OrCurrent();

    protected override async Task OnStateChangedAfterConfigAppliedAsync(RoleGAgentState state, CancellationToken ct)
    {
        // Refactor (iter15/cluster-028):
        //   Old pattern: replay exposed only RoleName and left role identity recoverable only from actor id shape.
        //   New principle: replay restores the typed RoleId from committed RoleGAgent state.
        RoleId = state.RoleId ?? string.Empty;
        RoleName = state.RoleName ?? string.Empty;
        await ApplyModuleExtensionsFromStateIfNeededAsync(state, ct);
    }

    protected override async Task OnCommittedStateChangedAsync(
        RoleGAgentState state,
        CancellationToken ct)
    {
        _ = ct;
        using var refreshTimeoutCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_postCommitConfigRefreshTimeoutMs),
            ChatRequestTimeProvider);
        await base.OnCommittedStateChangedAsync(state, refreshTimeoutCts.Token);
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(RoleGAgentState state)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
        var overrides = state.ConfigOverrides;
        if (overrides == null)
            return new AIAgentConfigStateOverrides();

        return new AIAgentConfigStateOverrides
        {
            HasProviderName = overrides.HasProviderName,
            ProviderName = overrides.HasProviderName ? overrides.ProviderName : null,
            HasModel = overrides.HasModel,
            Model = overrides.HasModel ? overrides.Model : null,
            HasSystemPrompt = overrides.HasSystemPrompt,
            SystemPrompt = overrides.HasSystemPrompt ? overrides.SystemPrompt : null,
            HasTemperature = overrides.HasTemperature,
            Temperature = overrides.HasTemperature ? overrides.Temperature : null,
            HasMaxTokens = overrides.HasMaxTokens,
            MaxTokens = overrides.HasMaxTokens ? overrides.MaxTokens : null,
            HasMaxToolRounds = overrides.HasMaxToolRounds,
            MaxToolRounds = overrides.HasMaxToolRounds ? overrides.MaxToolRounds : null,
            HasMaxHistoryMessages = overrides.HasMaxHistoryMessages,
            MaxHistoryMessages = overrides.HasMaxHistoryMessages ? overrides.MaxHistoryMessages : null,
            HasMaxPromptTokenBudget = overrides.HasMaxPromptTokenBudget,
            MaxPromptTokenBudget = overrides.HasMaxPromptTokenBudget ? overrides.MaxPromptTokenBudget : null,
            HasCompressionThreshold = overrides.HasCompressionThreshold,
            CompressionThreshold = overrides.HasCompressionThreshold ? overrides.CompressionThreshold : null,
            HasEnableSummarization = overrides.HasEnableSummarization,
            EnableSummarization = overrides.HasEnableSummarization ? overrides.EnableSummarization : null,
        };
    }

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes text stream events and tool call events.
    /// </summary>
    [EventHandler(AllowSelfHandling = true)]
    public virtual async Task HandleChatRequest(ChatRequestEvent request)
    {
        try
        {
            await HandleChatRequestCoreAsync(request);
        }
        catch (AgentProfileTurnAuthorityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[{Role}] Chat handler failed outside the provider stream. session={SessionId}",
                RoleName,
                request.SessionId);

            if (State.Sessions.TryGetValue(request.SessionId, out var completed) && completed.Completed)
                throw;

            if (string.IsNullOrWhiteSpace(request.SessionId))
                return;

            await PersistRoleChatSessionCompletionAsync(
                request,
                content: string.Empty,
                reasoningContent: string.Empty,
                toolCalls: [],
                contentParts: [],
                contentEmitted: false,
                outcome: RoleChatSessionOutcome.Failed,
                failureCode: "CHAT_HANDLER_FAILURE",
                safeMessage: "The chat request failed. Please try again.");
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleCompletionNotificationRetryFiredAsync(
        RoleChatCompletionNotificationRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (string.IsNullOrWhiteSpace(retry.SessionId) ||
            !State.Sessions.TryGetValue(retry.SessionId, out var session) ||
            !string.Equals(
                ResolveCompletionNotificationDeliveryId(retry.SessionId, session.RunContext),
                retry.DeliveryId,
                StringComparison.Ordinal))
        {
            return;
        }

        var matchesScheduledAttempt =
            session.CompletionNotificationDeliveryStatus ==
            RoleChatCompletionNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == session.CompletionNotificationAttempt;
        var matchesScheduledNextAttemptRecovery =
            session.CompletionNotificationDeliveryStatus ==
            RoleChatCompletionNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == session.CompletionNotificationAttempt + 1;
        var matchesScheduleBeforeCommitRecovery =
            session.CompletionNotificationDeliveryStatus ==
            RoleChatCompletionNotificationDeliveryStatus.Prepared &&
            retry.Attempt == session.CompletionNotificationAttempt + 1;

        if (!matchesScheduledAttempt &&
            !matchesScheduledNextAttemptRecovery &&
            !matchesScheduleBeforeCommitRecovery)
        {
            return;
        }

        if (matchesScheduledNextAttemptRecovery || matchesScheduleBeforeCommitRecovery)
        {
            await PersistDomainEventAsync(new RoleChatCompletionNotificationRetryScheduledEvent
            {
                SessionId = retry.SessionId,
                DeliveryId = retry.DeliveryId,
                Attempt = retry.Attempt,
                CallbackId = BuildCompletionNotificationRetryCallbackId(retry.SessionId, retry.DeliveryId),
                RetryAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            });
            session = State.Sessions[retry.SessionId];
        }

        await DeliverCompletionNotificationAsync(
            retry.SessionId,
            session.Clone(),
            CancellationToken.None,
            retry.Attempt);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleIncompleteSessionFinalizationRequestedAsync(
        RoleChatIncompleteSessionFinalizationRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryFinalizeIncompleteSessionAsync(
            request.SessionId,
            request.ExpectedLastProgressSequence);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleChatRecoveryContinuationRequestedAsync(
        RoleChatRecoveryContinuationRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RecoverCheckpointSessionAsync(request);
    }

    private async Task RecoverCheckpointSessionAsync(
        RoleChatRecoveryContinuationRequested request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) ||
            !State.Sessions.TryGetValue(request.SessionId, out var session) ||
            session.Completed ||
            session.RecoveryCheckpoint is not { } checkpoint ||
            checkpoint.Generation != request.ExpectedCheckpointGeneration ||
            !IsValidRecoveryCheckpoint(checkpoint))
        {
            return;
        }

        if (checkpoint.Stage == RoleChatRecoveryCheckpointStage.WaitingApproval)
            return;
        if (checkpoint.Stage == RoleChatRecoveryCheckpointStage.ContinuationPrepared &&
            !string.Equals(
                checkpoint.PendingOperationId,
                request.OperationId,
                StringComparison.Ordinal))
        {
            return;
        }

        var isApprovalContinuation =
            checkpoint.Stage == RoleChatRecoveryCheckpointStage.ContinuationPrepared;
        var targetSessionId = isApprovalContinuation
            ? checkpoint.ContinuationSessionId
            : request.SessionId;
        if (isApprovalContinuation &&
            State.Sessions.TryGetValue(targetSessionId, out var completedContinuationSession) &&
            completedContinuationSession.Completed)
        {
            await ReconcileApprovalContinuationSourceSessionAsync(
                request.SessionId,
                targetSessionId);
            return;
        }
        if (isApprovalContinuation &&
            State.Sessions.TryGetValue(targetSessionId, out var incompleteContinuationSession))
        {
            if (await TryRequestCheckpointRecoveryAsync(
                    targetSessionId,
                    incompleteContinuationSession,
                    CancellationToken.None))
            {
                return;
            }

            await TryFinalizeIncompleteSessionAsync(
                targetSessionId,
                incompleteContinuationSession.LastProgressSequence);
            return;
        }

        var recoveredControl = LLMControlContextMapper.FromPayload(checkpoint.LlmControl);
        var recoveredContext = await TryResolveRecoveryExecutionContextAsync(
            checkpoint,
            CancellationToken.None).ConfigureAwait(false);
        if (recoveredContext is null)
        {
            await FinalizeRecoveryOutcomeUncertainAsync(
                request.SessionId,
                "The chat session requires a runtime credential that can no longer be resolved.");
            return;
        }

        recoveredControl = recoveredControl with
        {
            NyxIdAccessToken = recoveredContext.Credentials.NyxIdAccessToken,
            NyxIdOrgToken = recoveredContext.Credentials.NyxIdOrgToken,
            SenderNyxIdAccessToken = recoveredContext.Credentials.SenderNyxIdAccessToken,
        };

        List<RecoveredChatToolResult>? recoveredResults;
        try
        {
            recoveredResults = await RecoverCheckpointToolResultsAsync(
                request.SessionId,
                checkpoint,
                recoveredContext,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ChatToolRecoveryPayloadMaterialException ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Chat recovery material is permanently unavailable. session={SessionId}",
                RoleName,
                request.SessionId);
            await FinalizeRecoveryOutcomeUncertainAsync(
                request.SessionId,
                "The durable material required to recover a tool operation is unavailable or invalid.");
            return;
        }
        if (recoveredResults is null)
        {
            await FinalizeRecoveryOutcomeUncertainAsync(
                request.SessionId,
                "The outcome of a previously started tool operation could not be proven safe to replay.");
            return;
        }

        session = State.Sessions[request.SessionId];
        checkpoint = session.RecoveryCheckpoint!;
        if (checkpoint.Stage == RoleChatRecoveryCheckpointStage.WaitingApproval)
        {
            if (State.PendingApproval is { } pending &&
                string.Equals(pending.SessionId, request.SessionId, StringComparison.Ordinal) &&
                string.Equals(pending.OperationId, checkpoint.PendingOperationId, StringComparison.Ordinal))
            {
                await PublishPendingToolApprovalAsync(pending, CancellationToken.None);
            }
            return;
        }

        var recoveryRequest = new ChatRequestEvent
        {
            SessionId = targetSessionId,
            Prompt = session.Prompt,
            ScopeId = session.ScopeId,
            RunContext = isApprovalContinuation ? null : session.RunContext?.Clone(),
            LlmControl = checkpoint.LlmControl?.Clone(),
            CallerDurableCredential = checkpoint.CallerDurableCredential?.Clone(),
            WorkflowLlmToolApprovalContinuation =
                checkpoint.WorkflowLlmApprovalContinuation?.Clone(),
            WorkflowLlmCompletionDeliveryContext =
                checkpoint.WorkflowLlmCompletionDeliveryContext?.Clone() ??
                session.WorkflowLlmCompletionDeliveryContext?.Clone(),
        };
        recoveryRequest.InputParts.Add(session.InputParts);

        var continuationContext = recoveredContext with
        {
            Request = recoveredContext.Request with
            {
                RequestId = targetSessionId,
                CallId = null,
                OperationId = null,
                IdempotencyKey = null,
            },
        };
        await HandleRecoveredChatTurnAsync(
            recoveryRequest,
            checkpoint,
            new RecoveredChatTurn(
                checkpoint.Stage,
                BuildRecoveryTranscript(recoveredResults),
                recoveredResults),
            continuationContext,
            recoveredControl);
    }

    private async Task ReconcileApprovalContinuationSourceSessionAsync(
        string sourceSessionId,
        string continuationSessionId)
    {
        if (!State.Sessions.TryGetValue(continuationSessionId, out var continuationSession) ||
            !continuationSession.Completed ||
            !State.Sessions.TryGetValue(sourceSessionId, out var sourceSession) ||
            sourceSession.Completed ||
            sourceSession.RecoveryCheckpoint is not { } sourceCheckpoint ||
            sourceCheckpoint.Stage != RoleChatRecoveryCheckpointStage.ContinuationPrepared ||
            !string.Equals(
                sourceCheckpoint.ContinuationSessionId,
                continuationSessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                continuationSession.DirectParentRoleChatSessionId,
                sourceSessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        var sourceToolResults = await ResolveCompletedApprovalSourceToolResultsAsync(
            sourceSessionId,
            sourceSession);
        if (sourceToolResults is null)
        {
            await FinalizeRecoveryOutcomeUncertainAsync(
                sourceSessionId,
                "The committed approved-tool result could not be verified while reconciling the continuation.");
            return;
        }

        var toolCalls = sourceToolResults
            .Select(static result => result.ToolCall)
            .Concat(continuationSession.ToolCalls.Select(static toolCall => new ToolCall
            {
                Id = toolCall.CallId,
                Name = toolCall.ToolName,
                ArgumentsJson = toolCall.ArgumentsJson,
            }))
            .GroupBy(static toolCall => toolCall.Id, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
        var toolReceipts = sourceToolResults
            .Where(static result => result.Receipt is not null)
            .Select(static result => result.Receipt!.Clone())
            .Concat(continuationSession.ToolReceipts.Select(static receipt => receipt.Clone()))
            .GroupBy(static receipt => receipt.CallId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
        var toolResults = sourceToolResults
            .Select(ToToolResultEvent)
            .Concat(continuationSession.ToolResults.Select(static result => result.Clone()))
            .GroupBy(static result => result.CallId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
        await PersistRoleChatSessionCompletionAsync(
            new ChatRequestEvent
            {
                SessionId = sourceSessionId,
                Prompt = sourceSession.Prompt,
                ScopeId = sourceSession.ScopeId,
                RunContext = sourceSession.RunContext?.Clone(),
                WorkflowLlmCompletionDeliveryContext =
                    continuationSession.WorkflowLlmCompletionDeliveryContext is null
                        ? sourceSession.WorkflowLlmCompletionDeliveryContext?.Clone()
                        : null,
            },
            continuationSession.FinalContent,
            continuationSession.FinalReasoningContent,
            toolCalls,
            ContentPartProtoMapper.FromProtoList(continuationSession.OutputParts),
            continuationSession.ContentEmitted,
            ToTokenUsage(continuationSession.Usage),
            model: continuationSession.Model,
            toolReceipts: toolReceipts,
            toolResults: toolResults,
            outcome: continuationSession.Outcome,
            failureCode: continuationSession.FailureCode,
            safeMessage: continuationSession.SafeMessage,
            authorizationRequired: continuationSession.AuthorizationRequired);
    }

    private async Task<IReadOnlyList<RecoveredChatToolResult>?>
        ResolveCompletedApprovalSourceToolResultsAsync(
            string sourceSessionId,
            RoleChatSessionState sourceSession)
    {
        var checkpoint = sourceSession.RecoveryCheckpoint;
        if (checkpoint is null || checkpoint.ToolIntents.Count == 0)
            return [];

        var completedOperationIds = checkpoint.ToolCompletions
            .Select(static completion => completion.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        if (checkpoint.ToolIntents.Any(intent => !completedOperationIds.Contains(intent.OperationId)))
            return null;

        try
        {
            return await RecoverCheckpointToolResultsAsync(
                sourceSessionId,
                checkpoint,
                AgentToolExecutionContext.Empty,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ChatToolRecoveryPayloadMaterialException)
        {
            return null;
        }
    }

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

    protected virtual Task HandleRecoveredChatTurnAsync(
        ChatRequestEvent request,
        RoleChatRecoveryCheckpoint checkpoint,
        RecoveredChatTurn recovery,
        AgentToolExecutionContext recoveryToolContext,
        LLMControlContext recoveryLlmControl) =>
        HandleChatRequestCoreAsync(
            request,
            checkpointRecovery:
                recovery.Stage != RoleChatRecoveryCheckpointStage.ContinuationPrepared,
            recoveryTranscript: recovery.Transcript,
            recoveryToolContext: recoveryToolContext,
            recoveryLlmControl: recoveryLlmControl);

    protected virtual async Task<AgentToolExecutionContext?> TryResolveRecoveryExecutionContextAsync(
        RoleChatRecoveryCheckpoint checkpoint,
        CancellationToken ct)
    {
        var context = AgentToolExecutionContextMapper.FromRecoveryPayload(checkpoint.RecoveryContext) with
        {
            ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
        };
        if (!checkpoint.RequiresRuntimeCredential)
            return context;

        var reference = checkpoint.CallerDurableCredential;
        if (_chatToolRecoverySecretVault is null ||
            reference is null ||
            reference.SourceKind != DurableCallerCredentialSourceKind.ScheduledDispatch ||
            string.IsNullOrWhiteSpace(reference.Ref) ||
            string.IsNullOrWhiteSpace(reference.Purpose) ||
            string.IsNullOrWhiteSpace(reference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(reference.SubjectId) ||
            !IsSupportedDurableCredentialPurpose(reference.Purpose))
        {
            return null;
        }

        var resolved = await _chatToolRecoverySecretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference.Ref,
                reference.Purpose,
                reference.OwnerScopeKey,
                reference.SubjectId,
                "role-chat-checkpoint-recovery"),
            ct).ConfigureAwait(false);
        if (!resolved.Resolved ||
            string.IsNullOrWhiteSpace(resolved.Secret) ||
            !MatchesResolvedCredentialReference(reference, resolved.Reference, _timeProvider.GetUtcNow()))
            return null;

        var token = resolved.Secret.Trim();
        var referenceCredentialKind = ResolveDurableCredentialKind(reference.Purpose);
        var credentialKind = context.Credentials.NyxIdCredentialKind !=
                             AgentToolNyxIdCredentialKind.Unspecified
            ? context.Credentials.NyxIdCredentialKind
            : referenceCredentialKind;
        if (credentialKind != referenceCredentialKind ||
            credentialKind == AgentToolNyxIdCredentialKind.ProxyDelegation &&
            checkpoint.RecoveryContext.RequiresSourceReadableNyxIdAccessToken)
        {
            return null;
        }

        var hasTypedRequiredSlots =
            checkpoint.RecoveryContext.RequiresNyxIdAccessToken ||
            checkpoint.RecoveryContext.RequiresNyxIdOrgToken ||
            checkpoint.RecoveryContext.RequiresSenderNyxIdAccessToken ||
            checkpoint.RecoveryContext.RequiresSourceReadableNyxIdAccessToken;
        return context with
        {
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: checkpoint.RecoveryContext.RequiresNyxIdAccessToken ||
                                      !hasTypedRequiredSlots
                    ? token
                    : null,
                NyxIdOrgToken: checkpoint.RecoveryContext.RequiresNyxIdOrgToken
                    ? token
                    : null,
                SenderNyxIdAccessToken: checkpoint.RecoveryContext.RequiresSenderNyxIdAccessToken
                    ? token
                    : null,
                NyxIdCredentialKind: credentialKind,
                SourceReadableNyxIdAccessToken:
                checkpoint.RecoveryContext.RequiresSourceReadableNyxIdAccessToken &&
                credentialKind == AgentToolNyxIdCredentialKind.SourceReadableUserBearer
                    ? token
                    : null),
        };
    }

    private static bool IsSupportedDurableCredentialPurpose(string purpose) =>
        string.Equals(
            purpose,
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            StringComparison.Ordinal) ||
        string.Equals(
            purpose,
            CredentialSecretPurposes.WorkflowCallerSourceReadableUserBearerToken,
            StringComparison.Ordinal) ||
        string.Equals(
            purpose,
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            StringComparison.Ordinal);

    private static AgentToolNyxIdCredentialKind ResolveDurableCredentialKind(string purpose) =>
        string.Equals(
            purpose,
            CredentialSecretPurposes.WorkflowCallerSourceReadableUserBearerToken,
            StringComparison.Ordinal)
            ? AgentToolNyxIdCredentialKind.SourceReadableUserBearer
            : AgentToolNyxIdCredentialKind.ProxyDelegation;

    private static bool MatchesResolvedCredentialReference(
        DurableCallerCredentialRef expected,
        SecretReference? actual,
        DateTimeOffset now) =>
        actual is not null &&
        string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
        string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) &&
        actual.Version > 0 &&
        !string.IsNullOrWhiteSpace(actual.Fingerprint) &&
        actual.CreatedAtUnixMs > 0 &&
        (actual.ExpiresAtUnixMs == 0 || actual.ExpiresAtUnixMs > now.ToUnixTimeMilliseconds());

    private async Task<List<RecoveredChatToolResult>?> RecoverCheckpointToolResultsAsync(
        string sessionId,
        RoleChatRecoveryCheckpoint checkpoint,
        AgentToolExecutionContext baseContext,
        CancellationToken ct)
    {
        var payloadStore = _chatToolRecoveryPayloadStore;
        if (payloadStore is null && checkpoint.ToolIntents.Count > 0)
            return null;

        var results = new List<RecoveredChatToolResult>(checkpoint.ToolIntents.Count);
        foreach (var intent in checkpoint.ToolIntents.OrderBy(static candidate => candidate.Round))
        {
            var completion = checkpoint.ToolCompletions.LastOrDefault(candidate =>
                string.Equals(candidate.OperationId, intent.OperationId, StringComparison.Ordinal));
            if (completion is not null)
            {
                var committedResult = await payloadStore!.ResolveResultAsync(
                    completion.ResultReference,
                    Id,
                    sessionId,
                    intent.OperationId,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
                if (!string.Equals(
                        AgentToolArgumentsDigest.ComputeSha256(committedResult.ResultJson),
                        completion.ResultSha256,
                        StringComparison.Ordinal) ||
                    committedResult.Success != completion.Success ||
                    !string.Equals(
                        committedResult.SafeErrorCode,
                        completion.SafeErrorCode,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                var committedArguments = await payloadStore.ResolveAsync(
                    intent.ArgumentsReference,
                    Id,
                    sessionId,
                    intent.OperationId,
                    ChatToolRecoveryPayloadKind.Arguments,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
                if (!string.Equals(
                        AgentToolArgumentsDigest.ComputeSha256(committedArguments),
                        intent.ArgumentsSha256,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                results.Add(new RecoveredChatToolResult(
                    intent.Round,
                    new ToolCall
                    {
                        Id = intent.ToolCallId,
                        Name = intent.ToolName,
                        ArgumentsJson = committedArguments,
                    },
                    committedResult.ResultJson,
                    committedResult.Success,
                    committedResult.SafeErrorCode,
                    committedResult.Receipt?.Clone()));
                continue;
            }

            var storedResult = await payloadStore!.TryResolveStoredResultAsync(
                Id,
                sessionId,
                intent.OperationId,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
            if (storedResult is not null)
            {
                var storedArguments = await payloadStore.ResolveAsync(
                    intent.ArgumentsReference,
                    Id,
                    sessionId,
                    intent.OperationId,
                    ChatToolRecoveryPayloadKind.Arguments,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
                if (!string.Equals(
                        AgentToolArgumentsDigest.ComputeSha256(storedArguments),
                        intent.ArgumentsSha256,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                var storedContext = AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext) with
                {
                    Credentials = baseContext.Credentials,
                    ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
                };
                var storedOperation = new PreparedChatToolOperation(
                    sessionId,
                    intent.Round,
                    intent.OperationId,
                    new ToolCall
                    {
                        Id = intent.ToolCallId,
                        Name = intent.ToolName,
                        ArgumentsJson = storedArguments,
                    },
                    storedContext,
                    intent.ReplayPolicy,
                    intent.Presentation,
                    AgentToolExecutionAttemptKind.ActorRecovery);
                await CommitCompletionCoreAsync(
                    storedOperation,
                    new ToolExecutionResult(
                        intent.ToolCallId,
                        intent.ToolName,
                        storedResult.Payload.ResultJson,
                        !storedResult.Payload.Success,
                        storedResult.Payload.Receipt?.Clone()),
                    storedResult,
                    ct).ConfigureAwait(false);
                results.Add(new RecoveredChatToolResult(
                    intent.Round,
                    storedOperation.ToolCall,
                    storedResult.Payload.ResultJson,
                    storedResult.Payload.Success,
                    storedResult.Payload.SafeErrorCode,
                    storedResult.Payload.Receipt?.Clone()));
                continue;
            }

            if (intent.ReplayPolicy == AgentToolReplayPolicy.NonReplayable ||
                intent.ReplayPolicy == AgentToolReplayPolicy.Unspecified ||
                !System.Enum.IsDefined(intent.ReplayPolicy))
            {
                return null;
            }

            var arguments = await payloadStore!.ResolveAsync(
                intent.ArgumentsReference,
                Id,
                sessionId,
                intent.OperationId,
                ChatToolRecoveryPayloadKind.Arguments,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
            if (!string.Equals(
                    AgentToolArgumentsDigest.ComputeSha256(arguments),
                    intent.ArgumentsSha256,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var operationContext = AgentToolExecutionContextMapper.FromRecoveryPayload(intent.RecoveryContext) with
            {
                Credentials = baseContext.Credentials,
                ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
            };
            operationContext = operationContext with
            {
                Request = operationContext.Request with
                {
                    CallId = intent.ToolCallId,
                    OperationId = intent.OperationId,
                    IdempotencyKey = intent.ReplayPolicy == AgentToolReplayPolicy.IdempotentRetryable
                        ? intent.OperationId
                        : operationContext.Request.IdempotencyKey,
                },
            };
            var tool = await ResolveRecoveryToolAsync(
                checkpoint,
                intent,
                operationContext,
                ct).ConfigureAwait(false);
            if (tool is null)
                return null;
            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    tool,
                    arguments,
                    operationContext,
                    AgentToolApprovalContinuationMode.ActorOwned,
                    null,
                    AgentToolExecutionAttemptKind.ActorRecovery),
                ct).ConfigureAwait(false);
            if (!outcome.TerminalInvoked && outcome.Retryable)
                throw new InvalidOperationException(outcome.SafeMessage);
            if (string.Equals(outcome.FailureCode, "outcome_uncertain", StringComparison.OrdinalIgnoreCase))
                return null;

            var result = new ToolExecutionResult(
                intent.ToolCallId,
                intent.ToolName,
                outcome.ResultJson,
                outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                    AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete),
                outcome.Receipt);
            await CommitCompletionCoreAsync(
                new PreparedChatToolOperation(
                    sessionId,
                    intent.Round,
                    intent.OperationId,
                    new ToolCall
                    {
                        Id = intent.ToolCallId,
                        Name = intent.ToolName,
                        ArgumentsJson = arguments,
                    },
                    operationContext,
                    intent.ReplayPolicy,
                    intent.Presentation,
                    AgentToolExecutionAttemptKind.ActorRecovery),
                result,
                storedResult: null,
                ct).ConfigureAwait(false);
            results.Add(new RecoveredChatToolResult(
                intent.Round,
                new ToolCall
                {
                    Id = intent.ToolCallId,
                    Name = intent.ToolName,
                    ArgumentsJson = arguments,
                },
                outcome.ResultJson,
                outcome.Kind is AgentToolExecutionOutcomeKind.Executed or
                    AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete,
                outcome.FailureCode ?? string.Empty,
                outcome.Receipt?.Clone()));
        }

        return results;
    }

    protected virtual Task<IAgentTool?> ResolveRecoveryToolAsync(
        RoleChatRecoveryCheckpoint checkpoint,
        RoleChatToolIntentState intent,
        AgentToolExecutionContext executionContext,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Tools.Get(intent.ToolName));
    }

    private static IReadOnlyList<ChatMessage> BuildRecoveryTranscript(
        IReadOnlyList<RecoveredChatToolResult> results)
    {
        var messages = new List<ChatMessage>();
        foreach (var round in results.GroupBy(static result => result.Round).OrderBy(static group => group.Key))
        {
            var roundResults = round.ToArray();
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ToolCalls = roundResults.Select(static result => result.ToolCall).ToArray(),
            });
            messages.AddRange(roundResults.Select(static result =>
                ChatMessage.Tool(result.ToolCall.Id, result.Result)));
        }

        return messages;
    }

    private async Task FinalizeRecoveryOutcomeUncertainAsync(
        string sessionId,
        string safeMessage)
    {
        if (!State.Sessions.TryGetValue(sessionId, out var session) || session.Completed)
            return;

        await PersistCompletionWithTerminalProgressAsync(new RoleChatSessionCompletedEvent
        {
            RoleId = RoleId,
            SessionId = sessionId,
            Prompt = session.Prompt,
            ContentEmitted = session.ContentEmitted,
            Outcome = RoleChatSessionOutcome.OutcomeUncertain,
            FailureCode = UncertainSessionFailureCode,
            SafeMessage = safeMessage,
            TerminalTime = CreateTerminalTimestamp(),
            RunContext = session.RunContext?.Clone(),
            WorkflowLlmCompletionDeliveryContext =
                ResolveWorkflowCompletionDeliveryContext(session),
            ActorId = Id,
        });
        await DeliverCompletionNotificationAsync(
            sessionId,
            State.Sessions[sessionId],
            CancellationToken.None);
    }

    protected sealed record RecoveredChatTurn(
        RoleChatRecoveryCheckpointStage Stage,
        IReadOnlyList<ChatMessage> Transcript,
        IReadOnlyList<RecoveredChatToolResult> ToolResults);

    protected sealed record RecoveredChatToolResult(
        int Round,
        ToolCall ToolCall,
        string Result,
        bool Success,
        string SafeErrorCode,
        AgentToolReceipt? Receipt);

    private async Task HandleChatRequestCoreAsync(
        ChatRequestEvent request,
        bool checkpointRecovery = false,
        IReadOnlyList<ChatMessage>? recoveryTranscript = null,
        AgentToolExecutionContext? recoveryToolContext = null,
        LLMControlContext? recoveryLlmControl = null)
    {
        RoleChatSessionState? trackedSession;
        try
        {
            trackedSession = ResolveTrackedSession(request);
        }
        catch (RoleChatCommandAttemptRejectionException conflict)
        {
            const string safeMessage = "This client request id was already used for different input.";
            await PersistDomainEventAsync(new RoleChatCommandAttemptRejectedEvent
            {
                RequestedSessionId = request.SessionId,
                CommandAttemptId = ResolveCommandAttemptId(request),
                Reason = conflict.Reason,
                SafeMessage = safeMessage,
            });
            return;
        }
        if (trackedSession is { Completed: true })
        {
            Logger.LogInformation(
                "[{Role}] Replaying cached LLM completion for session={SessionId}",
                RoleName,
                request.SessionId);
            await ReplayCommittedSessionWithPostTurnDeadlineAsync(request.SessionId, trackedSession);
            return;
        }

        if (trackedSession != null && !checkpointRecovery)
        {
            if (await TryRequestCheckpointRecoveryAsync(
                    request.SessionId,
                    trackedSession,
                    CancellationToken.None))
            {
                return;
            }

            var finalized = await TryFinalizeIncompleteSessionAsync(
                request.SessionId,
                trackedSession.LastProgressSequence);
            if (finalized && State.Sessions.TryGetValue(request.SessionId, out var terminalSession))
            {
                await RunPostTurnProcessingAsync(
                    request.SessionId,
                    "incomplete session terminal replay",
                    ct => ReplayCompletedSessionAsync(request.SessionId, terminalSession, ct));
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId) &&
            !HasTrackedSessionAdmissionCapacity(State))
        {
            await PersistDomainEventAsync(new RoleChatCommandAttemptRejectedEvent
            {
                RequestedSessionId = request.SessionId,
                CommandAttemptId = ResolveCommandAttemptId(request),
                Reason = RoleChatCommandAttemptRejectionReason.CapacityExhausted,
                SafeMessage = "This role is already tracking the maximum number of active chat sessions. Please try again later.",
            });
            return;
        }

        var turnStartedTimestamp = ChatRequestTimeProvider.GetTimestamp();
        var timeoutMs = ResolveLlmTimeoutMs(request.TimeoutMs);
        using var timeoutCts = CreateTurnDeadlineCancellationSource(timeoutMs);
        var streamCt = timeoutCts.Token;
        var useWorkflowFailureMarker = request.TimeoutMs > 0;
        var llmControl = recoveryLlmControl ?? LLMControlContextMapper.FromPayload(request.LlmControl);
        var toolContext = ResolveTurnToolContext(
            request,
            recoveryToolContext ??
            llmControl.ToToolContext(AgentToolExecutionContextMapper.FromPayload(request.ToolContext)),
            Id);
        SessionReplayRecord replayRecord;
        try
        {
            var committedAuthority = await EstablishTurnAuthorityAsync(
                request,
                trackedSession,
                toolContext,
                streamCt);
            if (trackedSession != null)
            {
                Logger.LogInformation(
                    "[{Role}] Resuming incomplete LLM session={SessionId}",
                    RoleName,
                    request.SessionId);
            }

            // Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
            //   Old pattern: Information log included raw value/prompt/input preview
            //   New principle: only stable id + length + status + redaction marker
            var requestSummary = BuildRequestLogSummary(request);
            Logger.LogInformation(
                "[{Role}] LLM request: session={SessionId}, status=started, prompt_len={PromptLen}, input_parts={InputPartCount}, input_redacted=true",
                RoleName,
                request.SessionId,
                requestSummary.PromptLength,
                requestSummary.InputPartCount);

            // ─── AG-UI: TEXT_MESSAGE_START ───
            await PersistSessionProgressAsync(
                request.SessionId,
                progress => progress.TextStarted = new RoleChatTextStartedProgress { AgentId = Id },
                streamCt);
            streamCt.ThrowIfCancellationRequested();
            await PublishAsync(new TextMessageStartEvent
            {
                SessionId = request.SessionId,
                AgentId = Id,
            }, TopologyAudience.Parent, streamCt);
            streamCt.ThrowIfCancellationRequested();

            try
            {
                streamCt.ThrowIfCancellationRequested();
                var turnCatalog = await MaterializeAndCommitAgentProfileTurnCatalogAsync(
                    request,
                    toolContext,
                    committedAuthority,
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
                replayRecord = await ExecuteStreamingChatAsync(
                    request,
                    llmControl,
                    toolContext,
                    turnCatalog,
                    turnStartedTimestamp,
                    streamCt,
                    recoveryTranscript);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException and
                    not ChatToolPostExternalCheckpointException)
            {
                streamCt.ThrowIfCancellationRequested();
                Logger.LogWarning(ex,
                    "[{Role}] LLM request failed. session={SessionId}, provider={Provider}, model={Model}, metadataKeys=[{MetadataKeys}]",
                    RoleName,
                    request.SessionId,
                    EffectiveConfig.ProviderName,
                    EffectiveConfig.Model ?? "<default>",
                    request.Metadata.Count > 0 ? string.Join(",", request.Metadata.Keys) : "<none>");
                var toolNames = Tools.HasTools
                    ? string.Join(",", Tools.GetAll().Select(t => t.Name ?? "<null>"))
                    : "none";
                var error = SanitizeFailureMessage(ex.Message);
                replayRecord = SessionReplayRecord.FromFailure(
                    BuildNonTimeoutLlmFailureContent(
                        error,
                        toolNames,
                        useWorkflowFailureMarker));
            }
        }
        catch (ChatToolPostExternalCheckpointException ex)
        {
            if (await TryHandlePostExternalToolCheckpointFailureAsync(request.SessionId, ex))
                return;

            throw;
        }
        catch (Exception ex) when (HasCommittedSessionCompletion(request.SessionId))
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Post-commit turn work failed after terminal authority was acquired. session={SessionId}",
                RoleName,
                request.SessionId);
            await ReplayCommittedSessionWithPostTurnDeadlineAsync(
                request.SessionId,
                State.Sessions[request.SessionId]);
            return;
        }
        catch (Exception ex) when (timeoutCts.IsCancellationRequested || ex is OperationCanceledException)
        {
            Logger.LogWarning(
                "[{Role}] LLM request timeout after {TimeoutMs}ms. session={SessionId}",
                RoleName,
                timeoutMs,
                request.SessionId);
            await FinalizeTimedOutTurnAsync(request, timeoutMs);
            return;
        }
        finally
        {
            // The stashed per-turn token must not outlive its turn: a later turn without a token
            // (e.g. an internal continuation) must not trigger an overlay refresh with a stale credential.
            _currentTurnNyxIdAccessToken = null;
        }

        // ─── Detect approval-pending tool result and set up continuation ───
        var completionPipelineReturned = false;
        try
        {
            streamCt.ThrowIfCancellationRequested();
            var pendingApproval = DetectPendingApproval(replayRecord.ToolReceipts, replayRecord.ToolCalls, request);
            OnPlanOrHandoffObserved(pendingApproval is not null);
            if (pendingApproval != null)
            {
                var approvalProgress = CreateSessionProgress(request.SessionId, progress =>
                    progress.ToolApprovalRequired = new RoleChatToolApprovalRequiredProgress
                    {
                        Pending = pendingApproval.Clone(),
                    });
                await PersistDomainEventAsync(approvalProgress, streamCt);
                streamCt.ThrowIfCancellationRequested();
                await SuspendForToolApprovalAsync(pendingApproval, streamCt);
                streamCt.ThrowIfCancellationRequested();
                return;
            }

            // Refactor (iter164/cluster-001-role-completion):
            //   Old pattern: terminal presentation frames were published before
            //                RoleChatSessionCompletedEvent was committed; commit failure was downgraded to replay-only loss.
            //   New principle: commit RoleChatSessionCompletedEvent first; publish terminal frames only from that committed fact.
            streamCt.ThrowIfCancellationRequested();
            await PersistSessionCompletionAsync(request, replayRecord, streamCt);
            completionPipelineReturned = true;
            await RunPostTurnProcessingAsync(
                request.SessionId,
                "terminal presentation",
                async ct =>
                {
                    replayRecord = await PublishMissingDisplayContentWithDeadlineAsync(
                        request.SessionId,
                        replayRecord,
                        ct);
                    await PublishUsageAsync(
                        request.SessionId,
                        ToTokenUsagePayload(replayRecord.Usage),
                        replayRecord.Model,
                        ct);
                    await PublishCompletionAsync(request.SessionId, replayRecord.Content, ct);
                });
        }
        catch (Exception) when (
            timeoutCts.IsCancellationRequested &&
            !HasCommittedSessionCompletion(request.SessionId))
        {
            await FinalizeTimedOutTurnAsync(request, timeoutMs);
        }
        catch (Exception ex) when (
            completionPipelineReturned &&
            HasCommittedSessionCompletion(request.SessionId))
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Post-commit presentation work failed after terminal authority was acquired. session={SessionId}",
                RoleName,
                request.SessionId);
            await ReplayCommittedSessionWithPostTurnDeadlineAsync(
                request.SessionId,
                State.Sessions[request.SessionId]);
        }
    }

    private async Task FinalizeTimedOutTurnAsync(ChatRequestEvent request, int timeoutMs)
    {
        var error = $"LLM request timed out after {timeoutMs}ms";
        var timeoutRecord = SessionReplayRecord.FromFailure(
            BuildLlmFailureContent(error),
            "LLM_TIMEOUT",
            "The LLM turn exceeded its deadline. Please try again.");
        await PersistSessionCompletionAsync(
            request,
            timeoutRecord,
            CancellationToken.None,
            clearMatchingPendingApproval: true);
        await RunPostTurnProcessingAsync(
            request.SessionId,
            "timeout terminal presentation",
            ct => PublishCompletionAsync(request.SessionId, timeoutRecord.Content, ct));
    }

    internal static int ResolveLlmTimeoutMs(int requestedTimeoutMs, int maxTurnDeadlineMs)
    {
        if (maxTurnDeadlineMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTurnDeadlineMs));

        return requestedTimeoutMs > 0 && requestedTimeoutMs < maxTurnDeadlineMs
            ? requestedTimeoutMs
            : maxTurnDeadlineMs;
    }

    protected int ResolveLlmTimeoutMs(int requestedTimeoutMs) =>
        ResolveLlmTimeoutMs(requestedTimeoutMs, _maxTurnDeadlineMs);

    protected CancellationTokenSource CreateTurnDeadlineCancellationSource(int timeoutMs) =>
        new(TimeSpan.FromMilliseconds(timeoutMs), ChatRequestTimeProvider);

    protected CancellationTokenSource CreatePostTurnProcessingCancellationSource() =>
        new(TimeSpan.FromMilliseconds(_postTurnProcessingTimeoutMs), ChatRequestTimeProvider);

    protected async Task RunPostTurnProcessingAsync(
        string sessionId,
        string operation,
        Func<CancellationToken, Task> action)
    {
        using var postTurnCts = CreatePostTurnProcessingCancellationSource();
        try
        {
            await action(postTurnCts.Token).WaitAsync(postTurnCts.Token);
        }
        catch (OperationCanceledException ex) when (postTurnCts.IsCancellationRequested)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Post-turn {Operation} exceeded its deadline; the committed terminal fact remains authoritative. session={SessionId}",
                RoleName,
                operation,
                sessionId);
        }
    }

    protected async Task<bool> TrySchedulePostTurnDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions options,
        CancellationToken ct)
    {
        using var schedulingDeadlineCts = CreatePostTurnProcessingCancellationSource();
        using var schedulingCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            schedulingDeadlineCts.Token);
        var schedulingCt = schedulingCts.Token;
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                    callbackId,
                    dueTime,
                    evt,
                    options,
                    schedulingCt)
                .WaitAsync(schedulingCt);
            schedulingCt.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (
            schedulingDeadlineCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunBestEffortPostTurnProcessingAsync(
        string sessionId,
        string operation,
        Func<CancellationToken, Task> action)
    {
        try
        {
            await RunPostTurnProcessingAsync(sessionId, operation, action);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Best-effort post-turn {Operation} failed; committed facts remain authoritative. session={SessionId}",
                RoleName,
                operation,
                sessionId);
        }
    }

    protected virtual string BuildNonTimeoutLlmFailureContent(
        string safeError,
        string toolNames,
        bool useWorkflowFailureMarker) =>
        useWorkflowFailureMarker
            ? BuildLlmFailureContent(safeError)
            : $"LLM request failed [tools={toolNames}]: {safeError}";

    private static string BuildLlmFailureContent(string? message)
    {
        var safeMessage = SanitizeFailureMessage(message);
        return $"{LlmFailureContentPrefix} {safeMessage}";
    }

    private static string SanitizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private static AgentToolExecutionContext ResolveTurnToolContext(
        ChatRequestEvent request,
        AgentToolExecutionContext context,
        string? actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return context with
        {
            Request = context.Request with
            {
                RequestId = NormalizeToolContextValue(request.SessionId) ?? context.Request.RequestId,
            },
            ExecutionOwner = string.IsNullOrWhiteSpace(actorId)
                ? context.ExecutionOwner
                : AgentToolExecutionOwners.Actor(actorId),
        };
    }

    protected async Task<AgentProfileTurnAuthorityState?> EstablishTurnAuthorityAsync(
        ChatRequestEvent request,
        RoleChatSessionState? trackedSession,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return null;

        if (trackedSession is null)
        {
            var started = new RoleChatSessionStartedEvent
            {
                SessionId = request.SessionId,
                Prompt = request.Prompt,
                InputParts = { request.InputParts },
                RunContext = request.RunContext?.Clone(),
                ScopeId = request.ScopeId ?? string.Empty,
                RecoveryCheckpoint = new RoleChatRecoveryCheckpoint
                {
                    Generation = 1,
                    Stage = RoleChatRecoveryCheckpointStage.ModelReady,
                    RecoveryContext = toolContext.ToRecoveryPayload(),
                    CallerDurableCredential = request.CallerDurableCredential?.Clone(),
                    LlmControl = ToRecoverySafeLlmControl(llmControl: request.LlmControl),
                    WorkflowLlmApprovalContinuation =
                        request.WorkflowLlmToolApprovalContinuation?.Clone(),
                    DirectParentRoleChatSessionId =
                        request.WorkflowLlmToolApprovalContinuation
                            ?.DirectParentRoleChatSessionId ?? string.Empty,
                    RequiresRuntimeCredential = HasRuntimeCredential(
                        LLMControlContextMapper.FromPayload(request.LlmControl),
                        toolContext),
                    PayloadExpiresAtUnixMs = _timeProvider.GetUtcNow()
                        .Add(ToolRecoveryPayloadLifetime)
                        .ToUnixTimeMilliseconds(),
                    WorkflowLlmCompletionDeliveryContext =
                        request.WorkflowLlmCompletionDeliveryContext?.Clone(),
                },
            };
            var preparation = await PrepareAgentProfileTurnAuthorityAsync(request, toolContext, ct);
            ct.ThrowIfCancellationRequested();
            if (preparation is null)
            {
                await PersistDomainEventAsync(started, ct);
                return null;
            }

            ct.ThrowIfCancellationRequested();
            var initial = new AgentProfileTurnAuthorityCommittedEvent
            {
                CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                Authority = preparation.Authority,
            };
            var predictedStartedState = ApplyChatSessionStarted(State, started);
            if (!TryApplyAgentProfileTurnAuthorityCommitted(predictedStartedState, initial, out _))
            {
                throw new AgentProfileTurnAuthorityException(
                    "Prepared turn authority is not valid for the new session.");
            }

            try
            {
                await PersistDomainEventsAsync([started, initial], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw new AgentProfileTurnAuthorityException(ex.Message, ex);
            }
            return ResolveCommittedTurnAuthority(request.SessionId);
        }

        if (State.AgentProfile is null)
            return null;

        var active = State.AgentProfileTurnAuthority;
        if (active?.ReconciliationKey is null ||
            !string.Equals(
                active.ReconciliationKey.SessionId,
                request.SessionId,
                StringComparison.Ordinal))
        {
            var legacy = new AgentProfileTurnAuthorityCommittedEvent
            {
                CommitKind = AgentProfileTurnAuthorityCommitKind.Initial,
                Authority = CreateLegacyRestrictedEmptyAuthority(request.SessionId),
            };
            await PersistRequiredTurnAuthorityAsync(legacy, ct);
            return ResolveCommittedTurnAuthority(request.SessionId);
        }

        if (active.SelectedExactSkillRef is null)
            return active.Clone();

        var retryAuthority = active.Clone();
        retryAuthority.ReconciliationKey.Attempt++;
        var retry = new AgentProfileTurnAuthorityCommittedEvent
        {
            CommitKind = AgentProfileTurnAuthorityCommitKind.RetryStarted,
            Authority = retryAuthority,
        };
        await PersistRequiredTurnAuthorityAsync(retry, ct);
        return ResolveCommittedTurnAuthority(request.SessionId);
    }

    private async Task<AgentProfileTurnCatalog?> MaterializeAndCommitAgentProfileTurnCatalogAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnAuthorityState? committedAuthority,
        CancellationToken ct)
    {
        if (committedAuthority is null)
            return null;

        var materialization = await MaterializeCommittedAgentProfileTurnCatalogAsync(
            request,
            toolContext,
            committedAuthority.Clone(),
            ct);
        ct.ThrowIfCancellationRequested();
        if (materialization is null)
            return null;

        var reconcile = new AgentProfileTurnAuthorityCommittedEvent
        {
            CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
            Authority = materialization.ReconcileProposal,
        };
        await PersistValidatedTurnAuthorityAsync(reconcile, ct);
        ct.ThrowIfCancellationRequested();
        var active = State.AgentProfileTurnAuthority;
        return active is not null && HasSameReconciliationKey(active, reconcile.Authority)
            ? materialization.Catalog
            : null;
    }

    private async Task PersistValidatedTurnAuthorityAsync(
        AgentProfileTurnAuthorityCommittedEvent authorityEvent,
        CancellationToken ct = default)
    {
        if (!TryApplyAgentProfileTurnAuthorityCommitted(State, authorityEvent, out _))
            throw new InvalidOperationException("Turn authority transition would violate the active fencing key or ceiling.");

        await PersistDomainEventAsync(authorityEvent, ct);
    }

    private async Task PersistRequiredTurnAuthorityAsync(
        AgentProfileTurnAuthorityCommittedEvent authorityEvent,
        CancellationToken ct)
    {
        try
        {
            await PersistValidatedTurnAuthorityAsync(authorityEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new AgentProfileTurnAuthorityException(ex.Message, ex);
        }
    }

    private AgentProfileTurnAuthorityState ResolveCommittedTurnAuthority(string sessionId)
    {
        var active = State.AgentProfileTurnAuthority;
        if (active?.ReconciliationKey is null ||
            !string.Equals(active.ReconciliationKey.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new AgentProfileTurnAuthorityException(
                "The committed turn authority does not match the active session.");
        }

        return active.Clone();
    }

    private static AgentProfileTurnAuthorityState CreateLegacyRestrictedEmptyAuthority(string sessionId) =>
        new()
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = sessionId,
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty,
            DegradationReasons = { AgentProfileTurnDegradationReason.LegacyAuthorityMissing },
        };

    protected virtual Task<AgentProfileTurnAuthorityPreparation?> PrepareAgentProfileTurnAuthorityAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        CancellationToken ct) =>
        Task.FromResult<AgentProfileTurnAuthorityPreparation?>(null);

    protected virtual Task<AgentProfileTurnCatalogMaterialization?> MaterializeCommittedAgentProfileTurnCatalogAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnAuthorityState committedAuthority,
        CancellationToken ct) =>
        Task.FromResult<AgentProfileTurnCatalogMaterialization?>(null);

    protected virtual void OnPlanOrHandoffObserved(bool handoffPending)
    {
    }

    protected virtual void OnFirstStreamedOutputObserved(TimeSpan elapsed)
    {
    }

    private async Task<SessionReplayRecord> ExecuteStreamingChatAsync(
        ChatRequestEvent request,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnCatalog? turnCatalog,
        long turnStartedTimestamp,
        CancellationToken streamCt,
        IReadOnlyList<ChatMessage>? recoveryTranscript = null)
    {
        // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
        var initialHistoryCount = History.Count;
        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        var toolCalls = new StreamingToolCallAccumulator();
        var contentParts = new List<ContentPart>();
        var toolReceipts = new List<AgentToolReceipt>();
        var toolResults = new List<ToolResultEvent>();
        var toolCallSnapshots = new List<ToolCallEvent>();
        TokenUsage? usage = null;
        var firstStreamedOutputObserved = false;
        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count > 0
            ? AgentToolExecutionContextMapper.StripOwnedControlKeys(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal))
            : null;
        // Stash this turn's token for chartered direct-chat subclasses (System Skill Overlay seam).
        // Kept in memory only for the turn; never persisted or logged.
        _currentTurnNyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken;
        var inputParts = ResolveRequestInputParts(request);

        var stream = recoveryTranscript is { Count: > 0 }
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
        await foreach (var chunk in stream)
        {
            // A provider may observe cancellation and still yield a late chunk. The host deadline,
            // not provider conformance, remains the terminal authority for the turn.
            streamCt.ThrowIfCancellationRequested();

            if (chunk.Usage != null)
                usage = chunk.Usage;

            if (!firstStreamedOutputObserved &&
                (!string.IsNullOrEmpty(chunk.DeltaContent) ||
                 chunk.DeltaContentPart != null ||
                 !string.IsNullOrEmpty(chunk.DeltaReasoningContent)))
            {
                firstStreamedOutputObserved = true;
                OnFirstStreamedOutputObserved(
                    ChatRequestTimeProvider.GetElapsedTime(turnStartedTimestamp));
            }

            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PersistSessionProgressAsync(
                    request.SessionId,
                    progress => progress.TextDelta = new RoleChatTextDeltaProgress { Delta = chunk.DeltaContent },
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
                await PublishAsync(new TextMessageContentEvent
                {
                    Delta = chunk.DeltaContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent, streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            if (chunk.DeltaContentPart != null)
            {
                contentParts.Add(chunk.DeltaContentPart);
                var part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart);
                await PersistSessionProgressAsync(
                    request.SessionId,
                    progress => progress.Media = new RoleChatMediaProgress
                    {
                        AgentId = Id,
                        Part = part.Clone(),
                    },
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
                await PublishAsync(new MediaContentEvent
                {
                    SessionId = request.SessionId,
                    AgentId = Id,
                    Part = part,
                }, TopologyAudience.Parent, streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PersistSessionProgressAsync(
                    request.SessionId,
                    progress => progress.ReasoningDelta = new RoleChatReasoningDeltaProgress
                    {
                        Delta = chunk.DeltaReasoningContent,
                    },
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
                await PublishAsync(new TextMessageReasoningEvent
                {
                    Delta = chunk.DeltaReasoningContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent, streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);

            if (chunk.ToolCallStarted != null)
            {
                var started = chunk.ToolCallStarted;
                CaptureToolCallSnapshot(toolCallSnapshots, started);
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
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            if (chunk.ToolCallCompleted != null)
            {
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
                toolResults.Add(toolResult.Clone());
                await PersistSessionProgressAsync(
                    request.SessionId,
                    progress => progress.ToolCompleted = new RoleChatToolCompletedProgress
                    {
                        Result = toolResult.Clone(),
                        ToolName = completed.ToolName,
                        OperationId = completed.OperationId,
                    },
                    streamCt);
                streamCt.ThrowIfCancellationRequested();
            }

            var receipt = chunk.ToolCallCompleted?.Receipt ?? chunk.ToolReceipt;
            if (receipt != null)
                toolReceipts.Add(receipt.Clone());
        }

        // Also reject a provider that observes cancellation and then ends the stream normally.
        streamCt.ThrowIfCancellationRequested();

        var appendedHistoryMessages = History.Messages
            .Skip(Math.Min(initialHistoryCount, History.Count))
            .ToArray();

        var completedToolCalls = MergeCompletedToolCalls(toolCalls.BuildToolCalls(), toolCallSnapshots);
        foreach (var toolCall in completedToolCalls)
        {
            var snapshot = FindToolCallSnapshot(toolCallSnapshots, toolCall.Id);
            streamCt.ThrowIfCancellationRequested();
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = ShouldRedactToolCallArguments(toolCall.Id, toolReceipts)
                    ? string.Empty
                    : toolCall.ArgumentsJson,
                Presentation = ResolveToolCallPresentation(toolCall.Name, snapshot),
            }, TopologyAudience.Parent, streamCt);
            streamCt.ThrowIfCancellationRequested();
        }

        foreach (var toolResult in appendedHistoryMessages)
        {
            if (!string.Equals(toolResult.Role, "tool", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(toolResult.ToolCallId))
            {
                continue;
            }

            var receipt = toolReceipts
                .LastOrDefault(candidate => string.Equals(candidate.CallId, toolResult.ToolCallId, StringComparison.Ordinal));
            var toolResultEvent = new ToolResultEvent
            {
                CallId = toolResult.ToolCallId,
                ResultJson = toolResult.Content ?? string.Empty,
                Success = receipt?.Status == AgentToolReceiptStatus.Success,
                Error = receipt?.ErrorMessage ?? string.Empty,
            };
            if (receipt is not null)
                toolResultEvent.Receipt = receipt.Clone();

            streamCt.ThrowIfCancellationRequested();
            await PublishAsync(toolResultEvent, TopologyAudience.Parent, streamCt);
            streamCt.ThrowIfCancellationRequested();
        }

        var authorizationRequired = toolReceipts
            .LastOrDefault(receipt =>
                receipt.Status == AgentToolReceiptStatus.AuthorizationRequired &&
                receipt.AuthorizationRequired != null)
            ?.AuthorizationRequired
            .Clone();
        var response = fullContent.ToString();
        // Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
        //   Old pattern: Information log included raw value/prompt/input preview
        //   New principle: only stable id + length + status + redaction marker
        Logger.LogInformation(
            "[{Role}] LLM response: session={SessionId}, status=completed, output_len={OutputLen}, output_redacted=true",
            RoleName,
            request.SessionId,
            response.Length);

        if (fullReasoning.Length > 0)
        {
            Logger.LogInformation(
                "[{Role}] LLM reasoning: session={SessionId}, status=completed, reasoning_len={ReasoningLen}, reasoning_redacted=true",
                RoleName,
                request.SessionId,
                fullReasoning.Length);
        }

        return new SessionReplayRecord(
            response,
            fullReasoning.ToString(),
            completedToolCalls,
            contentParts,
            toolReceipts,
            toolResults,
            Usage: usage,
            Model: EffectiveConfig.Model ?? string.Empty,
            ContentEmitted: fullContent.Length > 0,
            Outcome: authorizationRequired == null
                ? RoleChatSessionOutcome.Completed
                : RoleChatSessionOutcome.Blocked,
            AuthorizationRequired: authorizationRequired)
        {
            ToolCallSnapshots = toolCallSnapshots.Select(static snapshot => snapshot.Clone()).ToArray(),
        };
    }

    private Task PersistSessionCompletionAsync(
        ChatRequestEvent request,
        SessionReplayRecord replayRecord,
        CancellationToken ct = default,
        bool clearMatchingPendingApproval = false) =>
        PersistRoleChatSessionCompletionAsync(
            request,
            replayRecord.Content,
            replayRecord.ReasoningContent,
            replayRecord.ToolCalls,
            replayRecord.ContentParts,
            replayRecord.ContentEmitted,
            replayRecord.Usage,
            replayRecord.Model,
            replayRecord.ToolReceipts,
            replayRecord.ToolResults,
            replayRecord.ToolCallSnapshots,
            replayRecord.Outcome,
            replayRecord.FailureCode,
            replayRecord.SafeMessage,
            replayRecord.AuthorizationRequired,
            ct,
            clearMatchingPendingApproval);

    protected async Task PersistRoleChatSessionCompletionAsync(
        ChatRequestEvent request,
        string content,
        string reasoningContent,
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<ContentPart> contentParts,
        bool contentEmitted,
        TokenUsage? usage = null,
        string? model = null,
        IReadOnlyList<AgentToolReceipt>? toolReceipts = null,
        IReadOnlyList<ToolResultEvent>? toolResults = null,
        IReadOnlyList<ToolCallEvent>? toolCallSnapshots = null,
        RoleChatSessionOutcome outcome = RoleChatSessionOutcome.Completed,
        string? failureCode = null,
        string? safeMessage = null,
        NyxIdAuthorizationRequiredEvent? authorizationRequired = null,
        CancellationToken ct = default,
        bool clearMatchingPendingApproval = false)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return;

        if (State.Sessions.TryGetValue(request.SessionId, out var existingTerminal) &&
            existingTerminal.Completed &&
            !CanReconcileTerminalOutcome(existingTerminal.Outcome, outcome))
        {
            return;
        }

        var completion = new RoleChatSessionCompletedEvent
        {
            // Refactor (iter15/cluster-028):
            //   Old pattern: consumers inferred workflow role id from PublisherActorId string prefixes.
            //   New principle: completion events publish RoleId as a typed business fact.
            RoleId = RoleId,
            SessionId = request.SessionId,
            Content = content,
            ReasoningContent = reasoningContent,
            Prompt = request.Prompt,
            ContentEmitted = contentEmitted,
            ToolCalls = { ToToolCallEvents(toolCalls, toolReceipts ?? [], toolCallSnapshots ?? []) },
            OutputParts = { ContentPartProtoMapper.ToProtoList(contentParts) },
            ToolReceipts = { (toolReceipts ?? []).Select(receipt => receipt.Clone()) },
            ToolResults = { (toolResults ?? []).Select(result => result.Clone()) },
            Usage = ToTokenUsagePayload(usage),
            Model = model ?? string.Empty,
            Outcome = outcome,
            FailureCode = failureCode ?? string.Empty,
            SafeMessage = safeMessage ?? string.Empty,
            AuthorizationRequired = authorizationRequired?.Clone(),
            TerminalTime = CreateTerminalTimestamp(),
            RunContext = request.RunContext?.Clone(),
            WorkflowLlmCompletionDeliveryContext =
                request.WorkflowLlmCompletionDeliveryContext?.Clone(),
            ActorId = Id,
        };
        ct.ThrowIfCancellationRequested();
        PrepareTerminalProgress(completion);
        var matchingPendingApproval = clearMatchingPendingApproval &&
                                      State.PendingApproval is { } candidate &&
                                      string.Equals(
                                          candidate.SessionId,
                                          request.SessionId,
                                          StringComparison.Ordinal)
            ? candidate
            : null;
        if (matchingPendingApproval is not null)
        {
            await PersistDomainEventsAsync(
            [
                completion,
                new ClearPendingApprovalEvent { RequestId = matchingPendingApproval.RequestId },
            ], ct);
        }
        else
        {
            await PersistDomainEventAsync(completion, ct);
        }

        await OnRoleChatSessionTerminalCommittedAsync(request.SessionId, CancellationToken.None);
        await DeliverCompletionNotificationAsync(
            request.SessionId,
            State.Sessions[request.SessionId],
            CancellationToken.None);
    }

    protected bool HasCommittedSessionCompletion(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        State.Sessions.TryGetValue(sessionId, out var session) &&
        session.Completed;

    private static WorkflowLlmCompletionDeliveryContext?
        ResolveWorkflowCompletionDeliveryContext(RoleChatSessionState session) =>
        session.WorkflowLlmCompletionDeliveryContext?.Clone() ??
        session.RecoveryCheckpoint?.WorkflowLlmCompletionDeliveryContext?.Clone();

    protected async Task<bool> TryHandlePostExternalToolCheckpointFailureAsync(
        string sessionId,
        ChatToolPostExternalCheckpointException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Logger.LogWarning(
            exception,
            "[{Role}] Post-external tool checkpoint commit failed. session={SessionId} permanentMaterialFailure={PermanentMaterialFailure}",
            RoleName,
            sessionId,
            exception.PermanentMaterialFailure);
        if (exception.PermanentMaterialFailure)
        {
            await FinalizeRecoveryOutcomeUncertainAsync(
                sessionId,
                "The durable material required to commit a completed tool operation is unavailable or invalid.");
            return true;
        }

        return State.Sessions.TryGetValue(sessionId, out var incompleteSession) &&
               await TryRequestCheckpointRecoveryAsync(
                   sessionId,
                   incompleteSession,
                   CancellationToken.None);
    }

    private Task PersistCompletionWithTerminalProgressAsync(
        RoleChatSessionCompletedEvent completion,
        CancellationToken ct = default) =>
        PersistPreparedCompletionAsync(completion, ct);

    private async Task PersistPreparedCompletionAsync(
        RoleChatSessionCompletedEvent completion,
        CancellationToken ct)
    {
        PrepareTerminalProgress(completion);
        await PersistDomainEventAsync(completion, ct);
        await OnRoleChatSessionTerminalCommittedAsync(completion.SessionId, CancellationToken.None);
    }

    protected virtual Task OnRoleChatSessionTerminalCommittedAsync(
        string sessionId,
        CancellationToken ct) =>
        RequestDirectParentApprovalReconciliationAsync(sessionId, ct);

    private async Task RequestDirectParentApprovalReconciliationAsync(
        string continuationSessionId,
        CancellationToken ct)
    {
        if (!State.Sessions.TryGetValue(continuationSessionId, out var continuationSession) ||
            string.IsNullOrWhiteSpace(continuationSession.DirectParentRoleChatSessionId) ||
            !State.Sessions.TryGetValue(
                continuationSession.DirectParentRoleChatSessionId,
                out var directParentSession) ||
            directParentSession.Completed ||
            directParentSession.RecoveryCheckpoint is not { } directParentCheckpoint ||
            directParentCheckpoint.Stage != RoleChatRecoveryCheckpointStage.ContinuationPrepared ||
            !string.Equals(
                directParentCheckpoint.ContinuationSessionId,
                continuationSessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        await TryRequestCheckpointRecoveryAsync(
            continuationSession.DirectParentRoleChatSessionId,
            directParentSession,
            ct);
    }

    private void PrepareTerminalProgress(RoleChatSessionCompletedEvent completion)
    {
        completion.TerminalProgress.Clear();
        completion.TerminalProgress.Add(BuildTerminalProgressEvents(completion));
    }

    private IReadOnlyList<RoleChatSessionProgressedEvent> BuildTerminalProgressEvents(
        RoleChatSessionCompletedEvent completion)
    {
        var events = new List<RoleChatSessionProgressedEvent>();
        var sequence = ResolveLastProgressSequence(completion.SessionId);

        void Add(Action<RoleChatSessionProgressedEvent> configure)
        {
            var progress = CreateSessionProgress(completion.SessionId, ref sequence, configure);
            events.Add(progress);
        }

        if (!completion.ContentEmitted && IsDisplayableCompletionContent(completion.Content))
        {
            Add(progress =>
                progress.TextDelta = new RoleChatTextDeltaProgress { Delta = completion.Content });
        }

        if (completion.Usage != null)
        {
            Add(progress =>
                progress.Usage = new RoleChatUsageProgress
                {
                    Usage = completion.Usage.Clone(),
                    Model = completion.Model ?? string.Empty,
                });
        }

        Add(progress =>
            progress.TextEnded = new RoleChatTextEndedProgress { MessageId = completion.SessionId });

        if (completion.AuthorizationRequired != null)
        {
            Add(progress =>
                progress.AuthorizationRequired = new RoleChatAuthorizationRequiredProgress
                {
                    AuthorizationRequired = completion.AuthorizationRequired.Clone(),
                });
        }

        Add(progress =>
            progress.Terminal = new RoleChatTerminalProgress
            {
                Outcome = completion.Outcome,
                FailureCode = completion.FailureCode ?? string.Empty,
                SafeMessage = completion.SafeMessage ?? string.Empty,
                FinalContent = completion.Content ?? string.Empty,
            });

        return events;
    }

    private async Task PersistSessionProgressAsync(
        string? sessionId,
        Action<RoleChatSessionProgressedEvent> configure,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        await PersistDomainEventAsync(CreateSessionProgress(sessionId, configure), ct);
    }

    private RoleChatSessionProgressedEvent CreateSessionProgress(
        string? sessionId,
        Action<RoleChatSessionProgressedEvent> configure)
    {
        var sequence = ResolveLastProgressSequence(sessionId);
        return CreateSessionProgress(sessionId, ref sequence, configure);
    }

    private static RoleChatSessionProgressedEvent CreateSessionProgress(
        string? sessionId,
        ref long sequence,
        Action<RoleChatSessionProgressedEvent> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
            throw new ArgumentException("Session id is required for role chat progress.", nameof(sessionId));

        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = normalizedSessionId,
            Sequence = sequence = checked(sequence + 1),
        };
        configure(progress);
        if (progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.None)
            throw new InvalidOperationException("Role chat session progress requires a typed payload.");

        return progress;
    }

    private long ResolveLastProgressSequence(string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        return State.Sessions.TryGetValue(normalizedSessionId, out var session)
            ? session.LastProgressSequence
            : 0;
    }

    private async Task PersistApprovalTerminalFailureThenClearPendingAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage,
        string? terminalTurnId = null)
    {
        var completion = BuildApprovalTerminalFailure(
            pending,
            reasonCode,
            reasonMessage,
            terminalTurnId);
        var facts = new List<IMessage>();
        if (completion is not null)
            facts.Add(completion);
        if (State.PendingApproval?.RequestId == pending.RequestId)
            facts.Add(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
        if (facts.Count > 0)
            await PersistDomainEventsAsync(facts);
        if (completion is not null)
            await OnRoleChatSessionTerminalCommittedAsync(completion.SessionId, CancellationToken.None);
        await RunApprovalTerminalPostTurnProcessingAsync(
            pending,
            completion,
            reasonCode,
            reasonMessage);
    }

    private async Task TryPersistApprovalOutcomeUncertainThenClearPendingAsync(
        PendingToolApprovalState pending,
        string safeMessage)
    {
        try
        {
            var facts = new List<IMessage>();
            RoleChatSessionCompletedEvent? completion = null;
            if (State.Sessions.TryGetValue(pending.SessionId, out var session) && !session.Completed)
            {
                completion = new RoleChatSessionCompletedEvent
                {
                    RoleId = RoleId,
                    SessionId = pending.SessionId,
                    Prompt = session.Prompt,
                    ContentEmitted = session.ContentEmitted,
                    Outcome = RoleChatSessionOutcome.OutcomeUncertain,
                    FailureCode = UncertainSessionFailureCode,
                    SafeMessage = safeMessage,
                    TerminalTime = CreateTerminalTimestamp(),
                    RunContext = session.RunContext?.Clone(),
                    WorkflowLlmCompletionDeliveryContext =
                        ToWorkflowLlmCompletionDeliveryContext(pending.WorkflowLlmContinuation),
                    ActorId = Id,
                };
                PrepareTerminalProgress(completion);
                facts.Add(completion);
            }

            if (State.PendingApproval?.RequestId == pending.RequestId)
                facts.Add(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
            if (facts.Count > 0)
                await PersistDomainEventsAsync(facts);
            if (completion is not null)
                await OnRoleChatSessionTerminalCommittedAsync(completion.SessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[{Role}] Failed to persist approval outcome-uncertain authority. request={RequestId} session={SessionId}",
                RoleName,
                pending.RequestId,
                pending.SessionId);
            throw;
        }
    }

    private async Task TryPersistApprovalTerminalFailureThenClearPendingAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage,
        string? terminalTurnId = null)
    {
        try
        {
            await PersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                reasonCode,
                reasonMessage,
                terminalTurnId);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[{Role}] Failed to atomically persist approval terminal failure and pending cleanup. request={RequestId} session={SessionId} reasonCode={ReasonCode}",
                RoleName,
                pending.RequestId,
                pending.SessionId,
                reasonCode);
        }
    }

    private RoleChatSessionCompletedEvent? BuildApprovalTerminalFailure(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage,
        string? terminalTurnId = null)
    {
        var hasCallerSelectedTurnId = !string.IsNullOrWhiteSpace(terminalTurnId);
        var resolvedTurnId = !hasCallerSelectedTurnId
            ? pending.SessionId
            : terminalTurnId!.Trim();
        if (string.IsNullOrWhiteSpace(resolvedTurnId))
            return null;

        if (State.Sessions.TryGetValue(resolvedTurnId, out var existingSession) &&
            (hasCallerSelectedTurnId || existingSession.Completed))
        {
            Logger.LogWarning(
                "[{Role}] Approval terminal turn collides with an existing session; skipping conflicting completion. session={SessionId} reasonCode={ReasonCode}",
                RoleName,
                resolvedTurnId,
                reasonCode);
            return null;
        }

        var safeReason = string.IsNullOrWhiteSpace(reasonMessage)
            ? "Tool approval failed."
            : reasonMessage.Trim();
        var runContext = State.Sessions.TryGetValue(pending.SessionId, out var session)
            ? session.RunContext?.Clone()
            : null;
        var completion = new RoleChatSessionCompletedEvent
        {
            // Refactor (iter15/cluster-028):
            //   Old pattern: terminal failure facts omitted role identity and forced downstream actor-id parsing.
            //   New principle: every role completion fact carries the typed RoleId.
            RoleId = RoleId,
            SessionId = resolvedTurnId,
            Content = BuildLlmFailureContent($"{reasonCode}: {safeReason}"),
            Prompt = BuildContinuationPrompt(pending, safeReason),
            ContentEmitted = false,
            Outcome = RoleChatSessionOutcome.Failed,
            FailureCode = reasonCode.ToUpperInvariant(),
            SafeMessage = safeReason,
            TerminalTime = CreateTerminalTimestamp(),
            RunContext = runContext,
            WorkflowLlmCompletionDeliveryContext =
                ToWorkflowLlmCompletionDeliveryContext(pending.WorkflowLlmContinuation),
            ActorId = Id,
        };
        PrepareTerminalProgress(completion);
        return completion;
    }

    private async Task RunApprovalTerminalPostTurnProcessingAsync(
        PendingToolApprovalState pending,
        RoleChatSessionCompletedEvent? completion,
        string reasonCode,
        string reasonMessage)
    {
        try
        {
            if (completion is not null)
            {
                await DeliverCompletionNotificationAsync(
                    completion.SessionId,
                    State.Sessions[completion.SessionId],
                    CancellationToken.None);
            }
        }
        finally
        {
            await RunBestEffortPostTurnProcessingAsync(
                completion?.SessionId ?? pending.SessionId,
                "approval terminal hook",
                ct => OnApprovalTerminalFailureAsync(
                    pending,
                    reasonCode,
                    reasonMessage,
                    ct));
        }
    }

    private static string ResolveApprovalContinuationTurnId(string? continuationTurnId) =>
        string.IsNullOrWhiteSpace(continuationTurnId)
            ? $"turn-{Guid.NewGuid():N}"
            : continuationTurnId.Trim();

    private static WorkflowLlmCompletionDeliveryContext? ToWorkflowLlmCompletionDeliveryContext(
        WorkflowLlmToolApprovalContinuation? continuation) =>
        continuation is null
            ? null
            : new WorkflowLlmCompletionDeliveryContext
            {
                RunId = continuation.RunId,
                StepId = continuation.StepId,
                SessionId = continuation.SessionId,
            };

    private async Task PersistApprovalRequestNotPendingAsync(string continuationTurnId)
    {
        if (State.Sessions.ContainsKey(continuationTurnId))
        {
            Logger.LogWarning(
                "[{Role}] Approval continuation turn collides with an existing session; skipping request-not-pending completion. session={SessionId}",
                RoleName,
                continuationTurnId);
            return;
        }

        var completion = new RoleChatSessionCompletedEvent
        {
            RoleId = RoleId,
            SessionId = continuationTurnId,
            Content = BuildLlmFailureContent("This approval request is no longer pending."),
            ContentEmitted = false,
            Outcome = RoleChatSessionOutcome.Failed,
            FailureCode = "APPROVAL_REQUEST_NOT_PENDING",
            SafeMessage = "This approval request is no longer pending.",
            TerminalTime = CreateTerminalTimestamp(),
        };
        await PersistCompletionWithTerminalProgressAsync(completion);
    }

    private async Task ReplayCommittedSessionWithPostTurnDeadlineAsync(
        string sessionId,
        RoleChatSessionState trackedSession)
    {
        await DeliverCompletionNotificationAsync(
            sessionId,
            trackedSession,
            CancellationToken.None);
        await RunPostTurnProcessingAsync(
            sessionId,
            "committed terminal replay",
            ct => ReplayCompletedSessionAsync(sessionId, trackedSession, ct));
    }

    private async Task ReplayCompletedSessionAsync(
        string sessionId,
        RoleChatSessionState trackedSession,
        CancellationToken ct)
    {
        var snapshot = new RoleChatSessionCompletedEvent
        {
            RoleId = RoleId,
            SessionId = sessionId,
            Content = trackedSession.FinalContent ?? string.Empty,
            ReasoningContent = trackedSession.FinalReasoningContent ?? string.Empty,
            ToolCalls = { trackedSession.ToolCalls.Select(toolCall => toolCall.Clone()) },
            Prompt = trackedSession.Prompt ?? string.Empty,
            ContentEmitted = trackedSession.ContentEmitted,
            OutputParts = { trackedSession.OutputParts.Select(part => part.Clone()) },
            Usage = trackedSession.Usage?.Clone(),
            Model = trackedSession.Model ?? string.Empty,
            ToolReceipts = { trackedSession.ToolReceipts.Select(receipt => receipt.Clone()) },
            ToolResults = { trackedSession.ToolResults.Select(result => result.Clone()) },
            Outcome = trackedSession.Outcome,
            FailureCode = trackedSession.FailureCode ?? string.Empty,
            SafeMessage = trackedSession.SafeMessage ?? string.Empty,
            AuthorizationRequired = trackedSession.AuthorizationRequired?.Clone(),
            TerminalTime = trackedSession.TerminalTime?.Clone(),
            RunContext = trackedSession.RunContext?.Clone(),
            ActorId = Id,
        };
        await PersistSessionProgressAsync(
            sessionId,
            progress => progress.Replay = new RoleChatReplayProgress { Snapshot = snapshot },
            ct);

        // Failed and uncertain retries are represented by the committed typed replay
        // snapshot. Publishing an empty TextMessageEnd would falsely present success
        // to live consumers that do not inspect the committed outcome.
        if (trackedSession.Outcome is
            RoleChatSessionOutcome.Failed or
            RoleChatSessionOutcome.OutcomeUncertain)
        {
            await PublishAsync(new RoleChatSessionErrorEvent
            {
                SessionId = sessionId,
                Outcome = trackedSession.Outcome,
                Reason = trackedSession.FailureCode ?? string.Empty,
                Message = string.IsNullOrWhiteSpace(trackedSession.SafeMessage)
                    ? trackedSession.FailureCode ?? string.Empty
                    : trackedSession.SafeMessage,
            }, TopologyAudience.Parent, ct);
            return;
        }

        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = sessionId,
            AgentId = Id,
        }, TopologyAudience.Parent, ct);

        if (IsDisplayableCompletionContent(trackedSession.FinalContent))
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = trackedSession.FinalContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent, ct);
        }

        if (!string.IsNullOrEmpty(trackedSession.FinalReasoningContent))
        {
            await PublishAsync(new TextMessageReasoningEvent
            {
                Delta = trackedSession.FinalReasoningContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent, ct);
        }

        foreach (var toolCall in trackedSession.ToolCalls)
        {
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.CallId,
                ToolName = toolCall.ToolName,
                ArgumentsJson = toolCall.ArgumentsJson,
                Presentation = ToolPresentationDescriptors.Snapshot(
                    toolCall.Presentation,
                    toolCall.ToolName),
            }, TopologyAudience.Parent, ct);
        }

        foreach (var receipt in trackedSession.ToolReceipts)
        {
            var toolResultEvent = new ToolResultEvent
            {
                CallId = receipt.CallId ?? string.Empty,
                ResultJson = receipt.ResultJson ?? string.Empty,
                Success = receipt.Status == AgentToolReceiptStatus.Success,
                Error = receipt.ErrorMessage ?? string.Empty,
                Receipt = receipt.Clone(),
            };
            await PublishAsync(toolResultEvent, TopologyAudience.Parent, ct);
        }

        foreach (var contentPart in trackedSession.OutputParts)
        {
            await PublishAsync(new MediaContentEvent
            {
                SessionId = sessionId,
                AgentId = Id,
                Part = contentPart.Clone(),
            }, TopologyAudience.Parent, ct);
        }

        await PublishUsageAsync(sessionId, trackedSession.Usage, trackedSession.Model, ct);
        await PublishCompletionAsync(sessionId, trackedSession.FinalContent ?? string.Empty, ct);
    }

    private async Task<SessionReplayRecord> PublishMissingDisplayContentWithDeadlineAsync(
        string sessionId,
        SessionReplayRecord replayRecord,
        CancellationToken ct)
    {
        if (replayRecord.ContentEmitted ||
            !IsDisplayableCompletionContent(replayRecord.Content))
        {
            return replayRecord;
        }

        ct.ThrowIfCancellationRequested();
        await PublishAsync(new TextMessageContentEvent
        {
            Delta = replayRecord.Content,
            SessionId = sessionId,
        }, TopologyAudience.Parent, ct);
        ct.ThrowIfCancellationRequested();

        return replayRecord with { ContentEmitted = true };
    }

    private Task PublishCompletionAsync(
        string sessionId,
        string completionContent,
        CancellationToken ct = default) =>
        PublishAsync(
            new TextMessageEndEvent
            {
                Content = completionContent,
                SessionId = sessionId,
            },
            TopologyAudience.Parent,
            ct);

    private Task PublishUsageAsync(
        string sessionId,
        TokenUsagePayload? usage,
        string? model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || usage == null)
            return Task.CompletedTask;

        return PublishAsync(
            new ChatTokenUsageEvent
            {
                SessionId = sessionId,
                Usage = usage.Clone(),
                Model = model ?? string.Empty,
            },
            TopologyAudience.Parent,
            ct);
    }

    private async Task DeliverPendingCompletionNotificationsAsync(CancellationToken ct)
    {
        var pending = State.Sessions
            .Where(static entry =>
                entry.Value.Completed &&
                entry.Value.CompletionNotificationDeliveryStatus is not
                    RoleChatCompletionNotificationDeliveryStatus.Dispatched and not
                    RoleChatCompletionNotificationDeliveryStatus.Expired &&
                !string.IsNullOrWhiteSpace(entry.Value.RunContext?.CompletionNotificationActorId))
            .OrderBy(static entry => entry.Value.Sequence)
            .Select(static entry => (entry.Key, State: entry.Value.Clone()))
            .ToList();

        foreach (var (sessionId, session) in pending)
        {
            try
            {
                await DeliverCompletionNotificationAsync(sessionId, session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    exception,
                    "Role chat completion notification recovery remains pending; activation will continue. actor={ActorId} session={SessionId}",
                    Id,
                    sessionId);
            }
        }
    }

    private async Task RequestIncompleteSessionFinalizationAsync(CancellationToken ct)
    {
        var candidates = State.Sessions
            .Where(entry => !entry.Value.Completed)
            .OrderBy(static entry => entry.Value.Sequence)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (await TryRequestCheckpointRecoveryAsync(candidate.Key, candidate.Value, ct))
                continue;

            await PublishAsync(new RoleChatIncompleteSessionFinalizationRequested
            {
                SessionId = candidate.Key,
                ExpectedLastProgressSequence = candidate.Value.LastProgressSequence,
            }, TopologyAudience.Self, ct);
        }
    }

    protected async Task<bool> TryRequestCheckpointRecoveryAsync(
        string sessionId,
        RoleChatSessionState session,
        CancellationToken ct)
    {
        var checkpoint = session.RecoveryCheckpoint;
        if (!IsValidRecoveryCheckpoint(checkpoint))
            return false;

        if (checkpoint.Stage == RoleChatRecoveryCheckpointStage.WaitingApproval)
        {
            return State.PendingApproval is not null &&
                   string.Equals(State.PendingApproval.SessionId, sessionId, StringComparison.Ordinal) &&
                   string.Equals(
                       State.PendingApproval.OperationId,
                       checkpoint.PendingOperationId,
                       StringComparison.Ordinal);
        }

        var operationId = checkpoint.Stage == RoleChatRecoveryCheckpointStage.ContinuationPrepared
            ? checkpoint.PendingOperationId
            : string.Empty;
        await PublishAsync(new RoleChatRecoveryContinuationRequested
        {
            SessionId = sessionId,
            ExpectedCheckpointGeneration = checkpoint.Generation,
            OperationId = operationId,
        }, TopologyAudience.Self, ct);
        return true;
    }

    private bool IsValidRecoveryCheckpoint(RoleChatRecoveryCheckpoint? checkpoint)
    {
        if (checkpoint is null ||
            checkpoint.Generation <= 0 ||
            checkpoint.Stage == RoleChatRecoveryCheckpointStage.Unspecified ||
            !System.Enum.IsDefined(checkpoint.Stage))
        {
            return false;
        }

        if (checkpoint!.PayloadExpiresAtUnixMs > 0 &&
            checkpoint.PayloadExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            return false;
        }

        return checkpoint.Stage switch
        {
            RoleChatRecoveryCheckpointStage.WaitingApproval =>
                !string.IsNullOrWhiteSpace(checkpoint.PendingOperationId),
            RoleChatRecoveryCheckpointStage.ContinuationPrepared =>
                !string.IsNullOrWhiteSpace(checkpoint.PendingOperationId) &&
                !string.IsNullOrWhiteSpace(checkpoint.ContinuationSessionId),
            _ => true,
        };
    }

    protected async Task<bool> TryFinalizeIncompleteSessionAsync(
        string? sessionId,
        long expectedLastProgressSequence)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId) ||
            !State.Sessions.TryGetValue(normalizedSessionId, out var session) ||
            session.Completed ||
            session.LastProgressSequence != expectedLastProgressSequence ||
            IsPendingApprovalSession(normalizedSessionId))
        {
            return false;
        }

        var hasCommittedProgress = session.LastProgressSequence > 0 ||
                                   session.RecoveryCheckpoint?.ToolIntents.Count > 0;
        var completion = new RoleChatSessionCompletedEvent
        {
            RoleId = RoleId,
            SessionId = normalizedSessionId,
            Prompt = session.Prompt ?? string.Empty,
            ContentEmitted = session.ContentEmitted,
            Outcome = hasCommittedProgress
                ? RoleChatSessionOutcome.OutcomeUncertain
                : RoleChatSessionOutcome.Failed,
            FailureCode = hasCommittedProgress
                ? UncertainSessionFailureCode
                : OrphanedSessionFailureCode,
            SafeMessage = hasCommittedProgress
                ? "The chat session was interrupted after execution started, so its outcome could not be confirmed."
                : "The chat session was interrupted before execution started. Please try again.",
            TerminalTime = CreateTerminalTimestamp(),
            RunContext = session.RunContext?.Clone(),
            WorkflowLlmCompletionDeliveryContext =
                ResolveWorkflowCompletionDeliveryContext(session),
            ActorId = Id,
        };

        Logger.LogWarning(
            "[{Role}] Finalizing incomplete chat session without replay. session={SessionId} progressSequence={ProgressSequence} outcome={Outcome}",
            RoleName,
            normalizedSessionId,
            session.LastProgressSequence,
            completion.Outcome);
        await PersistCompletionWithTerminalProgressAsync(completion);
        await DeliverCompletionNotificationAsync(
            normalizedSessionId,
            State.Sessions[normalizedSessionId],
            CancellationToken.None);
        return true;
    }

    private bool IsPendingApprovalSession(string sessionId) =>
        State.PendingApproval != null &&
        string.Equals(State.PendingApproval.SessionId, sessionId, StringComparison.Ordinal);

    private async Task DeliverCompletionNotificationAsync(
        string sessionId,
        RoleChatSessionState session,
        CancellationToken ct,
        int? deliveryAttempt = null)
    {
        var runContext = session.RunContext?.Clone();
        if (!session.Completed ||
            session.CompletionNotificationDeliveryStatus is
                RoleChatCompletionNotificationDeliveryStatus.Dispatched or
                RoleChatCompletionNotificationDeliveryStatus.Expired ||
            string.IsNullOrWhiteSpace(runContext?.CompletionNotificationActorId))
        {
            return;
        }

        var deliveryId = ResolveCompletionNotificationDeliveryId(sessionId, runContext);
        var attempt = Math.Max(session.CompletionNotificationAttempt, deliveryAttempt ?? 0);
        var now = _timeProvider.GetUtcNow();
        if (HasElapsedCompletionNotificationDeadline(runContext, now))
        {
            await PersistDomainEventAsync(new RoleChatCompletionNotificationExpiredEvent
            {
                SessionId = sessionId,
                DeliveryId = deliveryId,
                Attempt = attempt,
                ExpiredAtUnixTimeMs = now.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        var completion = new RoleChatSessionCompletedEvent
        {
            RoleId = State.RoleId ?? string.Empty,
            ActorId = Id,
            SessionId = sessionId,
            Content = session.FinalContent ?? string.Empty,
            ReasoningContent = session.FinalReasoningContent ?? string.Empty,
            Prompt = session.Prompt ?? string.Empty,
            ContentEmitted = session.ContentEmitted,
            ToolCalls = { session.ToolCalls.Select(static toolCall => toolCall.Clone()) },
            OutputParts = { session.OutputParts.Select(static part => part.Clone()) },
            ToolReceipts = { session.ToolReceipts.Select(static receipt => receipt.Clone()) },
            Usage = session.Usage?.Clone(),
            Model = session.Model ?? string.Empty,
            Outcome = session.Outcome,
            FailureCode = session.FailureCode ?? string.Empty,
            SafeMessage = session.SafeMessage ?? string.Empty,
            AuthorizationRequired = session.AuthorizationRequired?.Clone(),
            TerminalTime = session.TerminalTime?.Clone(),
            RunContext = runContext,
        };
        using var deliveryDeadlineCts = CreatePostTurnProcessingCancellationSource();
        using var deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            deliveryDeadlineCts.Token);
        var deliveryCt = deliveryCts.Token;
        try
        {
            await SendToAsync(
                    runContext.CompletionNotificationActorId.Trim(),
                    completion,
                    deliveryCt,
                    new EventEnvelopePublishOptions
                    {
                        Delivery = new EventEnvelopeDeliveryOptions
                        {
                            OperationId = string.Create(
                                CultureInfo.InvariantCulture,
                                $"role-chat-terminal:{deliveryId}:outcome:{(int)completion.Outcome}"),
                        },
                    })
                .WaitAsync(deliveryCt);
        }
        catch (OperationCanceledException ex) when (
            deliveryDeadlineCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            Logger.LogWarning(
                ex,
                "Role chat completion delivery exceeded its deadline; scheduling durable retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                sessionId,
                deliveryId,
                attempt);
            await ScheduleCompletionNotificationRetryAsync(
                sessionId,
                runContext,
                deliveryId,
                attempt,
                CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Role chat completion delivery failed; scheduling durable retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                sessionId,
                deliveryId,
                attempt);
            await ScheduleCompletionNotificationRetryAsync(
                sessionId,
                runContext,
                deliveryId,
                attempt,
                CancellationToken.None);
            return;
        }

        try
        {
            await PersistDomainEventAsync(new RoleChatCompletionNotificationDispatchedEvent
            {
                SessionId = sessionId,
                RunContext = runContext,
                DeliveryId = deliveryId,
                Attempt = attempt,
                DispatchedAtUnixTimeMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Role chat completion dispatch acknowledgement failed; scheduling deduplicated retry. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                sessionId,
                deliveryId,
                attempt);
            await ScheduleCompletionNotificationRetryAsync(
                sessionId,
                runContext,
                deliveryId,
                attempt,
                CancellationToken.None);
        }
    }

    private async Task ScheduleCompletionNotificationRetryAsync(
        string sessionId,
        RoleChatRunContext runContext,
        string deliveryId,
        int failedAttempt,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (HasElapsedCompletionNotificationDeadline(runContext, now))
        {
            await PersistDomainEventAsync(new RoleChatCompletionNotificationExpiredEvent
            {
                SessionId = sessionId,
                DeliveryId = deliveryId,
                Attempt = failedAttempt,
                ExpiredAtUnixTimeMs = now.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        var currentAttempt = State.Sessions.TryGetValue(sessionId, out var current)
            ? current.CompletionNotificationAttempt
            : 0;
        var attempt = Math.Max(currentAttempt, failedAttempt) + 1;
        var retryDelayMs = CalculateCompletionNotificationRetryDelayMs(attempt);
        var dueTime = TimeSpan.FromMilliseconds(
            runContext.CompletionNotificationExpiresAtUnixMs > 0
                ? Math.Min(
                    retryDelayMs,
                    runContext.CompletionNotificationExpiresAtUnixMs - now.ToUnixTimeMilliseconds())
                : retryDelayMs);
        var retryAt = now.Add(dueTime);
        var callbackId = BuildCompletionNotificationRetryCallbackId(sessionId, deliveryId);
        var retryFired = new RoleChatCompletionNotificationRetryFiredEvent
        {
            SessionId = sessionId,
            DeliveryId = deliveryId,
            Attempt = attempt,
        };
        var retryOptions = BuildCompletionNotificationRetryOptions(callbackId, attempt);
        try
        {
            var scheduled = await TrySchedulePostTurnDurableTimeoutAsync(
                callbackId,
                dueTime,
                retryFired,
                retryOptions,
                ct);
            if (!scheduled)
            {
                Logger.LogWarning(
                    "Role chat completion retry scheduling exceeded its deadline; preserving the outbox for activation recovery. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                    Id,
                    sessionId,
                    deliveryId,
                    attempt);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var canPublishImmediateRecovery =
                current is
                {
                    CompletionNotificationDeliveryStatus:
                        RoleChatCompletionNotificationDeliveryStatus.Prepared,
                    CompletionNotificationAttempt: 0,
                } &&
                attempt == 1;
            Logger.LogWarning(
                ex,
                canPublishImmediateRecovery
                    ? "Role chat completion durable retry scheduling failed; publishing one immediate recovery continuation. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}"
                    : "Role chat completion durable retry scheduling failed; preserving the outbox for activation recovery. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                sessionId,
                deliveryId,
                attempt);
            if (canPublishImmediateRecovery)
            {
                using var recoveryDeadlineCts = CreatePostTurnProcessingCancellationSource();
                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    recoveryDeadlineCts.Token);
                var recoveryCt = recoveryCts.Token;
                try
                {
                    await PublishAsync(
                            retryFired,
                            TopologyAudience.Self,
                            recoveryCt,
                            options: retryOptions)
                        .WaitAsync(recoveryCt);
                    recoveryCt.ThrowIfCancellationRequested();
                }
                catch (Exception recoveryEx)
                {
                    Logger.LogWarning(
                        recoveryEx,
                        "Role chat completion immediate recovery publication failed or exceeded its deadline; preserving the outbox for activation recovery. actor={ActorId} session={SessionId} delivery={DeliveryId} attempt={Attempt}",
                        Id,
                        sessionId,
                        deliveryId,
                        attempt);
                }
            }
            throw;
        }
        await PersistDomainEventAsync(new RoleChatCompletionNotificationRetryScheduledEvent
        {
            SessionId = sessionId,
            DeliveryId = deliveryId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(retryAt),
        }, ct);
    }

    private static string BuildCompletionNotificationRetryCallbackId(string sessionId, string deliveryId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            CompletionNotificationRetryCallbackPrefix,
            sessionId,
            deliveryId);

    private static EventEnvelopePublishOptions BuildCompletionNotificationRetryOptions(
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

    private static long CalculateCompletionNotificationRetryDelayMs(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 7);
        var delayMs = CompletionNotificationRetryInitialDelayMs * (1L << exponent);
        return Math.Min(delayMs, CompletionNotificationRetryMaxDelayMs);
    }

    private static bool HasElapsedCompletionNotificationDeadline(
        RoleChatRunContext runContext,
        DateTimeOffset now) =>
        runContext.CompletionNotificationExpiresAtUnixMs > 0 &&
        runContext.CompletionNotificationExpiresAtUnixMs <= now.ToUnixTimeMilliseconds();

    private static string ResolveCompletionNotificationDeliveryId(
        string sessionId,
        RoleChatRunContext? runContext)
    {
        if (!string.IsNullOrWhiteSpace(runContext?.CompletionNotificationDeliveryId))
            return runContext.CompletionNotificationDeliveryId.Trim();
        if (!string.IsNullOrWhiteSpace(runContext?.RunId) ||
            !string.IsNullOrWhiteSpace(runContext?.CommandId))
        {
            return $"{runContext?.RunId}:{runContext?.CommandId}";
        }

        return sessionId;
    }

    private static bool IsDisplayableCompletionContent(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        !content.StartsWith(LlmFailureContentPrefix, StringComparison.Ordinal) &&
        !content.StartsWith("LLM request failed", StringComparison.Ordinal);

    private RoleChatSessionState? ResolveTrackedSession(ChatRequestEvent request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return null;

        if (!State.Sessions.TryGetValue(request.SessionId, out var trackedSession))
            return null;

        if (!string.Equals(trackedSession.Prompt, request.Prompt, StringComparison.Ordinal))
        {
            throw new RoleChatCommandAttemptRejectionException(
                RoleChatCommandAttemptRejectionReason.PromptMismatch,
                $"Session '{request.SessionId}' already exists with a different prompt.");
        }

        if (!HaveMatchingInputParts(trackedSession.InputParts, request.InputParts))
        {
            throw new RoleChatCommandAttemptRejectionException(
                RoleChatCommandAttemptRejectionReason.InputPartsMismatch,
                $"Session '{request.SessionId}' already exists with different multimodal input.");
        }

        if (!RoleChatRunContextsEqual(trackedSession.RunContext, request.RunContext))
        {
            throw new InvalidOperationException(
                $"Session '{request.SessionId}' already exists with a different Run context.");
        }

        return trackedSession;
    }

    private sealed class RoleChatCommandAttemptRejectionException(
        RoleChatCommandAttemptRejectionReason reason,
        string message) : InvalidOperationException(message)
    {
        public RoleChatCommandAttemptRejectionReason Reason { get; } = reason;
    }

    private static string ResolveCommandAttemptId(ChatRequestEvent request) =>
        string.IsNullOrWhiteSpace(request.CommandAttemptId)
            ? $"role-chat-attempt-{Guid.NewGuid():N}"
            : request.CommandAttemptId.Trim();

    private static bool RoleChatRunContextsEqual(RoleChatRunContext? left, RoleChatRunContext? right) =>
        left == null && right == null ||
        left != null && right != null && left.Equals(right);

    private static RoleGAgentState ApplyInitializeRoleAgent(
        RoleGAgentState current,
        InitializeRoleAgentEvent evt)
    {
        var next = current.Clone();
        var overrides = EnsureConfigOverrides(next);
        // Refactor (iter15/cluster-028):
        //   Old pattern: role initialization persisted presentation/config fields but not typed workflow role identity.
        //   New principle: RoleId is a first-class protobuf state field owned by the role actor.
        next.RoleId = evt.RoleId ?? string.Empty;
        next.RoleName = evt.RoleName ?? string.Empty;
        next.EventModules = NormalizeModuleExtensionText(evt.EventModules);
        next.EventRoutes = NormalizeModuleExtensionText(evt.EventRoutes);
        next.VoiceSessionDefaults.Clear();
        foreach (var entry in evt.VoiceSessionDefaults)
        {
            var moduleName = NormalizeModuleExtensionText(entry.Key);
            if (string.IsNullOrWhiteSpace(moduleName))
                continue;

            next.VoiceSessionDefaults[moduleName] = entry.Value?.Clone() ?? new VoiceSessionDefaults();
        }
        overrides.ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? string.Empty : evt.ProviderName.Trim();
        overrides.Model = string.IsNullOrWhiteSpace(evt.Model) ? string.Empty : evt.Model.Trim();
        overrides.SystemPrompt = evt.SystemPrompt ?? string.Empty;
        if (evt.HasTemperature)
            overrides.Temperature = evt.Temperature;
        else
            overrides.ClearTemperature();
        if (evt.MaxTokens > 0)
            overrides.MaxTokens = evt.MaxTokens;
        else
            overrides.ClearMaxTokens();
        if (evt.MaxToolRounds > 0)
            overrides.MaxToolRounds = evt.MaxToolRounds;
        else
            overrides.ClearMaxToolRounds();
        if (evt.MaxHistoryMessages > 0)
            overrides.MaxHistoryMessages = evt.MaxHistoryMessages;
        else
            overrides.ClearMaxHistoryMessages();
        if (evt.MaxPromptTokenBudget > 0)
            overrides.MaxPromptTokenBudget = evt.MaxPromptTokenBudget;
        else
            overrides.ClearMaxPromptTokenBudget();
        if (evt.CompressionThreshold > 0)
            overrides.CompressionThreshold = evt.CompressionThreshold;
        else
            overrides.ClearCompressionThreshold();
        if (evt.EnableSummarization)
            overrides.EnableSummarization = true;
        else
            overrides.ClearEnableSummarization();
        return next;
    }

    private static RoleGAgentState ApplySystemSkillOverlayMaterialized(
        RoleGAgentState current,
        SystemSkillOverlayMaterializedEvent evt)
    {
        // Retired (issue #2498): the overlay is no longer stored in actor state. Kept as a no-op reducer
        // so historical journals containing this event still replay without an unknown-event error.
        return current;
    }

    private static RoleGAgentState ApplyChatSessionStarted(
        RoleGAgentState current,
        RoleChatSessionStartedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return current;

        var next = current.Clone();
        var sessions = next.Sessions;
        if (!sessions.TryGetValue(evt.SessionId, out var session))
        {
            session = new RoleChatSessionState();
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }
        else if (session.Sequence <= 0)
        {
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }

        session.Prompt = evt.Prompt ?? string.Empty;
        session.InputParts.Clear();
        session.InputParts.Add(evt.InputParts);
        session.RunContext = evt.RunContext?.Clone();
        session.ScopeId = evt.ScopeId ?? string.Empty;
        session.RecoveryCheckpoint = evt.RecoveryCheckpoint?.Clone();
        session.DirectParentRoleChatSessionId =
            evt.RecoveryCheckpoint?.DirectParentRoleChatSessionId ?? string.Empty;
        session.WorkflowLlmCompletionDeliveryContext =
            evt.RecoveryCheckpoint?.WorkflowLlmCompletionDeliveryContext?.Clone();
        sessions[evt.SessionId] = session;
        TrimTrackedSessions(next, evt.SessionId);
        return next;
    }

    private static RoleGAgentState ApplyChatRecoveryCheckpointUpdated(
        RoleGAgentState current,
        RoleChatRecoveryCheckpointUpdatedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId) ||
            evt.Checkpoint is null ||
            !current.Sessions.TryGetValue(evt.SessionId, out var currentSession) ||
            currentSession.Completed)
        {
            return current;
        }

        var currentGeneration = currentSession.RecoveryCheckpoint?.Generation ?? 0;
        if (evt.ExpectedGeneration != currentGeneration ||
            evt.Checkpoint.Generation != currentGeneration + 1 ||
            evt.Checkpoint.Stage is RoleChatRecoveryCheckpointStage.Unspecified)
        {
            return current;
        }

        var next = current.Clone();
        next.Sessions[evt.SessionId].RecoveryCheckpoint = evt.Checkpoint.Clone();
        if (evt.Checkpoint.WorkflowLlmCompletionDeliveryContext is not null)
        {
            next.Sessions[evt.SessionId].WorkflowLlmCompletionDeliveryContext =
                evt.Checkpoint.WorkflowLlmCompletionDeliveryContext.Clone();
        }
        return next;
    }

    private static RoleGAgentState ApplyAgentProfileTurnAuthorityCommitted(
        RoleGAgentState current,
        AgentProfileTurnAuthorityCommittedEvent evt) =>
        TryApplyAgentProfileTurnAuthorityCommitted(current, evt, out var next) ? next : current;

    private static bool TryApplyAgentProfileTurnAuthorityCommitted(
        RoleGAgentState current,
        AgentProfileTurnAuthorityCommittedEvent evt,
        out RoleGAgentState next)
    {
        next = current;
        if (evt.Authority?.ReconciliationKey is null)
            return false;

        var incoming = CanonicalizeTurnAuthority(evt.Authority);
        var sessionId = incoming.ReconciliationKey.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || incoming.ReconciliationKey.Attempt <= 0 ||
            !current.Sessions.TryGetValue(sessionId, out var session) || session.Completed ||
            AuthorityRank(incoming.AuthorityKind) < 0 ||
            !HasConsistentAuthorityKindAndCeiling(incoming))
        {
            return false;
        }

        var active = current.AgentProfileTurnAuthority;
        AgentProfileTurnAuthorityState accepted;
        switch (evt.CommitKind)
        {
            case AgentProfileTurnAuthorityCommitKind.Initial:
                if (incoming.ReconciliationKey.Attempt != 1 || !CanApplyInitialAuthority(current, active, incoming))
                    return false;
                accepted = incoming;
                break;
            case AgentProfileTurnAuthorityCommitKind.RetryStarted:
                if (!CanApplyRetryAuthority(active, incoming))
                    return false;
                accepted = incoming;
                break;
            case AgentProfileTurnAuthorityCommitKind.Reconcile:
                if (!CanApplyReconciledAuthority(active, incoming))
                    return false;
                accepted = MergeReconciledAuthority(active!, incoming);
                break;
            default:
                return false;
        }

        if (active is not null && active.Equals(accepted))
            return true;

        next = current.Clone();
        next.AgentProfileTurnAuthority = accepted;
        return true;
    }

    private static bool CanApplyInitialAuthority(
        RoleGAgentState current,
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming)
    {
        if (active is null)
            return true;

        if (HasSameReconciliationKey(active, incoming))
            return CanonicalizeTurnAuthority(active).Equals(incoming);

        if (IsLegacyRestrictedEmptyAuthority(incoming))
            return true;

        if (!current.Sessions.TryGetValue(active.ReconciliationKey.SessionId, out var activeSession) ||
            !current.Sessions.TryGetValue(incoming.ReconciliationKey.SessionId, out var incomingSession))
        {
            return false;
        }

        return incomingSession.Sequence > activeSession.Sequence;
    }

    private static bool CanApplyRetryAuthority(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming)
    {
        if (active?.ReconciliationKey is null || active.SelectedExactSkillRef is null ||
            !string.Equals(
                active.ReconciliationKey.SessionId,
                incoming.ReconciliationKey.SessionId,
                StringComparison.Ordinal) ||
            incoming.ReconciliationKey.Attempt != active.ReconciliationKey.Attempt + 1)
        {
            return false;
        }

        var expected = CanonicalizeTurnAuthority(active);
        expected.ReconciliationKey.Attempt = incoming.ReconciliationKey.Attempt;
        return expected.Equals(incoming);
    }

    private static bool CanApplyReconciledAuthority(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming)
    {
        if (active?.ReconciliationKey is null || !HasSameReconciliationKey(active, incoming) ||
            !Equals(active.CandidateRoute, incoming.CandidateRoute) ||
            !Equals(active.SelectedExactSkillRef, incoming.SelectedExactSkillRef) ||
            AuthorityRank(incoming.AuthorityKind) > AuthorityRank(active.AuthorityKind))
        {
            return false;
        }

        var activeNames = active.AuthorityCeilingToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return incoming.AuthorityCeilingToolNames.All(activeNames.Contains);
    }

    private static AgentProfileTurnAuthorityState MergeReconciledAuthority(
        AgentProfileTurnAuthorityState active,
        AgentProfileTurnAuthorityState incoming)
    {
        var accepted = incoming.Clone();
        accepted.DegradationReasons.Clear();
        accepted.DegradationReasons.Add(
            active.DegradationReasons
                .Concat(incoming.DegradationReasons)
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        return accepted;
    }

    private static AgentProfileTurnAuthorityState CanonicalizeTurnAuthority(
        AgentProfileTurnAuthorityState authority)
    {
        var canonical = authority.Clone();
        canonical.AuthorityCeilingToolNames.Clear();
        canonical.AuthorityCeilingToolNames.Add(
            authority.AuthorityCeilingToolNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.Ordinal));
        canonical.DegradationReasons.Clear();
        canonical.DegradationReasons.Add(
            authority.DegradationReasons
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        return canonical;
    }

    private static bool HasSameReconciliationKey(
        AgentProfileTurnAuthorityState left,
        AgentProfileTurnAuthorityState right) =>
        left.ReconciliationKey is not null &&
        right.ReconciliationKey is not null &&
        left.ReconciliationKey.Attempt == right.ReconciliationKey.Attempt &&
        string.Equals(
            left.ReconciliationKey.SessionId,
            right.ReconciliationKey.SessionId,
            StringComparison.Ordinal);

    private static bool IsLegacyRestrictedEmptyAuthority(AgentProfileTurnAuthorityState authority) =>
        authority.AuthorityKind == AgentProfileTurnAuthorityKind.RestrictedEmpty &&
        authority.CandidateRoute is null &&
        authority.SelectedExactSkillRef is null &&
        authority.AuthorityCeilingToolNames.Count == 0 &&
        authority.DegradationReasons.Count == 1 &&
        authority.DegradationReasons[0] == AgentProfileTurnDegradationReason.LegacyAuthorityMissing;

    private static bool HasConsistentAuthorityKindAndCeiling(AgentProfileTurnAuthorityState authority) =>
        authority.AuthorityKind switch
        {
            AgentProfileTurnAuthorityKind.RestrictedEmpty => authority.AuthorityCeilingToolNames.Count == 0,
            AgentProfileTurnAuthorityKind.Recovery => authority.AuthorityCeilingToolNames.Count > 0,
            AgentProfileTurnAuthorityKind.Selected => true,
            _ => false,
        };

    private static int AuthorityRank(AgentProfileTurnAuthorityKind kind) => kind switch
    {
        AgentProfileTurnAuthorityKind.RestrictedEmpty => 1,
        AgentProfileTurnAuthorityKind.Recovery => 2,
        AgentProfileTurnAuthorityKind.Selected => 3,
        _ => -1,
    };

    private static RoleGAgentState ApplyChatSessionCompleted(
        RoleGAgentState current,
        RoleChatSessionCompletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return current;

        var isTerminalReconciliation =
            current.Sessions.TryGetValue(evt.SessionId, out var existingTerminal) &&
            existingTerminal.Completed &&
            CanReconcileTerminalOutcome(existingTerminal.Outcome, evt.Outcome);
        if (existingTerminal is { Completed: true } && !isTerminalReconciliation)
        {
            return current;
        }

        if (!isTerminalReconciliation &&
            HasPendingCompletionNotification(current, evt.SessionId))
        {
            return current;
        }

        var next = current.Clone();
        if (!next.Sessions.TryGetValue(evt.SessionId, out var session))
        {
            session = new RoleChatSessionState();
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }
        else if (session.Sequence <= 0)
        {
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }

        var runContextMatches = RoleChatRunContextsEqual(session.RunContext, evt.RunContext);
        var completionNotificationDeliveryStatus = runContextMatches && !isTerminalReconciliation
            ? session.CompletionNotificationDeliveryStatus
            : RoleChatCompletionNotificationDeliveryStatus.Unspecified;
        var workflowCompletionContextMatches = Equals(
            session.WorkflowLlmCompletionDeliveryContext,
            evt.WorkflowLlmCompletionDeliveryContext);
        var workflowCompletionDeliveryStatus = workflowCompletionContextMatches &&
                                               !isTerminalReconciliation
            ? session.WorkflowLlmCompletionDeliveryStatus
            : WorkflowLlmCompletionDeliveryStatus.Unspecified;
        session.Completed = true;
        session.Prompt = evt.Prompt ?? session.Prompt ?? string.Empty;
        session.FinalContent = evt.Content ?? string.Empty;
        session.FinalReasoningContent = evt.ReasoningContent ?? string.Empty;
        session.ContentEmitted = evt.ContentEmitted;
        session.ToolCalls.Clear();
        session.ToolCalls.Add(evt.ToolCalls);
        session.OutputParts.Clear();
        session.OutputParts.Add(evt.OutputParts);
        session.ToolReceipts.Clear();
        session.ToolReceipts.Add(evt.ToolReceipts.Select(receipt => receipt.Clone()));
        session.ToolResults.Clear();
        session.ToolResults.Add(evt.ToolResults.Select(result => result.Clone()));
        session.Usage = evt.Usage?.Clone();
        session.Model = evt.Model ?? string.Empty;
        session.Outcome = evt.Outcome;
        session.AuthorizationRequired = evt.AuthorizationRequired?.Clone();
        session.FailureCode = evt.FailureCode ?? string.Empty;
        session.SafeMessage = evt.SafeMessage ?? string.Empty;
        session.TerminalTime = evt.TerminalTime?.Clone();
        session.RunContext = evt.RunContext?.Clone();
        session.RecoveryCheckpoint = null;
        session.WorkflowLlmCompletionDeliveryContext =
            evt.WorkflowLlmCompletionDeliveryContext?.Clone();
        session.CompletionNotificationDeliveryStatus =
            !string.IsNullOrWhiteSpace(session.RunContext?.CompletionNotificationActorId)
                ? completionNotificationDeliveryStatus ==
                  RoleChatCompletionNotificationDeliveryStatus.Unspecified
                    ? RoleChatCompletionNotificationDeliveryStatus.Prepared
                    : completionNotificationDeliveryStatus
                : RoleChatCompletionNotificationDeliveryStatus.Unspecified;
        if (!runContextMatches || isTerminalReconciliation)
        {
            session.CompletionNotificationAttempt = 0;
            session.CompletionNotificationRetryCallbackId = string.Empty;
            session.CompletionNotificationRetryAt = null;
        }
        session.WorkflowLlmCompletionDeliveryStatus =
            session.WorkflowLlmCompletionDeliveryContext is not null
                ? workflowCompletionDeliveryStatus == WorkflowLlmCompletionDeliveryStatus.Unspecified
                    ? WorkflowLlmCompletionDeliveryStatus.Prepared
                    : workflowCompletionDeliveryStatus
                : WorkflowLlmCompletionDeliveryStatus.Unspecified;
        if (!workflowCompletionContextMatches || isTerminalReconciliation)
        {
            session.WorkflowLlmCompletionDeliveryAttempt = 0;
            session.WorkflowLlmCompletionDeliveryRetryCallbackId = string.Empty;
            session.WorkflowLlmCompletionDeliveryRetryAt = null;
        }
        foreach (var progress in evt.TerminalProgress)
        {
            if (string.Equals(progress.SessionId, evt.SessionId, StringComparison.Ordinal) &&
                progress.Sequence > session.LastProgressSequence)
            {
                session.LastProgressSequence = progress.Sequence;
            }
        }
        next.Sessions[evt.SessionId] = session;
        return next;
    }

    private static bool CanReconcileTerminalOutcome(
        RoleChatSessionOutcome current,
        RoleChatSessionOutcome candidate) =>
        current == RoleChatSessionOutcome.OutcomeUncertain &&
        candidate is RoleChatSessionOutcome.Completed or RoleChatSessionOutcome.Failed;

    private static RoleGAgentState ApplyChatSessionProgressed(
        RoleGAgentState current,
        RoleChatSessionProgressedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId) || evt.Sequence <= 0)
            return current;

        var next = current.Clone();
        if (!next.Sessions.TryGetValue(evt.SessionId, out var session))
        {
            session = new RoleChatSessionState();
            next.Sessions[evt.SessionId] = session;
        }

        if (evt.Sequence <= session.LastProgressSequence)
            return current;

        session.LastProgressSequence = evt.Sequence;
        return next;
    }

    private static RoleGAgentState ApplyCompletionNotificationDispatched(
        RoleGAgentState current,
        RoleChatCompletionNotificationDispatchedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId) ||
            !current.Sessions.TryGetValue(evt.SessionId, out var session))
        {
            return current;
        }

        var deliveryMatches = !string.IsNullOrWhiteSpace(evt.DeliveryId)
            ? string.Equals(
                ResolveCompletionNotificationDeliveryId(evt.SessionId, session.RunContext),
                evt.DeliveryId,
                StringComparison.Ordinal)
            : RoleChatRunContextsEqual(session.RunContext, evt.RunContext);
        if (!deliveryMatches)
            return current;

        if (!IsEligibleCompletionNotificationTerminalTransition(session, evt.Attempt))
            return current;

        var next = current.Clone();
        var nextSession = next.Sessions[evt.SessionId];
        nextSession.CompletionNotificationDeliveryStatus =
            RoleChatCompletionNotificationDeliveryStatus.Dispatched;
        nextSession.CompletionNotificationAttempt = Math.Max(
            nextSession.CompletionNotificationAttempt,
            evt.Attempt);
        nextSession.CompletionNotificationRetryCallbackId = string.Empty;
        nextSession.CompletionNotificationRetryAt = null;
        return next;
    }

    private static RoleGAgentState ApplyCompletionNotificationRetryScheduled(
        RoleGAgentState current,
        RoleChatCompletionNotificationRetryScheduledEvent evt)
    {
        if (!TryResolveCompletionNotificationSession(current, evt.SessionId, evt.DeliveryId, out var session) ||
            session == null ||
            !IsCompletionNotificationDeliveryPending(session) ||
            evt.Attempt != session.CompletionNotificationAttempt + 1)
        {
            return current;
        }

        var next = current.Clone();
        var nextSession = next.Sessions[evt.SessionId];
        nextSession.CompletionNotificationDeliveryStatus =
            RoleChatCompletionNotificationDeliveryStatus.RetryScheduled;
        nextSession.CompletionNotificationAttempt = evt.Attempt;
        nextSession.CompletionNotificationRetryCallbackId = evt.CallbackId ?? string.Empty;
        nextSession.CompletionNotificationRetryAt = evt.RetryAt?.Clone();
        return next;
    }

    private static RoleGAgentState ApplyCompletionNotificationExpired(
        RoleGAgentState current,
        RoleChatCompletionNotificationExpiredEvent evt)
    {
        if (!TryResolveCompletionNotificationSession(current, evt.SessionId, evt.DeliveryId, out var session) ||
            session == null ||
            !IsEligibleCompletionNotificationTerminalTransition(session, evt.Attempt))
        {
            return current;
        }

        var next = current.Clone();
        var nextSession = next.Sessions[evt.SessionId];
        nextSession.CompletionNotificationDeliveryStatus =
            RoleChatCompletionNotificationDeliveryStatus.Expired;
        nextSession.CompletionNotificationAttempt = Math.Max(
            nextSession.CompletionNotificationAttempt,
            evt.Attempt);
        nextSession.CompletionNotificationRetryCallbackId = string.Empty;
        nextSession.CompletionNotificationRetryAt = null;
        return next;
    }

    private static RoleGAgentState ApplyWorkflowLlmCompletionDeliveryRetryScheduled(
        RoleGAgentState current,
        WorkflowLlmCompletionDeliveryRetryScheduledEvent evt)
    {
        if (!TryResolveWorkflowLlmCompletionDeliverySession(
                current,
                evt.SessionId,
                evt.DeliveryId,
                out var session) ||
            session is null ||
            !IsWorkflowLlmCompletionDeliveryPending(session) ||
            evt.Attempt != session.WorkflowLlmCompletionDeliveryAttempt + 1)
        {
            return current;
        }

        var next = current.Clone();
        var nextSession = next.Sessions[evt.SessionId];
        nextSession.WorkflowLlmCompletionDeliveryStatus =
            WorkflowLlmCompletionDeliveryStatus.RetryScheduled;
        nextSession.WorkflowLlmCompletionDeliveryAttempt = evt.Attempt;
        nextSession.WorkflowLlmCompletionDeliveryRetryCallbackId = evt.CallbackId ?? string.Empty;
        nextSession.WorkflowLlmCompletionDeliveryRetryAt = evt.RetryAt?.Clone();
        return next;
    }

    private static RoleGAgentState ApplyWorkflowLlmCompletionDeliveryDispatched(
        RoleGAgentState current,
        WorkflowLlmCompletionDeliveryDispatchedEvent evt)
    {
        if (!TryResolveWorkflowLlmCompletionDeliverySession(
                current,
                evt.SessionId,
                evt.DeliveryId,
                out var session) ||
            session is null ||
            !IsWorkflowLlmCompletionDeliveryPending(session) ||
            (evt.Attempt != session.WorkflowLlmCompletionDeliveryAttempt &&
             evt.Attempt != session.WorkflowLlmCompletionDeliveryAttempt + 1))
        {
            return current;
        }

        var next = current.Clone();
        var nextSession = next.Sessions[evt.SessionId];
        nextSession.WorkflowLlmCompletionDeliveryStatus =
            WorkflowLlmCompletionDeliveryStatus.Dispatched;
        nextSession.WorkflowLlmCompletionDeliveryAttempt = Math.Max(
            nextSession.WorkflowLlmCompletionDeliveryAttempt,
            evt.Attempt);
        nextSession.WorkflowLlmCompletionDeliveryRetryCallbackId = string.Empty;
        nextSession.WorkflowLlmCompletionDeliveryRetryAt = null;
        TrimTrackedSessions(next);
        return next;
    }

    private static bool TryResolveWorkflowLlmCompletionDeliverySession(
        RoleGAgentState state,
        string sessionId,
        string deliveryId,
        out RoleChatSessionState? session)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            state.Sessions.TryGetValue(sessionId, out session) &&
            session.WorkflowLlmCompletionDeliveryContext is not null &&
            string.Equals(
                ResolveWorkflowLlmCompletionDeliveryId(
                    session.WorkflowLlmCompletionDeliveryContext),
                deliveryId,
                StringComparison.Ordinal))
        {
            return true;
        }

        session = null;
        return false;
    }

    protected static bool IsWorkflowLlmCompletionDeliveryPending(RoleChatSessionState session) =>
        session.WorkflowLlmCompletionDeliveryStatus is
            WorkflowLlmCompletionDeliveryStatus.Prepared or
            WorkflowLlmCompletionDeliveryStatus.RetryScheduled;

    protected static string ResolveWorkflowLlmCompletionDeliveryId(
        WorkflowLlmCompletionDeliveryContext context) =>
        $"{context.RunId}:{context.StepId}:{context.SessionId}";

    private static bool TryResolveCompletionNotificationSession(
        RoleGAgentState state,
        string sessionId,
        string deliveryId,
        out RoleChatSessionState? session)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            state.Sessions.TryGetValue(sessionId, out session) &&
            string.Equals(
                ResolveCompletionNotificationDeliveryId(sessionId, session.RunContext),
                deliveryId,
                StringComparison.Ordinal))
        {
            return true;
        }

        session = null;
        return false;
    }

    private static bool IsCompletionNotificationDeliveryPending(RoleChatSessionState session) =>
        session.CompletionNotificationDeliveryStatus is
            RoleChatCompletionNotificationDeliveryStatus.Prepared or
            RoleChatCompletionNotificationDeliveryStatus.RetryScheduled;

    private static bool HasPendingCompletionNotification(RoleGAgentState state, string sessionId) =>
        state.Sessions.TryGetValue(sessionId, out var session) &&
        session.Completed &&
        IsCompletionNotificationDeliveryPending(session);

    private static bool IsEligibleCompletionNotificationTerminalTransition(
        RoleChatSessionState session,
        int attempt) =>
        IsCompletionNotificationDeliveryPending(session) &&
        (attempt == session.CompletionNotificationAttempt ||
         attempt == session.CompletionNotificationAttempt + 1);

    private IEnumerable<ToolCallEvent> ToToolCallEvents(
        IEnumerable<ToolCall> toolCalls,
        IReadOnlyList<AgentToolReceipt> toolReceipts,
        IReadOnlyList<ToolCallEvent> toolCallSnapshots)
    {
        foreach (var toolCall in toolCalls)
        {
            var snapshot = FindToolCallSnapshot(toolCallSnapshots, toolCall.Id);
            yield return new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = ShouldRedactToolCallArguments(toolCall.Id, toolReceipts)
                    ? string.Empty
                    : toolCall.ArgumentsJson,
                Presentation = ResolveToolCallPresentation(toolCall.Name, snapshot),
            };
        }
    }

    private ToolPresentationDescriptor ResolveToolCallPresentation(
        string toolName,
        ToolCallEvent? snapshot) =>
        snapshot == null
            ? ToolPresentationDescriptors.Snapshot(Tools.Get(toolName), toolName)
            : ToolPresentationDescriptors.Snapshot(snapshot.Presentation, toolName);

    private static void CaptureToolCallSnapshot(
        List<ToolCallEvent> snapshots,
        ToolCallStartedChunk started)
    {
        var snapshot = new ToolCallEvent
        {
            CallId = started.ToolCall.Id,
            ToolName = started.ToolCall.Name,
            ArgumentsJson = started.ToolCall.ArgumentsJson,
            Presentation = ToolPresentationDescriptors.Snapshot(
                started.Presentation,
                started.ToolCall.Name),
        };
        var existingIndex = string.IsNullOrWhiteSpace(snapshot.CallId)
            ? -1
            : snapshots.FindIndex(candidate =>
                string.Equals(candidate.CallId, snapshot.CallId, StringComparison.Ordinal));
        if (existingIndex >= 0)
            snapshots[existingIndex] = snapshot;
        else
            snapshots.Add(snapshot);
    }

    private static ToolCallEvent? FindToolCallSnapshot(
        IReadOnlyList<ToolCallEvent> snapshots,
        string? callId) =>
        string.IsNullOrWhiteSpace(callId)
            ? null
            : snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.CallId, callId, StringComparison.Ordinal));

    private static IReadOnlyList<ToolCall> MergeCompletedToolCalls(
        IReadOnlyList<ToolCall> accumulated,
        IReadOnlyList<ToolCallEvent> snapshots)
    {
        var merged = accumulated.Select(CloneToolCall).ToList();
        foreach (var snapshot in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.CallId) &&
                merged.Any(candidate => string.Equals(candidate.Id, snapshot.CallId, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(new ToolCall
            {
                Id = snapshot.CallId,
                Name = snapshot.ToolName,
                ArgumentsJson = snapshot.ArgumentsJson,
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

    private static bool ShouldRedactToolCallArguments(
        string? callId,
        IReadOnlyList<AgentToolReceipt> toolReceipts) =>
        toolReceipts.Any(receipt =>
            string.Equals(receipt.CallId, callId, StringComparison.Ordinal) &&
            receipt.Status is AgentToolReceiptStatus.Error or
                AgentToolReceiptStatus.Denied or
                AgentToolReceiptStatus.AuthorizationRequired);

    private Timestamp CreateTerminalTimestamp() =>
        Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());

    private void RestoreHistoryFromCommittedSessions()
    {
        var messages = State.Sessions.Values
            .Where(static session => session.Completed && !string.IsNullOrWhiteSpace(session.Prompt))
            .OrderBy(static session => session.Sequence)
            .SelectMany(BuildCommittedSessionHistory)
            .TakeLast(History.MaxMessages)
            .ToArray();
        History.Import(messages);
    }

    private static IEnumerable<SerializableMessage> BuildCommittedSessionHistory(RoleChatSessionState session)
    {
        yield return new SerializableMessage
        {
            Role = "user",
            Content = session.Prompt ?? string.Empty,
            ContentParts = session.InputParts.Count == 0
                ? null
                : ContentPartProtoMapper.FromProtoList(session.InputParts),
        };

        var assistantContent = session.Outcome switch
        {
            RoleChatSessionOutcome.Blocked => session.AuthorizationRequired?.SafeMessage,
            RoleChatSessionOutcome.Failed or RoleChatSessionOutcome.OutcomeUncertain => session.SafeMessage,
            _ => session.FinalContent,
        };
        yield return new SerializableMessage
        {
            Role = "assistant",
            Content = assistantContent ?? string.Empty,
            ReasoningContent = session.FinalReasoningContent,
            ContentParts = session.OutputParts.Count == 0
                ? null
                : ContentPartProtoMapper.FromProtoList(session.OutputParts),
        };
    }

    private static bool HasTrackedSessionAdmissionCapacity(RoleGAgentState state) =>
        state.Sessions.Count < MaxTrackedSessions ||
        state.Sessions.Values.Any(CanTrimTrackedSession);

    private static void TrimTrackedSessions(RoleGAgentState state, string? preservedSessionId = null)
    {
        if (state.Sessions.Count <= MaxTrackedSessions)
            return;

        while (state.Sessions.Count > MaxTrackedSessions)
        {
            string? oldestSessionId = null;
            long oldestSequence = long.MaxValue;

            foreach (var session in state.Sessions)
            {
                if (string.Equals(session.Key, preservedSessionId, StringComparison.Ordinal))
                    continue;

                if (!CanTrimTrackedSession(session.Value))
                    continue;

                var sequence = session.Value.Sequence <= 0 ? long.MinValue : session.Value.Sequence;
                if (sequence < oldestSequence)
                {
                    oldestSequence = sequence;
                    oldestSessionId = session.Key;
                }
            }

            if (string.IsNullOrWhiteSpace(oldestSessionId))
                break;

            state.Sessions.Remove(oldestSessionId);
        }
    }

    private static bool CanTrimTrackedSession(RoleChatSessionState session)
    {
        var completionNotificationSettled =
            string.IsNullOrWhiteSpace(session.RunContext?.CompletionNotificationActorId) ||
            session.CompletionNotificationDeliveryStatus is
                RoleChatCompletionNotificationDeliveryStatus.Dispatched or
                RoleChatCompletionNotificationDeliveryStatus.Expired;
        var workflowCompletionSettled =
            session.WorkflowLlmCompletionDeliveryContext is null ||
            session.WorkflowLlmCompletionDeliveryStatus ==
                WorkflowLlmCompletionDeliveryStatus.Dispatched;
        var historyDeliverySettled =
            session.HistoryDeliveryStatus is
                RoleChatHistoryDeliveryStatus.Unspecified or
                RoleChatHistoryDeliveryStatus.Dispatched;
        return session.Completed &&
               completionNotificationSettled &&
               workflowCompletionSettled &&
               historyDeliverySettled;
    }

    private static AIAgentConfigOverrides EnsureConfigOverrides(RoleGAgentState state)
    {
        if (state.ConfigOverrides == null)
            state.ConfigOverrides = new AIAgentConfigOverrides();
        return state.ConfigOverrides;
    }

    private static VoicePresenceRuntimeState CreateEnabledVoicePresenceRuntimeState(
        VoiceSessionDefaults defaults,
        VoiceRemoteAudioSupport remoteAudioSupport)
    {
        var runtimeState = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.Idle,
            Initialized = true,
            RemoteAudioSupport = NormalizeEnableRemoteAudioSupport(remoteAudioSupport),
        };

        if (defaults.HasSampleRateHz && defaults.SampleRateHz > 0)
            runtimeState.PcmSampleRateHz = defaults.SampleRateHz;

        return runtimeState;
    }

    private static VoiceRemoteAudioSupport NormalizeEnableRemoteAudioSupport(VoiceRemoteAudioSupport remoteAudioSupport) =>
        remoteAudioSupport == VoiceRemoteAudioSupport.Unspecified
            ? VoiceRemoteAudioSupport.Supported
            : remoteAudioSupport;

    private async Task ApplyModuleExtensionsFromStateIfNeededAsync(RoleGAgentState state, CancellationToken ct)
    {
        var eventModules = NormalizeModuleExtensionText(state.EventModules);
        var eventRoutes = NormalizeModuleExtensionText(state.EventRoutes);
        if (string.Equals(_appliedEventModules, eventModules, StringComparison.Ordinal) &&
            string.Equals(_appliedEventRoutes, eventRoutes, StringComparison.Ordinal) &&
            ReferenceEquals(_appliedModuleServices, Services))
        {
            return;
        }

        if (string.IsNullOrEmpty(eventModules))
        {
            await SetModulesAsync([], ct);
        }
        else
        {
            await RoleGAgentFactory.ApplyModuleExtensionsAsync(this, eventModules, eventRoutes, Services, ct);
        }

        _appliedEventModules = eventModules;
        _appliedEventRoutes = eventRoutes;
        _appliedModuleServices = Services;
    }

    private static string NormalizeModuleExtensionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    // Idempotently add a module name to a comma-separated EventModules string, matching the
    // delimiter/options RoleGAgentFactory.BuildModuleExtensions splits on (',' + RemoveEmptyEntries
    // + TrimEntries). De-duplicated ordinal so re-enabling voice never appends a duplicate.
    private static string AppendModuleExtension(string? existing, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return NormalizeModuleExtensionText(existing);

        var modules = NormalizeModuleExtensionText(existing)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!modules.Contains(moduleName, StringComparer.Ordinal))
            modules.Add(moduleName);

        return string.Join(',', modules);
    }

    private static IReadOnlyList<ContentPart> ResolveRequestInputParts(ChatRequestEvent request)
    {
        if (request.InputParts.Count > 0)
        {
            var parts = new List<ContentPart>();
            // Include the text prompt as a TextPart alongside media parts
            if (!string.IsNullOrWhiteSpace(request.Prompt))
                parts.Add(ContentPart.TextPart(request.Prompt));
            parts.AddRange(ContentPartProtoMapper.FromProtoList(request.InputParts));
            return parts;
        }

        return [ContentPart.TextPart(request.Prompt ?? string.Empty)];
    }

    private static LLMRequestLogSummary BuildRequestLogSummary(ChatRequestEvent request) =>
        new(request.Prompt?.Length ?? 0, ResolveRequestInputParts(request).Count);

    private readonly record struct LLMRequestLogSummary(int PromptLength, int InputPartCount);

    private sealed class AgentProfileTurnAuthorityException : InvalidOperationException
    {
        public AgentProfileTurnAuthorityException(string message)
            : base(message)
        {
        }

        public AgentProfileTurnAuthorityException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static bool HaveMatchingInputParts(
        Google.Protobuf.Collections.RepeatedField<ChatContentPart> existing,
        Google.Protobuf.Collections.RepeatedField<ChatContentPart> incoming)
    {
        if (existing.Count != incoming.Count)
            return false;

        for (var i = 0; i < existing.Count; i++)
        {
            if (!existing[i].Equals(incoming[i]))
                return false;
        }

        return true;
    }

    private sealed record SessionReplayRecord(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ToolCall> ToolCalls,
        IReadOnlyList<ContentPart> ContentParts,
        IReadOnlyList<AgentToolReceipt> ToolReceipts,
        IReadOnlyList<ToolResultEvent> ToolResults,
        TokenUsage? Usage,
        string? Model,
        bool ContentEmitted,
        RoleChatSessionOutcome Outcome = RoleChatSessionOutcome.Completed,
        string FailureCode = "",
        string SafeMessage = "",
        NyxIdAuthorizationRequiredEvent? AuthorizationRequired = null)
    {
        public IReadOnlyList<ToolCallEvent> ToolCallSnapshots { get; init; } = [];

        public static SessionReplayRecord FromFailure(
            string content,
            string failureCode = "LLM_REQUEST_FAILED",
            string safeMessage = "The chat request failed. Please try again.") =>
            new(
                content,
                string.Empty,
                [],
                [],
                [],
                [],
                Usage: null,
                Model: null,
                ContentEmitted: false,
                Outcome: RoleChatSessionOutcome.Failed,
                FailureCode: failureCode,
                SafeMessage: safeMessage,
                AuthorizationRequired: null);
    }

    private static TokenUsagePayload? ToTokenUsagePayload(TokenUsage? usage) =>
        usage == null
            ? null
            : new TokenUsagePayload
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };

    private static TokenUsage? ToTokenUsage(TokenUsagePayload? usage) =>
        usage == null
            ? null
            : new TokenUsage(
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens);

}
