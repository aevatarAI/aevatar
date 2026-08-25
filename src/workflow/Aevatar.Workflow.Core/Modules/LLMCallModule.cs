using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends <see cref="WorkflowLlmExecutionIntent"/> to a role actor.</summary>
// Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
//   Old pattern: Information log included raw value/prompt/input preview
//   New principle: only stable id + length + status + redaction marker
public sealed class LLMCallModule : IEventModule<IWorkflowExecutionContext>
{
    private const int DefaultLlmTimeoutMs = 1_800_000;
    private const string LlmWatchdogCallbackPrefix = "llm-watchdog";
    internal const string ModuleStateKey = "llm_call";

    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;

    public LLMCallModule(
        WorkflowStepTargetAgentResolver? targetAgentResolver = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    {
        _targetAgentResolver = targetAgentResolver;
        _callerAccessTokenProvider = callerAccessTokenProvider;
    }

    public string Name => "llm_call";
    public int Priority => 10;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowLlmInvocationCompletedEvent.Descriptor) ||
                payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            await HandleStepRequestAsync(payload.Unpack<StepRequestEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(WorkflowLlmInvocationCompletedEvent.Descriptor))
        {
            await HandleLlmInvocationCompletedAsync(payload.Unpack<WorkflowLlmInvocationCompletedEvent>(), envelope, ctx, ct);
            return;
        }

