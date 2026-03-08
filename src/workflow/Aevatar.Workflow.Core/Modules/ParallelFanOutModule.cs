// ─────────────────────────────────────────────────────────────
// ParallelFanOutModule — 并行扇出模块
// 将 parallel 类型步骤拆分为 N 个子步骤并行执行，收齐后合并输出
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// 并行扇出模块。处理 parallel 类型步骤：拆分 N 个子步骤并行下发，收齐后合并结果并发布 StepCompletedEvent。
/// </summary>
public sealed class ParallelFanOutModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "parallel_fanout";

    /// <summary>
    /// 模块名称。
    /// </summary>
    public string Name => "parallel_fanout";

    /// <summary>
    /// 处理优先级，数值越小优先级越高。
    /// </summary>
    public int Priority => 5;

    /// <summary>
    /// 判断是否可处理该事件。
    /// </summary>
    /// <param name="envelope">事件信封。</param>
    /// <returns>若为 StepRequestEvent 或 StepCompletedEvent 则返回 true。</returns>
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    /// <summary>
    /// 处理事件。请求时拆分并行子步骤；完成时合并子步骤结果，满足数量后发布父步骤完成。
    /// </summary>
    /// <param name="envelope">事件信封。</param>
    /// <param name="ctx">事件处理上下文。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "parallel") return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var count = evt.Parameters.TryGetValue("parallel_count", out var cs) && int.TryParse(cs, out var n) ? n : 3;
            var state = WorkflowExecutionStateAccess.Load<ParallelFanOutModuleState>(ctx, ModuleStateKey);
            // Resolve which worker roles to fan out to
            // If step has workers param (comma-separated role IDs), use those; else generate generic sub-steps
            var workerRoles = new List<string>();
            if (evt.Parameters.TryGetValue("workers", out var workersParam) && !string.IsNullOrEmpty(workersParam))
            {
                workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workersParam));
                count = workerRoles.Count;
            }

            if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(evt.TargetRole))
            {
                ctx.Logger.LogWarning(
                    "ParallelFanOut: step={StepId} missing workers and target_role; cannot fan-out.",
                    evt.StepId);
                state.Parents.Remove(evt.StepId);
                await SaveStateAsync(state, ctx, ct);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = evt.StepId,
                    RunId = runId,
                    Success = false,
                    Error = "parallel requires parameters.workers (CSV/JSON list) or target_role",
                }, EventDirection.Self, ct);
                return;
            }

            var voteStepType = evt.Parameters.TryGetValue("vote_step_type", out var vst) ? vst : "";
            var voteParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Parameters)
            {
                if (key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase))
                    voteParams[key["vote_param_".Length..]] = value;
            }
            var parentState = new ParallelParentState
            {
                Expected = count,
                VoteConfig = new VoteConfigState
                {
                    StepType = voteStepType,
                },
            };
            foreach (var (key, value) in voteParams)
                parentState.VoteConfig.Parameters[key] = value;

            state.Parents[evt.StepId] = parentState;
            await SaveStateAsync(state, ctx, ct);

            var inputPreview = evt.Input.Length > 150 ? evt.Input[..150] + "..." : evt.Input;
            ctx.Logger.LogInformation("ParallelFanOut: step={StepId} fanout to {Count} workers, vote={VoteType}, input=({Len} chars) {Preview}",
                evt.StepId, count, string.IsNullOrWhiteSpace(voteStepType) ? "(none)" : voteStepType, evt.Input.Length, inputPreview);

            for (var i = 0; i < count; i++)
            {
                var role = i < workerRoles.Count ? workerRoles[i] : evt.TargetRole;
                await ctx.PublishAsync(new StepRequestEvent
                {
                    StepId = $"{evt.StepId}_sub_{i}",
                    StepType = "llm_call",
                    RunId = runId,
                    Input = evt.Input,
                    TargetRole = role ?? "",
                }, EventDirection.Self, ct);
            }
        }
        else
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var eventRunId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowExecutionStateAccess.Load<ParallelFanOutModuleState>(ctx, ModuleStateKey);

            // Vote result: map back to parent parallel step.
            if (state.VoteStepToParent.TryGetValue(evt.StepId, out var voteParentStepId))
            {
                state.VoteStepToParent.Remove(evt.StepId);
                var workersSuccess = state.ParentWorkerSuccess.TryGetValue(voteParentStepId, out var successFromWorkers) &&
                                     successFromWorkers;
                state.ParentWorkerSuccess.Remove(voteParentStepId);
                state.Parents.Remove(voteParentStepId);
                await SaveStateAsync(state, ctx, ct);

                var final = new StepCompletedEvent
                {
                    StepId = voteParentStepId,
                    RunId = eventRunId,
                    Success = workersSuccess && evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                    WorkerId = evt.WorkerId,
                };

                foreach (var (key, value) in evt.Metadata)
                    final.Metadata[key] = value;
                final.Metadata["parallel.used_vote"] = "true";
                final.Metadata["parallel.vote_step_id"] = evt.StepId;
                final.Metadata["parallel.workers_success"] = workersSuccess.ToString();

                await ctx.PublishAsync(final, EventDirection.Self, ct);
                return;
            }

            var parent = evt.StepId.LastIndexOf("_sub_", StringComparison.Ordinal) is var idx and > 0 ? evt.StepId[..idx] : null;
            if (parent == null) return;
            if (!state.Parents.TryGetValue(parent, out var parentState)) return;
            parentState.Collected.Add(evt.ToParallelItemResult());
            state.Parents[parent] = parentState;
            ctx.Logger.LogInformation("ParallelFanOut: collected {StepId} ({Count}/{Expected})",
                evt.StepId, parentState.Collected.Count, parentState.Expected);
            if (parentState.Collected.Count >= parentState.Expected)
            {
                var results = parentState.Collected;
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join("\n---\n", results.Select(r => r.Output));
                ctx.Logger.LogInformation("ParallelFanOut: step={StepId} all {Count} workers done, merged=({Len} chars)",
                    parent, results.Count, merged.Length);

                if (!string.IsNullOrWhiteSpace(parentState.VoteConfig.StepType))
                {
                    var voteStepId = $"{parent}_vote";
                    state.VoteStepToParent[voteStepId] = parent;
                    state.ParentWorkerSuccess[parent] = allSuccess;
                    await SaveStateAsync(state, ctx, ct);

                    var voteReq = new StepRequestEvent
                    {
                        StepId = voteStepId,
                        StepType = parentState.VoteConfig.StepType,
                        RunId = eventRunId,
                        Input = merged,
                    };
                    foreach (var (key, value) in parentState.VoteConfig.Parameters)
                        voteReq.Parameters[key] = value;

                    ctx.Logger.LogInformation(
                        "ParallelFanOut: step={StepId} dispatch vote step={VoteStepId} type={VoteType}",
                        parent, voteStepId, parentState.VoteConfig.StepType);

                    await ctx.PublishAsync(voteReq, EventDirection.Self, ct);
                }
                else
                {
                    state.Parents.Remove(parent);
                    await SaveStateAsync(state, ctx, ct);
                    var completed = new StepCompletedEvent
                    {
                        StepId = parent,
                        RunId = eventRunId,
                        Success = allSuccess,
                        Output = merged,
                    };
                    completed.Metadata["parallel.used_vote"] = "false";
                    await ctx.PublishAsync(completed, EventDirection.Self, ct);
                }
            }
            else
            {
                await SaveStateAsync(state, ctx, ct);
            }
        }
    }

    private static Task SaveStateAsync(
        ParallelFanOutModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0 &&
            state.VoteStepToParent.Count == 0 &&
            state.ParentWorkerSuccess.Count == 0)
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
