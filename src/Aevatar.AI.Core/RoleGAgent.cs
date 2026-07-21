// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs stable ids, lengths, status, and redaction markers for observability
// ─────────────────────────────────────────────────────────────

using System.Globalization;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
[GAgent("ai.role-agent")]
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent, IVoicePresenceRuntimeStateOwner
{
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const int MaxTrackedSessions = 128;
    private const string CompletionNotificationRetryCallbackPrefix = "role-chat-completion-retry";
    private const int CompletionNotificationRetryInitialDelayMs = 250;
    private const int CompletionNotificationRetryMaxDelayMs = 30_000;
    private string _appliedEventModules = string.Empty;
    private string _appliedEventRoutes = string.Empty;
    private IServiceProvider? _appliedModuleServices;
    private readonly TimeProvider _timeProvider;
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

    public RoleGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IToolApprovalHandler? approvalHandler = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort = null,
        TimeProvider? timeProvider = null)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewares,
            llmMiddlewares,
            toolSources,
            // RoleGAgent owns the pending-approval continuation (persisted state +
            // remote escalation + timeout), so yielding is its capability default.
            // Surfaces without that continuation must NOT wire a yielding handler;
            // they fall through to MissingApprovalHandler and fail closed.
            approvalHandler ?? new YieldApprovalHandler())
    {
        RemoteToolApprovalPort = remoteToolApprovalPort;
        RemoteToolApprovalNotificationPort = remoteToolApprovalNotificationPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
    }

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
        if (pending == null || pending.RequestId != evt.RequestId)
        {
            await PersistApprovalRequestNotPendingAsync(continuationTurnId);
            return;
        }

        // Cancel the escalation timeout
        await CancelApprovalTimeoutAsync(pending);

        if (evt.Approved)
        {
            Logger.LogInformation(
                "[{Role}] Tool approval APPROVED. Executing tool={Tool} request={RequestId}",
                RoleName, pending.ToolName, pending.RequestId);

            // Refactor (issue1414/cluster-004):
            //   Old pattern: pending approval state could rehydrate stable tool/caller context from metadata.
            //   New principle: typed ToolContext/LlmControl are the only tool control authority.
            try
            {
                // Refactor (issue1253-first):
                //   Old pattern: Approval resume rebuilt control context from a durable annotation bag.
                //   New principle: Use typed pending.ToolContext only; metadata is never a control source.
                var pendingToolContext = ResolvePendingToolContext(pending);
                using (AgentToolContextScope.Push(pendingToolContext))
                {
                    // Execute the yielded tool call
                    var toolResult = await Tools.ExecuteToolCallAsync(
                        new ToolCall
                        {
                            Id = pending.ToolCallId,
                            Name = pending.ToolName,
                            ArgumentsJson = pending.ArgumentsJson,
                        },
                        CancellationToken.None);

                    Logger.LogInformation(
                        "[{Role}] Tool executed. result length={Len} request={RequestId}",
                        RoleName, toolResult.Content?.Length ?? 0, pending.RequestId);

                    // Clear pending state
                    await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });

                    // Build continuation prompt with the actual tool result
                    var continuation = BuildContinuationPrompt(pending, toolResult.Content);

                    Logger.LogInformation(
                        "[{Role}] Dispatching continuation chat. request={RequestId}",
                        RoleName, pending.RequestId);

                    // Self-continuation: dispatch ChatRequestEvent to own inbox (new turn).
                    var continuationRequest = new ChatRequestEvent
                    {
                        Prompt = continuation,
                        SessionId = continuationTurnId,
                        ScopeId = pending.ScopeId,
                        ToolContext = pendingToolContext.ToPayload(),
                    };
                    await SendToAsync(Id, continuationRequest);

                    Logger.LogInformation(
                        "[{Role}] Continuation dispatched. request={RequestId}",
                        RoleName, pending.RequestId);
                }
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
        else
        {
            await PersistApprovalTerminalFailureThenClearPendingAsync(
                pending,
                "approval_denied",
                string.IsNullOrWhiteSpace(evt.Reason)
                    ? "Tool approval denied."
                    : evt.Reason,
                continuationTurnId);
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

    private PendingToolApprovalState? DetectPendingApproval(
        SessionReplayRecord replayRecord,
        ChatRequestEvent request)
    {
        var receipt = replayRecord.ToolReceipts
            .LastOrDefault(static candidate =>
                candidate.Status == AgentToolReceiptStatus.ApprovalRequired &&
                !string.IsNullOrWhiteSpace(candidate.ApprovalRequestId));
        if (receipt is null)
            return null;

        return new PendingToolApprovalState
        {
            RequestId = receipt.ApprovalRequestId,
            SessionId = request.SessionId ?? string.Empty,
            ToolName = receipt.ToolName ?? string.Empty,
            ToolCallId = receipt.CallId ?? string.Empty,
            ArgumentsJson = ResolveToolArguments(replayRecord.ToolCalls, receipt.CallId),
            IsDestructive = receipt.IsDestructive,
            ToolContext = ResolveToolContext(
                request,
                receipt.ApprovalRequestId,
                receipt.CallId ?? string.Empty).ToPayload(),
            ScopeId = request.ScopeId ?? string.Empty,
        };
    }

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

    private async Task ScheduleApprovalTimeoutAsync(PendingToolApprovalState pending)
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
                });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to schedule approval timeout", RoleName);
        }
    }

    private async Task CancelApprovalTimeoutAsync(PendingToolApprovalState pending)
    {
        if (_approvalTimeoutLease == null)
            return;

        try
        {
            await CancelDurableCallbackAsync(_approvalTimeoutLease);
            _approvalTimeoutLease = null;
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

    private static IReadOnlyDictionary<string, string> ScrubPendingApprovalMetadata(
        IReadOnlyDictionary<string, string>? metadata) =>
        AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata);

    private static AgentToolExecutionContext ResolveToolContext(
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
            Request = new AgentToolRequestIdentity(
                NormalizeToolContextValue(requestId) ?? context.Request.RequestId,
                NormalizeToolContextValue(toolCallId) ?? context.Request.CallId),
            Credentials = AgentToolCredentials.Empty,
            ExternalMetadata = ScrubPendingApprovalMetadata(context.ExternalMetadata),
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
            .On<RoleChatSessionProgressedEvent>(ApplyChatSessionProgressed)
            .On<RoleChatSessionCompletedEvent>(ApplyChatSessionCompleted)
            .On<RoleChatCompletionNotificationRetryScheduledEvent>(ApplyCompletionNotificationRetryScheduled)
            .On<RoleChatCompletionNotificationDispatchedEvent>(ApplyCompletionNotificationDispatched)
            .On<RoleChatCompletionNotificationExpiredEvent>(ApplyCompletionNotificationExpired)
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

    private async Task HandleChatRequestCoreAsync(ChatRequestEvent request)
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
            await DeliverCompletionNotificationAsync(request.SessionId, trackedSession, CancellationToken.None);
            await ReplayCompletedSessionAsync(request.SessionId, trackedSession);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId) && trackedSession == null)
        {
            await PersistDomainEventAsync(new RoleChatSessionStartedEvent
            {
                SessionId = request.SessionId,
                Prompt = request.Prompt,
                InputParts = { request.InputParts },
                RunContext = request.RunContext?.Clone(),
            });
        }
        else if (trackedSession != null)
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
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var useWorkflowFailureMarker = timeoutMs > 0;
        using var timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null;
        var streamCt = timeoutCts?.Token ?? CancellationToken.None;

        // ─── AG-UI: TEXT_MESSAGE_START ───
        await PersistSessionProgressAsync(request.SessionId, progress =>
            progress.TextStarted = new RoleChatTextStartedProgress { AgentId = Id });
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        SessionReplayRecord replayRecord;
        try
        {
            replayRecord = await ExecuteStreamingChatAsync(request, streamCt);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            Logger.LogWarning(
                "[{Role}] LLM request timeout after {TimeoutMs}ms. session={SessionId}",
                RoleName,
                timeoutMs,
                request.SessionId);
            var error = $"LLM request timed out after {timeoutMs}ms";
            replayRecord = SessionReplayRecord.FromFailure(BuildLlmFailureContent(error));
        }
        catch (Exception ex)
        {
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
                useWorkflowFailureMarker
                    ? BuildLlmFailureContent(error)
                    : $"LLM request failed [tools={toolNames}]: {error}");
        }
        finally
        {
            // The stashed per-turn token must not outlive its turn: a later turn without a token
            // (e.g. an internal continuation) must not trigger an overlay refresh with a stale credential.
            _currentTurnNyxIdAccessToken = null;
        }

        // ─── Detect approval-pending tool result and set up continuation ───
        var pendingApproval = DetectPendingApproval(replayRecord, request);
        if (pendingApproval != null)
        {
            var approvalProgress = CreateSessionProgress(request.SessionId, progress =>
                progress.ToolApprovalRequired = new RoleChatToolApprovalRequiredProgress
                {
                    Pending = pendingApproval.Clone(),
                });
            await PersistDomainEventsAsync(
            [
                new PendingToolApprovalPersistedEvent { Pending = pendingApproval },
                approvalProgress,
            ]);

            await PublishAsync(new ToolApprovalRequestEvent
            {
                RequestId = pendingApproval.RequestId,
                SessionId = pendingApproval.SessionId,
                ToolName = pendingApproval.ToolName,
                ToolCallId = pendingApproval.ToolCallId,
                ArgumentsJson = pendingApproval.ArgumentsJson,
                IsDestructive = pendingApproval.IsDestructive,
                ApprovalMode = "yield",
                TimeoutSeconds = ApprovalLocalTimeoutSeconds,
            }, TopologyAudience.Parent);

            await ScheduleApprovalTimeoutAsync(pendingApproval);
        }

        // Refactor (iter164/cluster-001-role-completion):
        //   Old pattern: terminal presentation frames were published before
        //                RoleChatSessionCompletedEvent was committed; commit failure was downgraded to replay-only loss.
        //   New principle: commit RoleChatSessionCompletedEvent first; publish terminal frames only from that committed fact.
        await PersistSessionCompletionAsync(request, replayRecord);
        replayRecord = await PublishMissingDisplayContentAsync(request.SessionId, replayRecord);
        await PublishUsageAsync(request.SessionId, ToTokenUsagePayload(replayRecord.Usage), replayRecord.Model);
        await PublishCompletionAsync(request.SessionId, replayRecord.Content);
    }

    private static int ResolveLlmTimeoutMs(ChatRequestEvent request)
    {
        return request.TimeoutMs > 0 ? request.TimeoutMs : 0;
    }

    private static string BuildLlmFailureContent(string? message)
    {
        var safeMessage = SanitizeFailureMessage(message);
        return $"{LlmFailureContentPrefix} {safeMessage}";
    }

    private static string SanitizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private async Task<SessionReplayRecord> ExecuteStreamingChatAsync(ChatRequestEvent request, CancellationToken streamCt)
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
        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count > 0
            ? AgentToolExecutionContextMapper.StripOwnedControlKeys(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal))
            : null;
        var llmControl = LLMControlContextMapper.FromPayload(request.LlmControl);
        var toolContext = llmControl.ToToolContext(AgentToolExecutionContextMapper.FromPayload(request.ToolContext));
        // Stash this turn's token for chartered direct-chat subclasses (System Skill Overlay seam).
        // Kept in memory only for the turn; never persisted or logged.
        _currentTurnNyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken;
        var inputParts = ResolveRequestInputParts(request);

        await foreach (var chunk in ChatStreamAsync(inputParts, request.SessionId, llmControl, toolContext, metadata, streamCt))
        {
            if (chunk.Usage != null)
                usage = chunk.Usage;

            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PersistSessionProgressAsync(request.SessionId, progress =>
                    progress.TextDelta = new RoleChatTextDeltaProgress { Delta = chunk.DeltaContent });
                await PublishAsync(new TextMessageContentEvent
                {
                    Delta = chunk.DeltaContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaContentPart != null)
            {
                contentParts.Add(chunk.DeltaContentPart);
                var part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart);
                await PersistSessionProgressAsync(request.SessionId, progress =>
                    progress.Media = new RoleChatMediaProgress
                    {
                        AgentId = Id,
                        Part = part.Clone(),
                    });
                await PublishAsync(new MediaContentEvent
                {
                    SessionId = request.SessionId,
                    AgentId = Id,
                    Part = part,
                }, TopologyAudience.Parent);
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PersistSessionProgressAsync(request.SessionId, progress =>
                    progress.ReasoningDelta = new RoleChatReasoningDeltaProgress
                    {
                        Delta = chunk.DeltaReasoningContent,
                    });
                await PublishAsync(new TextMessageReasoningEvent
                {
                    Delta = chunk.DeltaReasoningContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);

            if (chunk.ToolCallStarted != null)
            {
                var started = chunk.ToolCallStarted;
                CaptureToolCallSnapshot(toolCallSnapshots, started);
                await PersistSessionProgressAsync(request.SessionId, progress =>
                    progress.ToolStarted = new RoleChatToolStartedProgress
                    {
                        CallId = started.ToolCall.Id,
                        ToolName = started.ToolCall.Name,
                        Presentation = ToolPresentationDescriptors.Snapshot(
                            started.Presentation,
                            started.ToolCall.Name),
                    });
            }

            if (chunk.ToolCallCompleted != null)
            {
                var completed = chunk.ToolCallCompleted;
                var toolResult = new ToolResultEvent
                {
                    CallId = completed.CallId,
                    ResultJson = completed.ResultJson,
                    Success = completed.Success,
                    Error = completed.Error,
                };
                if (completed.Receipt != null)
                    toolResult.Receipt = completed.Receipt.Clone();
                toolResults.Add(toolResult.Clone());
                await PersistSessionProgressAsync(request.SessionId, progress =>
                    progress.ToolCompleted = new RoleChatToolCompletedProgress
                    {
                        Result = toolResult.Clone(),
                        ToolName = completed.ToolName,
                    });
            }

            var receipt = chunk.ToolCallCompleted?.Receipt ?? chunk.ToolReceipt;
            if (receipt != null)
                toolReceipts.Add(receipt.Clone());
        }

        var appendedHistoryMessages = History.Messages
            .Skip(Math.Min(initialHistoryCount, History.Count))
            .ToArray();

        var completedToolCalls = MergeCompletedToolCalls(toolCalls.BuildToolCalls(), toolCallSnapshots);
        foreach (var toolCall in completedToolCalls)
        {
            var snapshot = FindToolCallSnapshot(toolCallSnapshots, toolCall.Id);
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = ShouldRedactToolCallArguments(toolCall.Id, toolReceipts)
                    ? string.Empty
                    : toolCall.ArgumentsJson,
                Presentation = ResolveToolCallPresentation(toolCall.Name, snapshot),
            }, TopologyAudience.Parent);
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
                Success = receipt?.Status is null or AgentToolReceiptStatus.Success or AgentToolReceiptStatus.ApprovalRequired,
                Error = receipt?.ErrorMessage ?? string.Empty,
            };
            if (receipt is not null)
                toolResultEvent.Receipt = receipt.Clone();

            await PublishAsync(toolResultEvent, TopologyAudience.Parent);
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

    private Task PersistSessionCompletionAsync(ChatRequestEvent request, SessionReplayRecord replayRecord) =>
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
            replayRecord.AuthorizationRequired);

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
        NyxIdAuthorizationRequiredEvent? authorizationRequired = null)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return;

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
            ActorId = Id,
        };
        await PersistCompletionWithTerminalProgressAsync(completion);
        await DeliverCompletionNotificationAsync(request.SessionId, State.Sessions[request.SessionId], CancellationToken.None);
    }

    private Task PersistCompletionWithTerminalProgressAsync(RoleChatSessionCompletedEvent completion)
    {
        completion.TerminalProgress.Clear();
        completion.TerminalProgress.Add(BuildTerminalProgressEvents(completion));
        return PersistDomainEventAsync(completion);
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
        Action<RoleChatSessionProgressedEvent> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        await PersistDomainEventAsync(CreateSessionProgress(sessionId, configure));
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
        await PersistApprovalTerminalFailureAsync(pending, reasonCode, reasonMessage, terminalTurnId);
        await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
    }

    private async Task TryPersistApprovalTerminalFailureThenClearPendingAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage,
        string? terminalTurnId = null)
    {
        try
        {
            await PersistApprovalTerminalFailureAsync(pending, reasonCode, reasonMessage, terminalTurnId);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[{Role}] Failed to persist approval terminal failure. request={RequestId} session={SessionId} reasonCode={ReasonCode}",
                RoleName,
                pending.RequestId,
                pending.SessionId,
                reasonCode);
            return;
        }

        try
        {
            await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Failed to clear pending approval after terminal failure was persisted. request={RequestId} session={SessionId} reasonCode={ReasonCode}",
                RoleName,
                pending.RequestId,
                pending.SessionId,
                reasonCode);
        }
    }

    private async Task PersistApprovalTerminalFailureAsync(
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
            return;

        if (State.Sessions.TryGetValue(resolvedTurnId, out var existingSession) &&
            (hasCallerSelectedTurnId || existingSession.Completed))
        {
            Logger.LogWarning(
                "[{Role}] Approval terminal turn collides with an existing session; skipping conflicting completion. session={SessionId} reasonCode={ReasonCode}",
                RoleName,
                resolvedTurnId,
                reasonCode);
            return;
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
            ActorId = Id,
        };
        await PersistCompletionWithTerminalProgressAsync(completion);
        await DeliverCompletionNotificationAsync(
            resolvedTurnId,
            State.Sessions[resolvedTurnId],
            CancellationToken.None);
    }

    private static string ResolveApprovalContinuationTurnId(string? continuationTurnId) =>
        string.IsNullOrWhiteSpace(continuationTurnId)
            ? $"turn-{Guid.NewGuid():N}"
            : continuationTurnId.Trim();

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

    private async Task ReplayCompletedSessionAsync(string sessionId, RoleChatSessionState trackedSession)
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
        await PersistSessionProgressAsync(sessionId, progress =>
            progress.Replay = new RoleChatReplayProgress { Snapshot = snapshot });

        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = sessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        if (IsDisplayableCompletionContent(trackedSession.FinalContent))
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = trackedSession.FinalContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent);
        }

        if (!string.IsNullOrEmpty(trackedSession.FinalReasoningContent))
        {
            await PublishAsync(new TextMessageReasoningEvent
            {
                Delta = trackedSession.FinalReasoningContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent);
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
            }, TopologyAudience.Parent);
        }

        foreach (var receipt in trackedSession.ToolReceipts)
        {
            var toolResultEvent = new ToolResultEvent
            {
                CallId = receipt.CallId ?? string.Empty,
                ResultJson = receipt.ResultJson ?? string.Empty,
                Success = receipt.Status is AgentToolReceiptStatus.Success or AgentToolReceiptStatus.ApprovalRequired,
                Error = receipt.ErrorMessage ?? string.Empty,
                Receipt = receipt.Clone(),
            };
            await PublishAsync(toolResultEvent, TopologyAudience.Parent);
        }

        foreach (var contentPart in trackedSession.OutputParts)
        {
            await PublishAsync(new MediaContentEvent
            {
                SessionId = sessionId,
                AgentId = Id,
                Part = contentPart.Clone(),
            }, TopologyAudience.Parent);
        }

        await PublishUsageAsync(sessionId, trackedSession.Usage, trackedSession.Model);
        await PublishCompletionAsync(sessionId, trackedSession.FinalContent ?? string.Empty);
    }

    private async Task<SessionReplayRecord> PublishMissingDisplayContentAsync(
        string sessionId,
        SessionReplayRecord replayRecord)
    {
        if (replayRecord.ContentEmitted ||
            !IsDisplayableCompletionContent(replayRecord.Content))
        {
            return replayRecord;
        }

        await PublishAsync(new TextMessageContentEvent
        {
            Delta = replayRecord.Content,
            SessionId = sessionId,
        }, TopologyAudience.Parent);

        return replayRecord with { ContentEmitted = true };
    }

    private Task PublishCompletionAsync(string sessionId, string completionContent) =>
        PublishAsync(
            new TextMessageEndEvent
            {
                Content = completionContent,
                SessionId = sessionId,
            },
            TopologyAudience.Parent);

    private Task PublishUsageAsync(string sessionId, TokenUsagePayload? usage, string? model)
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
            TopologyAudience.Parent);
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
            await DeliverCompletionNotificationAsync(sessionId, session, ct);
    }

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
        try
        {
            await SendToAsync(
                runContext.CompletionNotificationActorId.Trim(),
                completion,
                ct,
                new EventEnvelopePublishOptions
                {
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        DeduplicationOperationId = $"role-chat-terminal:{deliveryId}",
                    },
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleCompletionNotificationRetryAsync(
                sessionId,
                runContext,
                deliveryId,
                attempt,
                ct);
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
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleCompletionNotificationRetryAsync(
                sessionId,
                runContext,
                deliveryId,
                attempt,
                ct);
            throw;
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
            await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                dueTime,
                retryFired,
                retryOptions,
                ct);
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
                await PublishAsync(retryFired, TopologyAudience.Self, ct, options: retryOptions);
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
                DeduplicationOperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
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
        sessions[evt.SessionId] = session;
        TrimTrackedSessions(next);
        return next;
    }

    private static RoleGAgentState ApplyChatSessionCompleted(
        RoleGAgentState current,
        RoleChatSessionCompletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return current;

        if (HasPendingCompletionNotification(current, evt.SessionId))
            return current;

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
        var completionNotificationDeliveryStatus = runContextMatches
            ? session.CompletionNotificationDeliveryStatus
            : RoleChatCompletionNotificationDeliveryStatus.Unspecified;
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
        session.CompletionNotificationDeliveryStatus =
            !string.IsNullOrWhiteSpace(session.RunContext?.CompletionNotificationActorId)
                ? completionNotificationDeliveryStatus ==
                  RoleChatCompletionNotificationDeliveryStatus.Unspecified
                    ? RoleChatCompletionNotificationDeliveryStatus.Prepared
                    : completionNotificationDeliveryStatus
                : RoleChatCompletionNotificationDeliveryStatus.Unspecified;
        if (!runContextMatches)
        {
            session.CompletionNotificationAttempt = 0;
            session.CompletionNotificationRetryCallbackId = string.Empty;
            session.CompletionNotificationRetryAt = null;
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
        TrimTrackedSessions(next);
        return next;
    }

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
        TrimTrackedSessions(next);
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
        TrimTrackedSessions(next);
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
        TrimTrackedSessions(next);
        return next;
    }

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
            RoleChatSessionOutcome.Failed => session.SafeMessage,
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

    private static void TrimTrackedSessions(RoleGAgentState state)
    {
        if (state.Sessions.Count <= MaxTrackedSessions)
            return;

        while (state.Sessions.Count > MaxTrackedSessions)
        {
            string? oldestSessionId = null;
            long oldestSequence = long.MaxValue;

            foreach (var session in state.Sessions)
            {
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

    private static bool CanTrimTrackedSession(RoleChatSessionState session) =>
        string.IsNullOrWhiteSpace(session.RunContext?.CompletionNotificationActorId) ||
        session.CompletionNotificationDeliveryStatus is
            RoleChatCompletionNotificationDeliveryStatus.Dispatched or
            RoleChatCompletionNotificationDeliveryStatus.Expired;

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

        public static SessionReplayRecord FromFailure(string content) =>
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
                FailureCode: "LLM_REQUEST_FAILED",
                SafeMessage: "The chat request failed. Please try again.",
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

}