        if (payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor))
            await HandleWatchdogTimeoutFiredAsync(payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>(), envelope, ctx, ct);
    }

    private async Task HandleStepRequestAsync(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (request.StepType != "llm_call")
            return;

        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stepId))
        {
            await PublishFailedCompletionAsync(stepId, WorkflowRunIdNormalizer.Normalize(request.RunId), "llm_call step requires non-empty step_id", ctx.AgentId, ctx, ct);
            return;
        }

        var prompt = request.Input ?? string.Empty;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) &&
            !string.IsNullOrEmpty(prefix))
        {
            prompt = prefix.TrimEnd() + "\n\n" + prompt;
        }

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!TryResolvePending(runtimeState, runId, stepId, out var sessionId, out var pendingState))
        {
            var attemptKey = BuildAttemptKey(runId, stepId);
            var attempt = runtimeState.AttemptsByStepId.GetValueOrDefault(attemptKey, 0) + 1;
            runtimeState.AttemptsByStepId[attemptKey] = attempt;
            sessionId = CreateSessionId(ctx.AgentId, runId, stepId, attempt);
            pendingState = new PendingLlmCallState
            {
                StepId = stepId,
                RunId = runId,
                TargetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
                    workflow: null,
                    configuredTargetRole: request.TargetRole,
                    stepType: request.StepType),
                RequestDispatched = false,
                WatchdogCallbackId = BuildWatchdogCallbackId(sessionId),
                DispatchOperationId = BuildDispatchOperationId(sessionId),
                ExecutionId = request.ExecutionId,
                InputValueId = request.InputValueId,
            };
            runtimeState.PendingBySessionId[sessionId] = pendingState;
            await SaveStateAsync(runtimeState, ctx, ct);
        }

        await EnsureWatchdogScheduledAsync(sessionId, pendingState, timeoutMs, ctx, ct);
        pendingState = GetRequiredPending(sessionId, ctx);
        pendingState = await EnsureDispatchOperationIdAsync(sessionId, pendingState, ctx, ct);
        if (pendingState.RequestDispatched)
            return;

        WorkflowStepTargetAgentResolution target;
        try
        {
            target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: target resolution failed for step={StepId}", stepId);
            await FailPendingAsync(sessionId, $"LLM target resolution failed: {ex.Message}", ctx.AgentId, ctx, ct);
            return;
        }

        try
        {
            await DispatchChatRequestAsync(
                request,
                target,
                sessionId,
                pendingState.DispatchOperationId,
                prompt,
                timeoutMs,
                stepId,
                ctx,
                ct);
            await MarkRequestDispatchedAsync(sessionId, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "LLMCallModule: dispatch failed for step={StepId}", stepId);
            await FailPendingAsync(sessionId, $"LLM dispatch failed: {ex.Message}", target.WorkerId, ctx, ct);
        }
    }

    private async Task HandleLlmInvocationCompletedAsync(
        WorkflowLlmInvocationCompletedEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        if (evt.AuthorizationRequirement is not null)
        {
            ctx.Logger.LogInformation(
                "LLMCallModule: run={RunId} step={StepId} session={SessionId} status=interactive_authorization_handoff",
                pending.RunId,
                pending.StepId,
                sessionId);
            return;
        }

        var publisherActorId = envelope.Route?.PublisherActorId ?? ctx.AgentId;
        if (!evt.Success)
        {
            await PublishFailedCompletionAsync(
                pending,
                string.IsNullOrWhiteSpace(evt.Error) ? "LLM call failed." : evt.Error,
                publisherActorId,
                evt.RecoveryFailureKind,
                ctx,
                ct);
            await RemovePendingAsync(sessionId, pending, ctx, ct);
            return;
        }

        if (evt.ManagedHandoff != null && !string.IsNullOrWhiteSpace(evt.ManagedHandoff.InvocationId))
        {
            await RemovePendingAsync(sessionId, pending, ctx, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: run={RunId} step={StepId} session={SessionId} status=completed output_len={OutputLen} output_redacted=true",
            pending.RunId,
            pending.StepId,
            sessionId,
            evt.Content?.Length ?? 0);

        await ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                ExecutionId = pending.ExecutionId,
                Success = true,
                Output = evt.Content ?? string.Empty,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
                WorkerId = publisherActorId,
                Usage = evt.Usage?.Clone(),
            },
            TopologyAudience.Self,
            ct);
        await RemovePendingAsync(sessionId, pending, ctx, ct);
    }

    private async Task HandleWatchdogTimeoutFiredAsync(
        LlmCallWatchdogTimeoutFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(evt.SessionId, out var pending))
            return;

        if (!MatchesWatchdog(envelope, pending))
        {
            ctx.Logger.LogDebug(
                "LLMCallModule: ignore watchdog without matching lease session={SessionId}",
                evt.SessionId);
            return;
        }

        ctx.Logger.LogWarning(
            "LLMCallModule: step={StepId} timeout after {Timeout}ms (run={RunId}).",
            pending.StepId,
            evt.TimeoutMs,
            pending.RunId);

        await PublishFailedCompletionAsync(
            pending,
            $"LLM call timed out after {evt.TimeoutMs}ms",
            ctx.AgentId,
            ctx,
            ct);
        await RemovePendingAsync(evt.SessionId, pending, ctx, ct);
    }

    private async Task FailPendingAsync(
        string sessionId,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pending))
            return;

        await StopWatchdogAsync(pending, ctx, ct);
        await PublishFailedCompletionAsync(pending, error, workerId, ctx, ct);
        await RemovePendingAsync(sessionId, pending, ctx, ct);
    }

    private static int ResolveLlmTimeoutMs(StepRequestEvent request)
    {
        if (request.Parameters.TryGetValue("llm_timeout_ms", out var llmTimeoutRaw) &&
            int.TryParse(llmTimeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var llmTimeoutMs) &&
            llmTimeoutMs > 0)
        {
            return llmTimeoutMs;
        }

        if (request.Parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return DefaultLlmTimeoutMs;
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: LLM override metadata was forwarded from generic execution
    //                item values after string-key lookup.
    //   New principle: LLM override metadata is copied from typed runtime
    //                  override fields after blank-value filtering.
    private static void CopyParametersToChatRequest(
        StepRequestEvent request,
        WorkflowLlmExecutionIntent intent,
        int timeoutMs)
    {
        foreach (var (key, value) in request.Parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            var normalizedKey = key.Trim();
            var normalizedValue = value.Trim();
            if (IsReservedParameter(normalizedKey))
                continue;

            intent.Annotations[normalizedKey] = normalizedValue;
        }
    }

    [SuppressMessage(
        "Maintainability",
        "CA1502:Avoid excessive complexity",
        Justification = "Mechanical boundary normalization from known workflow step keys into typed Telegram fields; splitting would add indirection without changing behavior.")]
    internal static bool IsReservedParameter(string key)
    {
        switch (key)
        {
            case "telegram.timeout_ms":
            case "timeout_ms":
            case "llm_timeout_ms":
            case "aevatar.llm_timeout_ms":
                return true;
            case "run_id":
            case "workflow.run_id":
            case "workflow_run_id":
            case "session_id":
                return true;
            case "step_id":
            case "workflow.step_id":
            case "workflow_step_id":
                return true;
            default:
                return false;
        }
    }

    private WorkflowStepTargetAgentResolver ResolveTargetAgentResolver(IEventContext ctx)
    {
        if (_targetAgentResolver != null)
            return _targetAgentResolver;

        var resolver = ctx.Services.GetService(typeof(WorkflowStepTargetAgentResolver)) as WorkflowStepTargetAgentResolver;
        if (resolver != null)
            return resolver;

        return new WorkflowStepTargetAgentResolver();
    }

    private async Task EnsureWatchdogScheduledAsync(
        string sessionId,
        PendingLlmCallState pending,
        int timeoutMs,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.WatchdogLease != null)
            return;

        var callbackId = string.IsNullOrWhiteSpace(pending.WatchdogCallbackId)
            ? BuildWatchdogCallbackId(sessionId)
            : pending.WatchdogCallbackId;
        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(timeoutMs),
            new LlmCallWatchdogTimeoutFiredEvent
            {
                SessionId = sessionId,
                TimeoutMs = timeoutMs,
                RunId = pending.RunId,
                StepId = pending.StepId,
            },
            ct: ct);

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var persistedPending))
            return;

        persistedPending.WatchdogCallbackId = callbackId;
        persistedPending.WatchdogLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        runtimeState.PendingBySessionId[sessionId] = persistedPending;
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private async Task DispatchChatRequestAsync(
        StepRequestEvent request,
        WorkflowStepTargetAgentResolution target,
        string sessionId,
        string dispatchOperationId,
        string prompt,
        int timeoutMs,
        string stepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var intent = new WorkflowLlmExecutionIntent
        {
            Prompt = prompt,
            SessionId = sessionId,
            TimeoutMs = timeoutMs,
            RunId = WorkflowRunIdNormalizer.Normalize(request.RunId),
            StepId = stepId,
            // Carry the owning run's authoritative scope to the role actor so its tool caller
            // context can be scope-scoped on the channel-less Direct/studio path (no inbound
            // channel stamps the caller scope there). Empty stays empty; the role actor only
            // fills a caller scope that is otherwise unset.
            ScopeId = Normalize(ctx.ScopeId) ?? string.Empty,
            ScheduleId = Normalize(ctx.ScheduleId) ?? string.Empty,
            ToolCatalogPolicyVersion = Normalize(ctx.ToolCatalogPolicyVersion) ?? string.Empty,
        };
        intent.InputFileRefs.Add(request.InputFileRefs.Select(static fileRef => fileRef.Clone()));
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            request.RunId ?? string.Empty,
            stepId);
        intent.WorkflowRuntimeContext = new WorkflowToolRuntimeContextPayload
        {
            ParentActorId = runtimeContext.ParentActorId,
            ParentRunId = runtimeContext.ParentRunId,
            ParentStepId = runtimeContext.ParentStepId,
            RootRunId = runtimeContext.RootRunId,
            Depth = runtimeContext.Depth,
        };
        if (WorkflowRunExecutionContextStateAccess.TryGetLlm(ctx, out var llm))
        {
            intent.Model = Normalize(llm.ModelOverride) ?? string.Empty;
            intent.UserMemoryPrompt = Normalize(llm.UserMemoryPrompt) ?? string.Empty;
            intent.RoutePreference = Normalize(llm.RoutePreference) ?? string.Empty;
            if (llm.HasMaxToolRoundsOverride)
                intent.MaxToolRounds = llm.MaxToolRoundsOverride;
        }
        if (!WorkflowLlmExecutionIntentRuntimeContextAccess.ApplyDurableAgentKeyOrSenderNyxIdAccessToken(
                ctx,
                intent))
        {
            var callerCredential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
            intent.CallerCredential = await BuildRoleCallerCredentialAsync(
                callerCredential,
                HasUnattendedWebhookAuthorization(ctx),
                ct);
        }
        CopyAgentToolScope(request.StepParameters?.AgentToolScope, intent);
        CopyParametersToChatRequest(request, intent, timeoutMs);
        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(ctx, intent.Headers);
        var dispatchOptions = BuildDispatchOptions(dispatchOperationId);

        if (!target.UseSelf)
        {
            ctx.Logger.LogInformation(
                "LLMCallModule: run={RunId} step={StepId} session={SessionId} status=dispatching mode={Mode} actor={ActorId} timeout={Timeout}ms prompt_len={PromptLen} prompt_redacted=true",
                WorkflowRunIdNormalizer.Normalize(request.RunId),
                stepId,
                sessionId,
                target.Mode,
                target.ActorId,
                timeoutMs,
                prompt.Length);
            await ctx.SendToAsync(target.ActorId, intent, ct, dispatchOptions);
            return;
        }

        ctx.Logger.LogInformation(
            "LLMCallModule: run={RunId} step={StepId} session={SessionId} status=dispatching_self timeout={Timeout}ms prompt_len={PromptLen} prompt_redacted=true",
            WorkflowRunIdNormalizer.Normalize(request.RunId),
            stepId,
            sessionId,
            timeoutMs,
            prompt.Length);
        await ctx.PublishAsync(intent, TopologyAudience.Self, ct, dispatchOptions);
    }

    private static bool HasUnattendedWebhookAuthorization(IWorkflowExecutionContext ctx) =>
        ctx is IWorkflowExecutionStateHostAccessor accessor &&
        string.Equals(accessor.StateHost.RunOrigin, WorkflowRunOrigins.Webhook, StringComparison.Ordinal) &&
        accessor.StateHost.ExecutionContextSnapshot.UnattendedEffectAuthorization is not null;

    private async Task<WorkflowCallerCredential> BuildRoleCallerCredentialAsync(
        (bool Found, WorkflowCallerCredential Credential) resolved,
        bool hasUnattendedWebhookAuthorization,
        CancellationToken ct)
    {
        if (!resolved.Found)
            return new WorkflowCallerCredential();

        var durable = resolved.Credential.DurableCallerCredential;
        if (WorkflowLlmExecutionIntentRuntimeContextAccess.IsDurableAgentKeyCredential(durable))
        {
            // Unattended workflows carry only the vault-backed Agent Key handle across
            // the role-actor boundary. The role resolves it locally for every NyxID-backed
            // tool path; a short-lived delegation token never replaces this authority.
            return new WorkflowCallerCredential
            {
                DurableCallerCredential = durable.Clone(),
                Kind = NyxIdCallerCredentialKind.AgentKey,
            };
        }

        if (!hasUnattendedWebhookAuthorization)
        {
            return await WorkflowCallerAccessTokenResolver.ResolveAsync(
                resolved.Credential,
                _callerAccessTokenProvider,
                ct);
        }

        if (durable?.SourceKind != DurableCallerCredentialSourceKind.WebhookBinding)
            return new WorkflowCallerCredential();

        // The role actor receives only the vault handle. It resolves the exact
        // Agent Key locally, so raw binding credentials never cross actor events.
        return new WorkflowCallerCredential
        {
            DurableCallerCredential = durable.Clone(),
            NyxIdAuthority = resolved.Credential.NyxIdAuthority?.Clone(),
            Kind = resolved.Credential.Kind,
        };
    }

    private static Task PublishFailedCompletionAsync(
        PendingLlmCallState pending,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        PublishFailedCompletionAsync(pending, error, workerId, WorkflowRecoveryFailureKind.Unspecified, ctx, ct);

    private static Task PublishFailedCompletionAsync(
        PendingLlmCallState pending,
        string error,
        string workerId,
        WorkflowRecoveryFailureKind recoveryFailureKind,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        PublishFailedCompletionAsync(
            pending.StepId,
            pending.RunId,
            error,
            workerId,
            recoveryFailureKind,
            pending.ExecutionId,
            ctx,
            ct);

    private static Task PublishFailedCompletionAsync(
        string stepId,
        string runId,
        string error,
        string workerId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        PublishFailedCompletionAsync(
            stepId,
            runId,
            error,
            workerId,
            WorkflowRecoveryFailureKind.Unspecified,
            string.Empty,
            ctx,
            ct);

    private static Task PublishFailedCompletionAsync(
        string stepId,
        string runId,
        string error,
        string workerId,
        WorkflowRecoveryFailureKind recoveryFailureKind,
        string executionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                ExecutionId = executionId,
                Success = false,
                Error = error,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
                WorkerId = string.IsNullOrWhiteSpace(workerId) ? ctx.AgentId : workerId,
                RecoveryFailureKind = recoveryFailureKind,
            },
            TopologyAudience.Self,
            ct);

    private static string BuildWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(LlmWatchdogCallbackPrefix, sessionId);

    private static string BuildDispatchOperationId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-llm-dispatch", sessionId);

    private static string BuildAttemptKey(string runId, string stepId) =>
        string.IsNullOrWhiteSpace(runId) ? stepId : $"{runId}:{stepId}";

    private static string CreateSessionId(string scopeId, string runId, string stepId, int attempt) =>
        string.IsNullOrWhiteSpace(runId)
            ? WorkflowChatSessionKeys.CreateWorkflowStepSessionId(scopeId, $"{stepId}:a{attempt}")
            : WorkflowChatSessionKeys.CreateWorkflowStepSessionId(scopeId, runId, stepId, attempt);

    private static EventEnvelopePublishOptions BuildDispatchOptions(string dispatchOperationId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = dispatchOperationId,
            },
        };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void CopyAgentToolScope(
        WorkflowAgentToolScope? source,
        WorkflowLlmExecutionIntent intent)
    {
        if (source == null)
            return;

        intent.AgentToolScope = new WorkflowAgentToolScope
        {
            RestrictAllowedToolNames = source.RestrictAllowedToolNames || source.AllowedToolNames.Count > 0,
            RestrictToolSets = source.RestrictToolSets || source.ToolSetRefs.Count > 0,
        };
        foreach (var toolName in source.AllowedToolNames)
        {
            var normalized = Normalize(toolName);
            if (normalized is not null)
                intent.AgentToolScope.AllowedToolNames.Add(normalized);
        }
        foreach (var toolSetRef in source.ToolSetRefs)
        {
            var normalized = Normalize(toolSetRef);
            if (normalized is not null)
                intent.AgentToolScope.ToolSetRefs.Add(normalized);
        }
    }

    private static bool TryResolvePending(
        LLMCallModuleState state,
        string runId,
        string stepId,
        out string sessionId,
        out PendingLlmCallState pending)
    {
        foreach (var entry in state.PendingBySessionId)
        {
            if (!string.Equals(entry.Value.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(entry.Value.StepId, stepId, StringComparison.Ordinal))
            {
                continue;
            }

            sessionId = entry.Key;
            pending = entry.Value;
            return true;
        }

        sessionId = string.Empty;
        pending = default!;
        return false;
    }

    private static PendingLlmCallState GetRequiredPending(string sessionId, IWorkflowExecutionContext ctx)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        return runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pendingState)
            ? pendingState
            : throw new InvalidOperationException($"Missing pending LLM call state for session {sessionId}.");
    }

    private static async Task MarkRequestDispatchedAsync(
        string sessionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out var pendingState))
            return;

        pendingState.RequestDispatched = true;
        runtimeState.PendingBySessionId[sessionId] = pendingState;
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private static async Task<PendingLlmCallState> EnsureDispatchOperationIdAsync(
        string sessionId,
        PendingLlmCallState pendingState,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pendingState.DispatchOperationId))
            return pendingState;

        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.TryGetValue(sessionId, out pendingState))
            throw new InvalidOperationException($"Missing pending LLM call state for session {sessionId}.");

        pendingState.DispatchOperationId = BuildDispatchOperationId(sessionId);
        runtimeState.PendingBySessionId[sessionId] = pendingState;
        await SaveStateAsync(runtimeState, ctx, ct);
        return pendingState;
    }

    private static async Task RemovePendingAsync(
        string sessionId,
        PendingLlmCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeState = WorkflowExecutionStateAccess.Load<LLMCallModuleState>(ctx, ModuleStateKey);
        if (!runtimeState.PendingBySessionId.Remove(sessionId))
            return;

        runtimeState.AttemptsByStepId.Remove(BuildAttemptKey(pending.RunId, pending.StepId));
        await SaveStateAsync(runtimeState, ctx, ct);
    }

    private static bool MatchesWatchdog(EventEnvelope envelope, PendingLlmCallState pending)
    {
        if (pending.WatchdogLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.WatchdogLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.WatchdogCallbackId, StringComparison.Ordinal);
    }

    private static async Task StopWatchdogAsync(
        PendingLlmCallState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            pending.WatchdogLease,
            "LLMCallModule watchdog cleanup",
            ct);
    }

    private static Task SaveStateAsync(
        LLMCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingBySessionId.Count == 0 && state.AttemptsByStepId.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }
}
