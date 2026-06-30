// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs stable ids, lengths, status, and redaction markers for observability
// ─────────────────────────────────────────────────────────────

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
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
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
    private const string SystemSkillOverlayRefreshCallbackId = "system-skill-overlay-refresh";
    private const int SystemSkillOverlayPromptLogSampleRate = 64;
    private static readonly TimeSpan DefaultSystemSkillOverlayRefreshTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SystemSkillOverlayInitialRefreshDelay = TimeSpan.FromSeconds(1);
    private string _appliedEventModules = string.Empty;
    private string _appliedEventRoutes = string.Empty;
    private IServiceProvider? _appliedModuleServices;
    private SystemSkillOverlay? _systemSkillOverlay;
    private int _systemSkillOverlayPromptLogCounter;

    public RoleGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IToolApprovalHandler? approvalHandler = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort = null)
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
    }

    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    // Refactor (iter15/cluster-028):
    //   Old pattern: workflow artifact facts derived role identity by parsing child actor id prefixes.
    //   New principle: role identity is a typed actor-owned fact persisted on RoleGAgent state.
    public string RoleId { get; private set; } = "";

    protected IRemoteToolApprovalPort? RemoteToolApprovalPort { get; }

    protected IRemoteToolApprovalNotificationPort? RemoteToolApprovalNotificationPort { get; }

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
        var pending = State.PendingApproval;
        if (pending == null || pending.RequestId != evt.RequestId)
            return;

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
                        SessionId = Guid.NewGuid().ToString("N"),
                        ScopeId = pending.SessionId,
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
                    ex.Message);

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
                    : evt.Reason);
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
                $"Remote approval submit failed: {ex.Message}");
        }
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
                $"Remote approval status check failed: {ex.Message}");
        }

        switch (snapshot.Status)
        {
            case RemoteToolApprovalStatus.Approved:
                await HandleToolApprovalDecision(new ToolApprovalDecisionEvent
                {
                    RequestId = pending.RequestId,
                    SessionId = pending.SessionId,
                    Approved = true,
                    Reason = snapshot.Reason ?? "Approved via NyxID remote.",
                });
                return;

            case RemoteToolApprovalStatus.Rejected:
            case RemoteToolApprovalStatus.Expired:
                await PersistApprovalTerminalFailureThenClearPendingAsync(
                    pending,
                    snapshot.Status == RemoteToolApprovalStatus.Expired ? "approval_timeout" : "approval_denied",
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
    public async Task HandleSystemSkillOverlayRefresh(SystemSkillOverlayRefreshFiredEvent evt)
    {
        var builder = Services.GetService<ISystemSkillOverlayBuilder>();
        var options = Services.GetService<SystemSkillOverlayOptions>();
        if (!IsSystemSkillOverlayEnabled(builder, options))
        {
            _systemSkillOverlay = new SystemSkillOverlay();
            return;
        }

        SystemSkillOverlay? nextOverlay;
        try
        {
            nextOverlay = await builder!.BuildAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            var nextAttempt = Math.Max(0, evt.Attempt) + 1;
            Logger.LogWarning(
                ex,
                "[{Role}] System skill overlay refresh failed; keeping last-known-good overlay. attempt={Attempt}",
                RoleName,
                nextAttempt);
            await ScheduleSystemSkillOverlayRefreshAsync(
                ComputeSystemSkillOverlayRefreshBackoff(nextAttempt),
                nextAttempt,
                CancellationToken.None);
            return;
        }

        var nextWatermark = ResolveSystemSkillOverlayWatermark(nextOverlay);
        var currentWatermark = ResolveSystemSkillOverlayWatermark(State.SystemSkillOverlay);

        if (!string.Equals(nextWatermark, currentWatermark, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new SystemSkillOverlayMaterializedEvent
            {
                Overlay = nextOverlay?.Clone() ?? new SystemSkillOverlay(),
            });
        }

        await ScheduleSystemSkillOverlayRefreshAsync(
            ResolveSystemSkillOverlayRefreshTtl(options),
            attempt: 0,
            CancellationToken.None);
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
            .On<RoleChatSessionCompletedEvent>(ApplyChatSessionCompleted)
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
        HydrateSystemSkillOverlayMirror(state);
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
        var trackedSession = ResolveTrackedSession(request);
        if (trackedSession is { Completed: true })
        {
            Logger.LogInformation(
                "[{Role}] Replaying cached LLM completion for session={SessionId}",
                RoleName,
                request.SessionId);
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
            replayRecord = SessionReplayRecord.FromFailure(
                BuildLlmFailureContent($"LLM request timed out after {timeoutMs}ms"));
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
            replayRecord = SessionReplayRecord.FromFailure(
                useWorkflowFailureMarker
                    ? BuildLlmFailureContent(ex.Message)
                    : $"LLM request failed [tools={toolNames}]: {SanitizeFailureMessage(ex.Message)}");
        }

        // ─── Detect approval-pending tool result and set up continuation ───
        var pendingApproval = DetectPendingApproval(replayRecord, request);
        if (pendingApproval != null)
        {
            await PersistDomainEventAsync(new PendingToolApprovalPersistedEvent
            {
                Pending = pendingApproval,
            });

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
        TokenUsage? usage = null;
        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count > 0
            ? AgentToolExecutionContextMapper.StripOwnedControlKeys(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal))
            : null;
        var llmControl = LLMControlContextMapper.FromPayload(request.LlmControl);
        var toolContext = llmControl.ToToolContext(AgentToolExecutionContextMapper.FromPayload(request.ToolContext));
        var inputParts = ResolveRequestInputParts(request);

        await foreach (var chunk in ChatStreamAsync(inputParts, request.SessionId, llmControl, toolContext, metadata, streamCt))
        {
            if (chunk.Usage != null)
                usage = chunk.Usage;

            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PublishAsync(new TextMessageContentEvent
                {
                    Delta = chunk.DeltaContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaContentPart != null)
            {
                contentParts.Add(chunk.DeltaContentPart);
                await PublishAsync(new MediaContentEvent
                {
                    SessionId = request.SessionId,
                    AgentId = Id,
                    Part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart),
                }, TopologyAudience.Parent);
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PublishAsync(new TextMessageReasoningEvent
                {
                    Delta = chunk.DeltaReasoningContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);

            if (chunk.ToolReceipt != null)
                toolReceipts.Add(chunk.ToolReceipt.Clone());
        }

        var appendedHistoryMessages = History.Messages
            .Skip(Math.Min(initialHistoryCount, History.Count))
            .ToArray();

        foreach (var toolCall in toolCalls.BuildToolCalls())
        {
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
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
            toolCalls.BuildToolCalls(),
            contentParts,
            toolReceipts,
            Usage: usage,
            Model: EffectiveConfig.Model ?? string.Empty,
            ContentEmitted: fullContent.Length > 0);
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
            replayRecord.ToolReceipts);

    protected Task PersistRoleChatSessionCompletionAsync(
        ChatRequestEvent request,
        string content,
        string reasoningContent,
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<ContentPart> contentParts,
        bool contentEmitted,
        TokenUsage? usage = null,
        string? model = null,
        IReadOnlyList<AgentToolReceipt>? toolReceipts = null)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(new RoleChatSessionCompletedEvent
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
            ToolCalls = { ToToolCallEvents(toolCalls) },
            OutputParts = { ContentPartProtoMapper.ToProtoList(contentParts) },
            ToolReceipts = { (toolReceipts ?? []).Select(receipt => receipt.Clone()) },
            Usage = ToTokenUsagePayload(usage),
            Model = model ?? string.Empty,
        });
    }

    private async Task PersistApprovalTerminalFailureThenClearPendingAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage)
    {
        await PersistApprovalTerminalFailureAsync(pending, reasonCode, reasonMessage);
        await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
    }

    private async Task TryPersistApprovalTerminalFailureThenClearPendingAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage)
    {
        try
        {
            await PersistApprovalTerminalFailureAsync(pending, reasonCode, reasonMessage);
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

    private Task PersistApprovalTerminalFailureAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage)
    {
        if (string.IsNullOrWhiteSpace(pending.SessionId))
            return Task.CompletedTask;

        var safeReason = string.IsNullOrWhiteSpace(reasonMessage)
            ? "Tool approval failed."
            : reasonMessage.Trim();
        return PersistDomainEventAsync(new RoleChatSessionCompletedEvent
        {
            // Refactor (iter15/cluster-028):
            //   Old pattern: terminal failure facts omitted role identity and forced downstream actor-id parsing.
            //   New principle: every role completion fact carries the typed RoleId.
            RoleId = RoleId,
            SessionId = pending.SessionId,
            Content = BuildLlmFailureContent($"{reasonCode}: {safeReason}"),
            Prompt = BuildContinuationPrompt(pending, safeReason),
            ContentEmitted = false,
        });
    }

    private async Task ReplayCompletedSessionAsync(string sessionId, RoleChatSessionState trackedSession)
    {
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
        await PublishCompletionAsync(sessionId, trackedSession.FinalContent);
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
            throw new InvalidOperationException(
                $"Session '{request.SessionId}' already exists with a different prompt.");
        }

        if (!HaveMatchingInputParts(trackedSession.InputParts, request.InputParts))
        {
            throw new InvalidOperationException(
                $"Session '{request.SessionId}' already exists with different multimodal input.");
        }

        return trackedSession;
    }

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
        var next = current.Clone();
        next.SystemSkillOverlay = evt.Overlay?.Clone() ?? new SystemSkillOverlay();
        return next;
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
        session.Usage = evt.Usage?.Clone();
        session.Model = evt.Model ?? string.Empty;
        next.Sessions[evt.SessionId] = session;
        TrimTrackedSessions(next);
        return next;
    }

    private static IEnumerable<ToolCallEvent> ToToolCallEvents(IEnumerable<ToolCall> toolCalls)
    {
        foreach (var toolCall in toolCalls)
        {
            yield return new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
            };
        }
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

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        HydrateSystemSkillOverlayMirror(State);
        if (IsSystemSkillOverlayEmpty(_systemSkillOverlay))
            await ScheduleSystemSkillOverlayRefreshAsync(SystemSkillOverlayInitialRefreshDelay, attempt: 0, ct);
    }

    protected override string DecorateSystemPrompt(string basePrompt)
    {
        var decorated = base.DecorateSystemPrompt(basePrompt);
        var overlayMarkdown = _systemSkillOverlay?.OverlayMarkdown;
        if (string.IsNullOrWhiteSpace(overlayMarkdown))
            return decorated;

        if (_systemSkillOverlayPromptLogCounter++ % SystemSkillOverlayPromptLogSampleRate == 0)
        {
            Logger.LogInformation(
                "[{Role}] System skill overlay prompt: source_watermark={SourceWatermark}, kernel_tokens_estimate={KernelTokensEstimate}, overlay_tokens_estimate={OverlayTokensEstimate}",
                RoleName,
                ResolveSystemSkillOverlayWatermark(_systemSkillOverlay),
                EstimatePromptTokens(decorated),
                EstimatePromptTokens(overlayMarkdown));
        }

        if (string.IsNullOrWhiteSpace(decorated))
            return overlayMarkdown.Trim();

        return $"{decorated.TrimEnd()}\n\n{overlayMarkdown.Trim()}";
    }

    private void HydrateSystemSkillOverlayMirror(RoleGAgentState state)
    {
        if (!IsSystemSkillOverlayEnabled())
        {
            _systemSkillOverlay = new SystemSkillOverlay();
            return;
        }

        _systemSkillOverlay = state.SystemSkillOverlay?.Clone();
    }

    private bool IsSystemSkillOverlayEnabled() =>
        IsSystemSkillOverlayEnabled(
            Services.GetService<ISystemSkillOverlayBuilder>(),
            Services.GetService<SystemSkillOverlayOptions>());

    private static bool IsSystemSkillOverlayEnabled(
        ISystemSkillOverlayBuilder? builder,
        SystemSkillOverlayOptions? options) =>
        builder != null && options?.Enabled != false;

    private async Task ScheduleSystemSkillOverlayRefreshAsync(
        TimeSpan dueTime,
        int attempt,
        CancellationToken ct)
    {
        if (!IsSystemSkillOverlayEnabled())
            return;

        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                SystemSkillOverlayRefreshCallbackId,
                NormalizeSystemSkillOverlayDueTime(dueTime),
                new SystemSkillOverlayRefreshFiredEvent
                {
                    Attempt = Math.Max(0, attempt),
                },
                ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to schedule system skill overlay refresh", RoleName);
        }
    }

    private static TimeSpan ResolveSystemSkillOverlayRefreshTtl(SystemSkillOverlayOptions? options) =>
        options?.RefreshTtl > TimeSpan.Zero ? options.RefreshTtl : DefaultSystemSkillOverlayRefreshTtl;

    private static TimeSpan NormalizeSystemSkillOverlayDueTime(TimeSpan dueTime) =>
        dueTime > TimeSpan.Zero ? dueTime : SystemSkillOverlayInitialRefreshDelay;

    private static TimeSpan ComputeSystemSkillOverlayRefreshBackoff(int failedAttempt)
    {
        var exponent = Math.Clamp(failedAttempt - 1, 0, 4);
        return TimeSpan.FromMinutes(1 << exponent);
    }

    private static bool IsSystemSkillOverlayEmpty(SystemSkillOverlay? overlay) =>
        overlay == null || string.IsNullOrWhiteSpace(overlay.OverlayMarkdown);

    private static string ResolveSystemSkillOverlayWatermark(SystemSkillOverlay? overlay) =>
        overlay?.SourceWatermark ?? string.Empty;

    private static int EstimatePromptTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Math.Max(1, (Encoding.UTF8.GetByteCount(text) + 3) / 4);
    }

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
        TokenUsage? Usage,
        string? Model,
        bool ContentEmitted)
    {
        public static SessionReplayRecord FromFailure(string content) =>
            new(content, string.Empty, [], [], [], Usage: null, Model: null, ContentEmitted: false);
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
