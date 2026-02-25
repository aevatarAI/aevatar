// ─────────────────────────────────────────────────────────────
// ParallelFanOutModule — 并行扇出模块
// 将 parallel 类型步骤拆分为 N 个子步骤并行执行，收齐后合并输出
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// 并行扇出模块。处理 parallel 类型步骤：拆分 N 个子步骤并行下发，收齐后合并结果并发布 StepCompletedEvent。
/// </summary>
public sealed class ParallelFanOutModule : IEventModule
{
    private readonly Dictionary<(string RunId, string StepId), int> _expected = [];
    private readonly Dictionary<(string RunId, string StepId), List<StepCompletedEvent>> _collected = [];
    private readonly Dictionary<(string RunId, string StepId), VoteConfig> _voteConfigs = [];
    private readonly Dictionary<(string RunId, string StepId), (string RunId, string StepId)> _voteStepToParent = [];
    private readonly Dictionary<(string RunId, string StepId), bool> _parentWorkerSuccess = [];

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
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "parallel") return;
            var runId = NormalizeRunId(evt.RunId);
            var parentKey = (runId, evt.StepId);
            var count = evt.Parameters.TryGetValue("parallel_count", out var cs) && int.TryParse(cs, out var n) ? n : 3;
            _expected[parentKey] = count;
            _collected[parentKey] = [];
            // Resolve which worker roles to fan out to
            // If step has workers param (comma-separated role IDs), use those; else generate generic sub-steps
            var workerRoles = new List<string>();
            if (evt.Parameters.TryGetValue("workers", out var workersParam) && !string.IsNullOrEmpty(workersParam))
            {
                workerRoles.AddRange(workersParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                count = workerRoles.Count;
                _expected[parentKey] = count;
            }

            var voteStepType = evt.Parameters.TryGetValue("vote_step_type", out var vst) ? vst : "";
            var voteParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Parameters)
            {
                if (key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase))
                    voteParams[key["vote_param_".Length..]] = value;
            }
            _voteConfigs[parentKey] = new VoteConfig(voteStepType, voteParams);

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
            var evt = payload.Unpack<StepCompletedEvent>();
            var eventRunId = NormalizeRunId(evt.RunId);

            // Vote result: map back to parent parallel step.
            var voteStepKey = (eventRunId, evt.StepId);
            if (_voteStepToParent.TryGetValue(voteStepKey, out var voteParentKey))
            {
                _voteStepToParent.Remove(voteStepKey);
                _voteConfigs.Remove(voteParentKey);

                var workersSuccess = _parentWorkerSuccess.TryGetValue(voteParentKey, out var successFromWorkers) &&
                                     successFromWorkers;
                _parentWorkerSuccess.Remove(voteParentKey);

                var final = new StepCompletedEvent
                {
                    StepId = voteParentKey.StepId,
                    RunId = voteParentKey.RunId,
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
            var parentKey = (eventRunId, parent);
            if (!_collected.ContainsKey(parentKey)) return;
            _collected[parentKey].Add(evt);
            ctx.Logger.LogInformation("ParallelFanOut: collected {StepId} ({Count}/{Expected})",
                evt.StepId, _collected[parentKey].Count, _expected[parentKey]);
            if (_collected[parentKey].Count >= _expected[parentKey])
            {
                var results = _collected[parentKey];
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join("\n---\n", results.Select(r => r.Output));
                ctx.Logger.LogInformation("ParallelFanOut: step={StepId} all {Count} workers done, merged=({Len} chars)",
                    parent, results.Count, merged.Length);

                if (_voteConfigs.TryGetValue(parentKey, out var voteConfig) &&
                    !string.IsNullOrWhiteSpace(voteConfig.StepType))
                {
                    var voteStepId = $"{parent}_vote";
                    var voteStepKey2 = (eventRunId, voteStepId);
                    _voteStepToParent[voteStepKey2] = parentKey;
                    _parentWorkerSuccess[parentKey] = allSuccess;

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
                    _voteConfigs.Remove(parentKey);
                }

                _collected.Remove(parentKey);
                _expected.Remove(parentKey);
            }
        }
    }

    private static string NormalizeRunId(string runId) => string.IsNullOrWhiteSpace(runId) ? string.Empty : runId;

    private sealed record VoteConfig(string StepType, Dictionary<string, string> Parameters);
}
