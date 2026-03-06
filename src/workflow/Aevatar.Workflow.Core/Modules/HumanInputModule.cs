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
public sealed class HumanInputModule : IEventModule
{
    private readonly Dictionary<(string RunId, string StepId), StepRequestEvent> _pending = [];

    public string Name => "human_input";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowResumedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
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

            _pending[(runId, request.StepId)] = request;

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
            };
            suspended.Metadata["variable"] = variable;

            await ctx.PublishAsync(suspended, EventDirection.Both, ct);
            return;
        }

        // ─── Handle WorkflowResumedEvent: use provided input ───
        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            var resumed = payload.Unpack<WorkflowResumedEvent>();
            if (!TryResolvePending(resumed, out var pendingKey, out var pending))
                return;

            var userInput = resumed.UserInput;
            var onTimeout = pending.Parameters.GetValueOrDefault("on_timeout", "fail");

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
                }, EventDirection.Self, ct);
                _pending.Remove(pendingKey);
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
            }, EventDirection.Self, ct);
            _pending.Remove(pendingKey);
        }
    }

    private bool TryResolvePending(
        WorkflowResumedEvent resumed,
        out (string RunId, string StepId) pendingKey,
        out StepRequestEvent pending)
    {
        pendingKey = default;
        pending = default!;
        if (string.IsNullOrWhiteSpace(resumed.RunId))
            return false;

        pendingKey = (WorkflowRunIdNormalizer.Normalize(resumed.RunId), resumed.StepId);
        return _pending.TryGetValue(pendingKey, out pending!);
    }

}
