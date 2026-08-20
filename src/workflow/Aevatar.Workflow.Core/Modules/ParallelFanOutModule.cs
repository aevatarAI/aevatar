// ─────────────────────────────────────────────────────────────
// ParallelFanOutModule — 并行扇出模块
// 将 parallel 类型步骤拆分为 N 个子步骤并行执行，收齐后合并输出
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Agreement;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// 并行扇出模块。处理 parallel 类型步骤：拆分 N 个子步骤并行下发，收齐后合并结果并发布 StepCompletedEvent。
/// Refactor (iter11/cluster-021): Old backpressure reporting read raw Queue.Count after head removals.
/// Refactor (iter11/cluster-021): New reporting uses cursor-aware queued count while helper preserves FIFO.
/// </summary>
public sealed class ParallelFanOutModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "parallel_fanout";
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();

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
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var parentKey = BuildParentAttemptKey(runId, evt.StepId, evt.ExecutionId, normalizedValues);
            RemoveSupersededParentAttempts(state, runId, evt.StepId, parentKey);
            // Resolve which worker roles to fan out to
            // If step has workers param (comma-separated role IDs), use those; else generate generic sub-steps
            var workerRoles = new List<string>();
            if (evt.Parameters.TryGetValue("workers", out var workersParam) && !string.IsNullOrEmpty(workersParam))
            {
                workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workersParam));
                count = workerRoles.Count;
            }

            var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
                WorkflowParameterValueParser.GetString(
                    evt.Parameters,
                    "llm_call",
                    "sub_step_type",
                    "child_step_type"));
            var subTargetRole = WorkflowParameterValueParser.GetString(
                evt.Parameters,
                evt.TargetRole,
                "sub_target_role",
                "child_target_role");

            if (workerRoles.Count == 0 &&
                string.IsNullOrWhiteSpace(subTargetRole) &&
                string.Equals(subStepType, "llm_call", StringComparison.Ordinal))
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
                    ExecutionId = evt.ExecutionId,
                    Success = false,
                    Error = "parallel requires parameters.workers (CSV/JSON list) or target_role for llm_call workers",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                }, TopologyAudience.Self, ct);
                return;
            }

            var voteStepType = evt.Parameters.TryGetValue("vote_step_type", out var vst) ? vst : "";
            var voteParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Parameters)
            {
                var voteParameterKey = VoteAgreementRuleConfigurationParser.StripVoteParameterPrefix(key);
                if (voteParameterKey != null)
                    voteParams[voteParameterKey] = value;
            }
            var voteRule = new VoteAgreementRule();
            if (!string.IsNullOrWhiteSpace(voteStepType) &&
                string.Equals(
                    WorkflowPrimitiveCatalog.ToCanonicalType(voteStepType),
                    "vote",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!VoteAgreementRuleConfigurationParser.TryParse(voteParams, out voteRule, out var voteRuleError))
                {
                    state.Parents.Remove(evt.StepId);
                    await SaveStateAsync(state, ctx, ct);
                    await ctx.PublishAsync(new StepCompletedEvent
                    {
                        StepId = evt.StepId,
                        RunId = runId,
                        ExecutionId = evt.ExecutionId,
                        Success = false,
                        Error = voteRuleError,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    }, TopologyAudience.Self, ct);
                    return;
                }
            }
            var parentState = new ParallelParentState
            {
                Expected = count,
                ParentRunId = runId,
                ParentStepId = evt.StepId,
                ParentExecutionId = evt.ExecutionId,
                VoteConfig = new VoteConfigState
                {
                    StepType = WorkflowPrimitiveCatalog.ToCanonicalType(voteStepType),
                    VoteRule = voteRule,
                },
            };
            foreach (var (key, value) in voteParams)
                parentState.VoteConfig.Parameters[key] = value;

            state.Parents[parentKey] = parentState;

            var maxConcurrent = BackpressureHelper.ResolveMaxConcurrent(evt.Parameters);
            var minConcurrent = BackpressureHelper.ResolveMinConcurrent(evt.Parameters, maxConcurrent);
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                maxConcurrent,
                minConcurrent);

            // Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
            //   Old pattern: Information log included raw value/prompt/input preview
            //   New principle: only stable id + length + status + redaction marker
            ctx.Logger.LogInformation(
                "ParallelFanOut: run={RunId} step={StepId} status=fanout_dispatching workers={Count} vote={VoteType} input_len={InputLen} input_redacted=true",
                runId,
                evt.StepId,
                count,
                string.IsNullOrWhiteSpace(voteStepType) ? "(none)" : voteStepType,
                evt.Input.Length);

            BackpressureAppliedEvent? backpressureApplied = null;
            var immediateDispatches = new List<BackpressureQueueEntry>();
            for (var i = 0; i < count; i++)
            {
                var role = i < workerRoles.Count ? workerRoles[i] : subTargetRole;
                var subParameters = ResolveSubStepParameters(evt.Parameters, evt.Input, role, i);
                var entry = BackpressureHelper.ToQueueEntry(
                    BuildChildStepId(evt.StepId, i, evt.ExecutionId, normalizedValues),
                    subStepType,
                    runId,
                    evt.Input,
                    role ?? "",
                    subParameters,
                    externalInvocation: evt.ExternalInvocation,
                    executionId: WorkflowExecutionValueStore.BuildInternalExecutionId(
                        runId,
                        evt.ExecutionId,
                        $"{evt.StepId}_sub_{i}"),
                    inputValueId: evt.InputValueId);
                if (WorkflowExecutionValueStore.IsNormalized(kernelState))
                {
                    var childRequest = BackpressureHelper.ToStepRequest(entry, kernelState);
                    WorkflowExecutionValueStore.PrepareInternalDispatch(
                        kernelState,
                        childRequest,
                        originEnvelopeId: null);
                    entry.ExecutionId = childRequest.ExecutionId;
                    entry.InputValueId = childRequest.InputValueId;
                    BackpressureHelper.ClearInlineInputAfterCanonicalAdmission(entry, kernelState);
                }
                parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;
                state.ChildToParentKey[entry.StepId] = parentKey;
                if (BackpressureHelper.TryAdmit(state.Backpressure, entry))
                {
                    immediateDispatches.Add(entry);
                }
                else if (backpressureApplied == null)
                {
                    backpressureApplied = new BackpressureAppliedEvent
                    {
                        StepId = evt.StepId,
                        RunId = runId,
                        QueuedCount = BackpressureHelper.QueuedCount(state.Backpressure),
                        ActiveCount = state.Backpressure.ActiveWorkers,
                        MaxConcurrent = state.Backpressure.MaxConcurrentWorkers,
                    };
                }
            }
            var topUpEntries = BackpressureHelper.TopUpToTarget(state.Backpressure);
            // Always save after loop — TryAdmit mutates ActiveWorkers even when no items are queued
            if (WorkflowExecutionValueStore.IsNormalized(kernelState))
            {
                await WorkflowExecutionStateAccess.SaveAsync(
                    ctx,
                    WorkflowExecutionKernel.ModuleStateKey,
                    kernelState,
                    ct);
            }
            await SaveStateAsync(state, ctx, ct);
            if (backpressureApplied != null)
                await ctx.PublishAsync(backpressureApplied, TopologyAudience.Self, ct);
            foreach (var entry in immediateDispatches)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(entry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
            foreach (var topUpEntry in topUpEntries)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(topUpEntry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
        }
        else
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var eventRunId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowExecutionStateAccess.Load<ParallelFanOutModuleState>(ctx, ModuleStateKey);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);

            // Vote result: map back to parent parallel step.
            if (state.VoteStepToParent.TryGetValue(evt.StepId, out var voteParentKey))
            {
                if (!state.Parents.TryGetValue(voteParentKey, out var voteParentState) ||
                    !MatchesExpectedChild(voteParentState, evt, normalizedValues))
                    return;
                var workersSuccess = state.ParentWorkerSuccess.TryGetValue(voteParentKey, out var successFromWorkers) &&
                                     successFromWorkers;
                if (!normalizedValues)
                {
                    var final = new StepCompletedEvent
                    {
                        StepId = voteParentState.ParentStepId,
                        RunId = voteParentState.ParentRunId,
                        ExecutionId = voteParentState.ParentExecutionId,
                        Success = workersSuccess && evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        WorkerId = evt.WorkerId,
                        BranchKey = evt.BranchKey,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    };
                    if (evt.VoteAgreementDecision != null)
                        final.VoteAgreementDecision = evt.VoteAgreementDecision.Clone();
                    foreach (var (key, value) in evt.Annotations)
                        final.Annotations[key] = value;
                    final.Annotations["parallel.used_vote"] = "true";
                    final.Annotations["parallel.vote_step_id"] = evt.StepId;
                    final.Annotations["parallel.workers_success"] = workersSuccess.ToString();

                    state.VoteStepToParent.Remove(evt.StepId);
                    state.ParentWorkerSuccess.Remove(voteParentKey);
                    state.Parents.Remove(voteParentKey);
                    foreach (var childStepId in voteParentState.ChildExecutionIds.Keys)
                        state.ChildToParentKey.Remove(childStepId);
                    await SaveStateAsync(state, ctx, ct);
                    await ctx.PublishAsync(final, TopologyAudience.Self, ct);
                    return;
                }

                if (voteParentState.PendingCompletion == null)
                {
                    voteParentState.PendingCompletion = new StepCompletedEvent
                    {
                        StepId = voteParentState.ParentStepId,
                        RunId = voteParentState.ParentRunId,
                        ExecutionId = voteParentState.ParentExecutionId,
                        Success = workersSuccess && evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        WorkerId = evt.WorkerId,
                        BranchKey = evt.BranchKey,
                        OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                        OutputSourceStepId = evt.StepId,
                        OutputSourceExecutionId = evt.ExecutionId,
                    };
                    if (evt.VoteAgreementDecision != null)
                        voteParentState.PendingCompletion.VoteAgreementDecision = evt.VoteAgreementDecision.Clone();
                    foreach (var (key, value) in evt.Annotations)
                        voteParentState.PendingCompletion.Annotations[key] = value;
                    voteParentState.PendingCompletion.Annotations["parallel.used_vote"] = "true";
                    voteParentState.PendingCompletion.Annotations["parallel.vote_step_id"] = evt.StepId;
                    voteParentState.PendingCompletion.Annotations["parallel.workers_success"] = workersSuccess.ToString();
                    if (normalizedValues)
                    {
                        await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                            voteParentState.PendingCompletion,
                            evt,
                            ctx,
                            ct);
                    }
                    state.Parents[voteParentKey] = voteParentState;
                    await SaveStateAsync(state, ctx, ct);
                }

                await PublishPendingCompletionAsync(
                    state,
                    voteParentKey,
                    voteParentState,
                    evt,
                    evt.StepId,
                    ctx,
                    ct);
                return;
            }

            var legacyParentStepId = evt.StepId.LastIndexOf("_sub_", StringComparison.Ordinal) is var idx and > 0
                ? evt.StepId[..idx]
                : null;
            var parentKey = state.ChildToParentKey.TryGetValue(evt.StepId, out var mappedParentKey)
                ? mappedParentKey
                : legacyParentStepId;
            if (parentKey == null || !state.Parents.TryGetValue(parentKey, out var parentState)) return;
            if (!MatchesExpectedChild(parentState, evt, normalizedValues)) return;
            if (parentState.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(state, parentKey, parentState, evt, null, ctx, ct);
                return;
            }
            // Deduplicate: ignore if this sub-step was already collected
            if (parentState.CollectedStepIds.Contains(evt.StepId))
                return;
            var outputValueId = normalizedValues
                ? WorkflowExecutionValueStore.GetCompletedStepOutputValueId(kernelState, evt.StepId)
                : null;
            if (normalizedValues && string.IsNullOrWhiteSpace(outputValueId))
                return;
            parentState.CollectedStepIds.Add(evt.StepId);
            var itemResult = evt.ToParallelItemResult();
            itemResult.OutputValueId = outputValueId ?? string.Empty;
            if (normalizedValues)
                itemResult.Output = string.Empty;
            parentState.Collected.Add(itemResult);
            state.Parents[parentKey] = parentState;
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                BackpressureHelper.DefaultMaxConcurrentWorkers);
            var drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);
            ctx.Logger.LogInformation("ParallelFanOut: collected {StepId} ({Count}/{Expected})",
                evt.StepId, parentState.Collected.Count, parentState.Expected);
            if (parentState.Collected.Count >= parentState.Expected)
            {
                var results = OrderCollectedResults(parentState);
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join(
                    "\n---\n",
                    results.Select(result => normalizedValues && !string.IsNullOrWhiteSpace(result.OutputValueId)
                        ? WorkflowExecutionValueStore.ResolveCompletedStepOutput(
                            kernelState,
                            result.StepId)
                        : result.Output));
                ctx.Logger.LogInformation("ParallelFanOut: step={StepId} all {Count} workers done, merged=({Len} chars)",
                    parentState.ParentStepId, results.Count, merged.Length);

                if (!string.IsNullOrWhiteSpace(parentState.VoteConfig.StepType))
                {
                    var voteStepId = BuildVoteStepId(parentState, normalizedValues);

                    var voteReq = new StepRequestEvent
                    {
                        StepId = voteStepId,
                        StepType = parentState.VoteConfig.StepType,
                        RunId = eventRunId,
                        Input = merged,
                        StepParameters = new WorkflowStepParameters(),
                        ExecutionId = WorkflowExecutionValueStore.BuildInternalExecutionId(
                            eventRunId,
                            parentState.ParentExecutionId,
                            voteStepId),
                    };
                    parentState.ChildExecutionIds[voteStepId] = voteReq.ExecutionId;
                    state.ChildToParentKey[voteStepId] = parentKey;
                    state.VoteStepToParent[voteStepId] = parentKey;
                    state.ParentWorkerSuccess[parentKey] = allSuccess;
                    state.Parents[parentKey] = parentState;
                    await SaveStateAsync(state, ctx, ct);

                    foreach (var (key, value) in parentState.VoteConfig.Parameters)
                        voteReq.Parameters[key] = value;
                    var voteResults = results.Select(result =>
                    {
                        var clone = result.Clone();
                        if (normalizedValues && !string.IsNullOrWhiteSpace(clone.OutputValueId))
                        {
                            clone.Output = WorkflowExecutionValueStore.ResolveCompletedStepOutput(
                                kernelState,
                                clone.StepId);
                        }

                        return clone;
                    });
                    voteReq.StepParameters.VoteAgreementCandidates = BuildVoteAgreementCandidateSet(voteResults);
                    var voteRule = parentState.VoteConfig.VoteRule;
                    if (voteRule is { Mode: not AgreementRuleMode.Unspecified })
                        voteReq.StepParameters.VoteAgreementRule = voteRule.Clone();

                    ctx.Logger.LogInformation(
                        "ParallelFanOut: step={StepId} dispatch vote step={VoteStepId} type={VoteType}",
                        parentState.ParentStepId, voteStepId, parentState.VoteConfig.StepType);

                    await ctx.PublishAsync(voteReq, TopologyAudience.Self, ct);
                }
                else
                {
                    var parentCompletion = new StepCompletedEvent
                    {
                        StepId = parentState.ParentStepId,
                        RunId = parentState.ParentRunId,
                        ExecutionId = parentState.ParentExecutionId,
                        Success = allSuccess,
                        Output = merged,
                        OutputProvenance = parentState.Expected == 1
                            ? WorkflowStepOutputProvenance.ReferencedStepOutput
                            : WorkflowStepOutputProvenance.Produced,
                    };
                    parentCompletion.Annotations["parallel.used_vote"] = "false";
                    if (!normalizedValues)
                    {
                        parentCompletion.OutputProvenance = WorkflowStepOutputProvenance.Produced;
                        state.Parents.Remove(parentKey);
                        state.ParentWorkerSuccess.Remove(parentKey);
                        foreach (var childStepId in parentState.ChildExecutionIds.Keys)
                            state.ChildToParentKey.Remove(childStepId);
                        await SaveStateAsync(state, ctx, ct);
                        await ctx.PublishAsync(parentCompletion, TopologyAudience.Self, ct);
                    }
                    else
                    {
                        parentState.PendingCompletion = parentCompletion;
                        if (parentCompletion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput)
                        {
                            await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                                parentCompletion,
                                evt,
                                ctx,
                                ct);
                        }
                        state.Parents[parentKey] = parentState;
                        await SaveStateAsync(state, ctx, ct);
                        await PublishPendingCompletionAsync(state, parentKey, parentState, evt, null, ctx, ct);
                    }
                }
            }
            else
            {
                await SaveStateAsync(state, ctx, ct);
            }

            foreach (var drainedEntry in drained)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(drainedEntry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
        }
    }

    private static string BuildParentAttemptKey(
        string runId,
        string stepId,
        string executionId,
        bool normalized) =>
        normalized
            ? $"{runId}:{stepId}:execution:{(executionId ?? string.Empty).Trim()}"
            : stepId;

    private static string BuildChildStepId(
        string parentStepId,
        int index,
        string parentExecutionId,
        bool normalized)
    {
        if (!normalized || string.IsNullOrWhiteSpace(parentExecutionId))
            return $"{parentStepId}_sub_{index}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parentStepId}_execution_{hash}_sub_{index}";
    }

    private static string BuildVoteStepId(ParallelParentState parent, bool normalized)
    {
        if (!normalized || string.IsNullOrWhiteSpace(parent.ParentExecutionId))
            return $"{parent.ParentStepId}_vote";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parent.ParentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parent.ParentStepId}_execution_{hash}_vote";
    }

    private static bool MatchesExpectedChild(
        ParallelParentState parent,
        StepCompletedEvent completion,
        bool normalized)
    {
        if (!string.Equals(
                WorkflowRunIdNormalizer.Normalize(parent.ParentRunId),
                WorkflowRunIdNormalizer.Normalize(completion.RunId),
                StringComparison.Ordinal))
            return false;
        if (!parent.ChildExecutionIds.TryGetValue(completion.StepId, out var expected))
            return !normalized;
        return !normalized || string.Equals(expected, completion.ExecutionId, StringComparison.Ordinal);
    }

    private static void RemoveSupersededParentAttempts(
        ParallelFanOutModuleState state,
        string runId,
        string stepId,
        string currentKey)
    {
        foreach (var (key, parent) in state.Parents.ToArray())
        {
            if (string.Equals(key, currentKey, StringComparison.Ordinal) ||
                !string.Equals(parent.ParentRunId, runId, StringComparison.Ordinal) ||
                !string.Equals(parent.ParentStepId, stepId, StringComparison.Ordinal))
                continue;
            state.Parents.Remove(key);
            state.ParentWorkerSuccess.Remove(key);
            foreach (var child in parent.ChildExecutionIds.Keys)
            {
                state.ChildToParentKey.Remove(child);
                state.VoteStepToParent.Remove(child);
            }
        }
    }

    private static async Task PublishPendingCompletionAsync(
        ParallelFanOutModuleState state,
        string parentKey,
        ParallelParentState parent,
        StepCompletedEvent source,
        string? voteStepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = parent.PendingCompletion
            ?? throw new InvalidOperationException($"Parallel parent '{parentKey}' has no pending completion.");
        if (completion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput &&
            string.IsNullOrWhiteSpace(completion.OutputReferenceId))
        {
            await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(completion, source, ctx, ct);
            state.Parents[parentKey] = parent;
            await SaveStateAsync(state, ctx, ct);
        }

        await ctx.PublishAsync(
            WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completion, ctx),
            TopologyAudience.Self,
            ct);
        state.Parents.Remove(parentKey);
        state.ParentWorkerSuccess.Remove(parentKey);
        if (!string.IsNullOrWhiteSpace(voteStepId))
            state.VoteStepToParent.Remove(voteStepId);
        foreach (var childStepId in parent.ChildExecutionIds.Keys)
            state.ChildToParentKey.Remove(childStepId);
        await SaveStateAsync(state, ctx, CancellationToken.None);
    }

    private static Task SaveStateAsync(
        ParallelFanOutModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0 &&
            state.VoteStepToParent.Count == 0 &&
            state.ChildToParentKey.Count == 0 &&
            state.ParentWorkerSuccess.Count == 0)
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private Dictionary<string, string> ResolveSubStepParameters(
        IReadOnlyDictionary<string, string> parameters,
        string input,
        string? worker,
        int index)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = input,
            ["index"] = index.ToString(CultureInfo.InvariantCulture),
            ["worker"] = worker ?? string.Empty,
        };
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                resolved[key["sub_param_".Length..]] = _expressionEvaluator.Evaluate(value, variables);
        }

        return resolved;
    }

    private static IReadOnlyList<ParallelItemResult> OrderCollectedResults(ParallelParentState parentState) =>
        parentState.Collected
            .Select((result, position) => new
            {
                Result = result,
                Position = position,
                WorkerIndex = position < parentState.CollectedStepIds.Count
                    ? ExtractSubStepIndex(parentState.CollectedStepIds[position])
                    : int.MaxValue,
            })
            .OrderBy(static item => item.WorkerIndex)
            .ThenBy(static item => item.Position)
            .Select(static item => item.Result)
            .ToArray();

    private static int ExtractSubStepIndex(string stepId)
    {
        var markerIndex = stepId.LastIndexOf("_sub_", StringComparison.Ordinal);
        return markerIndex > 0 &&
               int.TryParse(
                   stepId[(markerIndex + "_sub_".Length)..],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var index)
            ? index
            : int.MaxValue;
    }

    private static VoteAgreementCandidateSet BuildVoteAgreementCandidateSet(
        IEnumerable<ParallelItemResult> results)
    {
        var set = new VoteAgreementCandidateSet();
        var index = 0;
        foreach (var result in results)
        {
            var candidate = new VoteAgreementCandidate
            {
                CandidateId = string.IsNullOrWhiteSpace(result.WorkerId)
                    ? $"candidate-{index}"
                    : result.WorkerId,
                Success = result.Success,
                Output = result.Output,
                Error = result.Error,
                WorkerId = result.WorkerId,
                BranchKey = result.BranchKey,
                NextStepId = result.NextStepId,
                AssignedVariable = result.AssignedVariable,
                AssignedValue = result.AssignedValue,
            };
            foreach (var (key, value) in result.Annotations)
                candidate.Annotations[key] = value;

            set.Candidates.Add(candidate);
            index++;
        }

        return set;
    }
}
