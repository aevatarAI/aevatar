// ─────────────────────────────────────────────────────────────
// HumanApprovalModule — 人工审批模块
// 暂停工作流执行，等待人工审批后继续或终止
// Inspired by MAF's Confirmation / RequestExternalInput actions
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Human approval module. Handles step_type == "human_approval".
/// Suspends workflow and waits for a WorkflowResumedEvent.
/// </summary>
public sealed class HumanApprovalModule : IEventModule
{
    private readonly Dictionary<(string RunId, string StepId), StepRequestEvent> _pending = [];

    public string Name => "human_approval";
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

        // ─── Handle StepRequestEvent: suspend workflow ───
        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "human_approval") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);

            var prompt = request.Parameters.GetValueOrDefault("prompt", "Approve this step?");
            var timeoutSeconds = int.TryParse(
                request.Parameters.GetValueOrDefault("timeout", "3600"), out var t) ? t : 3600;

            _pending[(runId, request.StepId)] = request;

            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} suspended, prompt=\"{Prompt}\", timeout={Timeout}s",
                runId, request.StepId, prompt, timeoutSeconds);

            await ctx.PublishAsync(new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = "human_approval",
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
            }, EventDirection.Both, ct);
            return;
        }

        // ─── Handle WorkflowResumedEvent: resume or reject ───
        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            var resumed = payload.Unpack<WorkflowResumedEvent>();
            if (!TryResolvePending(resumed, out var pendingKey, out var pending))
                return;
            _pending.Remove(pendingKey);

            var onReject = pending.Parameters.GetValueOrDefault("on_reject", "fail");

            if (resumed.Approved)
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} approved",
                    pending.RunId,
                    pending.StepId);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = true,
                    Output = string.IsNullOrEmpty(resumed.UserInput) ? pending.Input : resumed.UserInput,
                }, EventDirection.Self, ct);
            }
            else
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} rejected, on_reject={OnReject}",
                    pending.RunId,
                    pending.StepId,
                    onReject);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = onReject != "fail",
                    Output = pending.Input,
                    Error = onReject == "fail" ? "Human approval rejected" : "",
                }, EventDirection.Self, ct);
            }
        }
    }

    private bool TryResolvePending(
        WorkflowResumedEvent resumed,
        out (string RunId, string StepId) pendingKey,
        out StepRequestEvent pending)
    {
        if (!string.IsNullOrWhiteSpace(resumed.RunId))
        {
            pendingKey = (WorkflowRunIdNormalizer.Normalize(resumed.RunId), resumed.StepId);
            return _pending.TryGetValue(pendingKey, out pending!);
        }

        // Backward compatibility: old clients may omit run_id.
        var matchCount = 0;
        pendingKey = default;
        pending = default!;
        foreach (var entry in _pending)
        {
            if (!entry.Key.StepId.Equals(resumed.StepId, StringComparison.Ordinal))
                continue;

            matchCount++;
            if (matchCount > 1)
                return false;

            pendingKey = entry.Key;
            pending = entry.Value;
        }

        return matchCount == 1;
    }

}
