// ─────────────────────────────────────────────────────────────
// ParallelFanOutModule — 并行扇出模块
// 将 parallel 类型步骤拆分为 N 个子步骤并行执行，收齐后合并输出
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>
/// 并行扇出模块。处理 parallel 类型步骤：拆分 N 个子步骤并行下发，收齐后合并结果并发布 StepCompletedEvent。
/// </summary>
public sealed class ParallelFanOutModule : IEventModule
{
    private readonly Dictionary<string, int> _expected = [];
    private readonly Dictionary<string, List<StepCompletedEvent>> _collected = [];
    private readonly Dictionary<string, VoteConfig> _voteConfigs = [];
    private readonly Dictionary<string, string> _voteStepToParent = [];
    private readonly Dictionary<string, bool> _parentWorkerSuccess = [];

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
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true ||
        envelope.Payload?.TypeUrl?.Contains("StepCompletedEvent") == true;

    /// <summary>
    /// 处理事件。请求时拆分并行子步骤；完成时合并子步骤结果，满足数量后发布父步骤完成。
    /// </summary>
    /// <param name="envelope">事件信封。</param>
    /// <param name="ctx">事件处理上下文。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload!.TypeUrl.Contains("StepRequestEvent"))
        {
            var evt = envelope.Payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "parallel") return;
            var count = evt.Parameters.TryGetValue("parallel_count", out var cs) && int.TryParse(cs, out var n) ? n : 3;
            _expected[evt.StepId] = count; _collected[evt.StepId] = [];
            // Resolve which worker roles to fan out to
            // If step has workers param (comma-separated role IDs), use those; else generate generic sub-steps
            var workerRoles = new List<string>();
            if (evt.Parameters.TryGetValue("workers", out var workersParam) && !string.IsNullOrEmpty(workersParam))
            {
                workerRoles.AddRange(workersParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                count = workerRoles.Count;
                _expected[evt.StepId] = count;
            }

            var voteStepType = evt.Parameters.TryGetValue("vote_step_type", out var vst) ? vst : "";
            var voteParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Parameters)
            {
                if (key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase))
                    voteParams[key["vote_param_".Length..]] = value;
            }
            _voteConfigs[evt.StepId] = new VoteConfig(voteStepType, voteParams);

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
                    RunId = evt.RunId,
                    Input = evt.Input,
                    TargetRole = role ?? "",
                }, EventDirection.Self, ct);
            }
        }
        else
        {
            var evt = envelope.Payload.Unpack<StepCompletedEvent>();

            // Vote result: map back to parent parallel step.
            if (_voteStepToParent.TryGetValue(evt.StepId, out var voteParent))
            {
                _voteStepToParent.Remove(evt.StepId);
                _voteConfigs.Remove(voteParent);

                var workersSuccess = _parentWorkerSuccess.TryGetValue(voteParent, out var successFromWorkers) &&
                                     successFromWorkers;
                _parentWorkerSuccess.Remove(voteParent);

                var final = new StepCompletedEvent
                {
                    StepId = voteParent,
                    RunId = evt.RunId,
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
            if (parent == null || !_collected.ContainsKey(parent)) return;
            _collected[parent].Add(evt);
            ctx.Logger.LogInformation("ParallelFanOut: collected {StepId} ({Count}/{Expected})",
                evt.StepId, _collected[parent].Count, _expected[parent]);
            if (_collected[parent].Count >= _expected[parent])
            {
                var results = _collected[parent];
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join("\n---\n", results.Select(r => r.Output));
                ctx.Logger.LogInformation("ParallelFanOut: step={StepId} all {Count} workers done, merged=({Len} chars)",
                    parent, results.Count, merged.Length);

                if (_voteConfigs.TryGetValue(parent, out var voteConfig) &&
                    !string.IsNullOrWhiteSpace(voteConfig.StepType))
                {
                    var voteStepId = $"{parent}_vote";
                    _voteStepToParent[voteStepId] = parent;
                    _parentWorkerSuccess[parent] = allSuccess;

                    var voteReq = new StepRequestEvent
                    {
                        StepId = voteStepId,
                        StepType = voteConfig.StepType,
                        RunId = evt.RunId,
                        Input = merged,
                    };
                    foreach (var (key, value) in voteConfig.Parameters)
                        voteReq.Parameters[key] = value;

                    ctx.Logger.LogInformation(
                        "ParallelFanOut: step={StepId} dispatch vote step={VoteStepId} type={VoteType}",
                        parent, voteStepId, voteConfig.StepType);

                    await ctx.PublishAsync(voteReq, EventDirection.Self, ct);
                }
                else
                {
                    var completed = new StepCompletedEvent
                    {
                        StepId = parent,
                        RunId = evt.RunId,
                        Success = allSuccess,
                        Output = merged,
                    };
                    completed.Metadata["parallel.used_vote"] = "false";
                    await ctx.PublishAsync(completed, EventDirection.Self, ct);
                    _voteConfigs.Remove(parent);
                }

                _collected.Remove(parent);
                _expected.Remove(parent);
            }
        }
    }

    private sealed record VoteConfig(string StepType, Dictionary<string, string> Parameters);
}
