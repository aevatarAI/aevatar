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

            var prompt = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "Approve this step?",
                "prompt",
                "message");
            var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
                request.Parameters,
                defaultSeconds: 3600);

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

            var onReject = pending.Parameters.GetValueOrDefault("on_reject", "fail");

            if (resumed.Approved)
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} approved",
                    pending.RunId,
                    pending.StepId);
                var approved = new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = true,
                    Output = string.IsNullOrEmpty(resumed.UserInput) ? pending.Input : resumed.UserInput,
                };
                approved.Metadata["branch"] = "true";
                await ctx.PublishAsync(approved, EventDirection.Self, ct);
                _pending.Remove(pendingKey);
            }
            else
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} rejected, on_reject={OnReject}",
                    pending.RunId,
                    pending.StepId,
                    onReject);

                var rejectionOutput = !string.IsNullOrEmpty(resumed.UserInput)
                    ? $"[Previous content]\n{pending.Input}\n\n[User feedback]\n{resumed.UserInput}"
                    : pending.Input;

                var rejected = new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = onReject != "fail",
                    Output = rejectionOutput,
                    Error = onReject == "fail" ? "Human approval rejected" : "",
                };
                rejected.Metadata["branch"] = "false";
                await ctx.PublishAsync(rejected, EventDirection.Self, ct);
                _pending.Remove(pendingKey);
            }
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
