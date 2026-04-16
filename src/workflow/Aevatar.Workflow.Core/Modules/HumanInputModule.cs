// ─────────────────────────────────────────────────────────────
// HumanInputModule — 人工输入模块
// 暂停工作流执行，等待人工提供输入后继续
// Inspired by MAF's Question / WaitForInput actions
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Human input module. Handles step_type == "human_input".
/// Suspends workflow and waits for a WorkflowResumedEvent carrying user input.
/// </summary>
public sealed class HumanInputModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "human_input";

    public string Name => "human_input";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowResumedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        // ─── Handle StepRequestEvent: suspend and ask for input ───
        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "human_input") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);

            var prompt = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "Please provide input:",
                "prompt",
                "message");
            var variable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "user_input",
                "variable");
            var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
                request.Parameters,
                defaultSeconds: 1800);

            var state = WorkflowExecutionStateAccess.Load<HumanInputModuleState>(ctx, ModuleStateKey);
            state.Pending[BuildPendingKey(runId, request.StepId)] = new PendingHumanInputState
            {
                StepId = request.StepId,
                RunId = runId,
                Input = request.Input ?? string.Empty,
                OnTimeout = request.Parameters.GetValueOrDefault("on_timeout", "fail"),
            };
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation(
                "HumanInput: run={RunId} step={StepId} suspended, prompt=\"{Prompt}\", variable={Var}, timeout={Timeout}s",
                runId, request.StepId, prompt, variable, timeoutSeconds);

            var suspended = new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = "human_input",
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
                VariableName = variable,
            };
            WorkflowSuspensionRequestSupport.ApplyContent(suspended, request.Input);
            WorkflowSuspensionRequestSupport.ApplyDeliveryTarget(suspended, request);

            await ctx.PublishAsync(suspended, TopologyAudience.ParentAndChildren, ct);
            return;
        }

        // ─── Handle WorkflowResumedEvent: use provided input ───
        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            var resumed = payload.Unpack<WorkflowResumedEvent>();
            var state = WorkflowExecutionStateAccess.Load<HumanInputModuleState>(ctx, ModuleStateKey);
            if (!TryResolvePending(state, resumed, out var pendingKey, out var pending))
                return;

            var userInput = resumed.UserInput;
            var onTimeout = pending.OnTimeout;

            if (string.IsNullOrEmpty(userInput) && !resumed.Approved)
            {
                ctx.Logger.LogWarning(
                    "HumanInput: run={RunId} step={StepId} timed out or cancelled",
                    pending.RunId,
                    pending.StepId);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = onTimeout != "fail",
                    Output = pending.Input,
                    Error = onTimeout == "fail" ? "Human input timed out" : "",
                }, TopologyAudience.Self, ct);
                state.Pending.Remove(pendingKey);
                await SaveStateAsync(state, ctx, ct);
                return;
            }

            ctx.Logger.LogInformation(
                "HumanInput: run={RunId} step={StepId} received input ({Len} chars)",
                pending.RunId,
                pending.StepId,
                userInput?.Length ?? 0);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = true,
                Output = userInput ?? "",
            }, TopologyAudience.Self, ct);
            state.Pending.Remove(pendingKey);
            await SaveStateAsync(state, ctx, ct);
        }
    }

    private bool TryResolvePending(
        HumanInputModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingHumanInputState pending)
    {
        pendingKey = string.Empty;
        pending = new PendingHumanInputState();
        if (string.IsNullOrWhiteSpace(resumed.RunId))
            return false;

        pendingKey = BuildPendingKey(
            WorkflowRunIdNormalizer.Normalize(resumed.RunId),
            resumed.StepId ?? string.Empty);
        if (!state.Pending.TryGetValue(pendingKey, out var resolvedPending))
            return false;

        pending = resolvedPending;
        return string.Equals(
            pending.RunId,
            WorkflowRunIdNormalizer.Normalize(resumed.RunId),
            StringComparison.Ordinal);
    }

    private static string BuildPendingKey(string runId, string stepId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}";

    private static Task SaveStateAsync(
        HumanInputModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
