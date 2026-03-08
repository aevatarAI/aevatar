using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Extensions.Maker.Modules;

/// <summary>
/// Recursive MAKER solver module.
/// Implements atomicity decision + recursive decomposition + per-stage voting.
/// </summary>
public sealed class MakerRecursiveModule : IEventModule<IWorkflowExecutionContext>
{
    private readonly Dictionary<StepRunKey, NodeState> _nodes = [];
    private readonly Dictionary<StepRunKey, InternalStageRef> _internalStages = [];
    private readonly Dictionary<StepRunKey, StepRunKey> _childToParent = [];

    public string Name => "maker_recursive";
    public int Priority => 3;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (!IsRecursiveStep(request.StepType)) return;
            await HandleRecursiveRequestAsync(request, ctx, ct);
            return;
        }

        var completed = payload.Unpack<StepCompletedEvent>();
        await HandleStepCompletedAsync(completed, ctx, ct);
    }

    private async Task HandleRecursiveRequestAsync(StepRequestEvent request, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var nodeKey = new StepRunKey(runId, request.StepId);
        if (_nodes.ContainsKey(nodeKey))
        {
            ctx.Logger.LogDebug(
                "maker_recursive: ignore duplicate request run={RunId} step={StepId}",
                runId,
                request.StepId);
            return;
        }

        var state = NodeState.Create(request, runId);
        _nodes[nodeKey] = state;

        ctx.Logger.LogInformation(
            "maker_recursive: start run={RunId} step={StepId} depth={Depth}/{MaxDepth}",
            state.RunId,
            state.StepId,
            state.Depth,
            state.MaxDepth);

        await DispatchAtomicVoteAsync(state, ctx, ct);
    }

    private async Task HandleStepCompletedAsync(StepCompletedEvent completed, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(completed.RunId);
        var completionKey = new StepRunKey(runId, completed.StepId);
        if (_internalStages.TryGetValue(completionKey, out var stageRef))
        {
            _internalStages.Remove(completionKey);
            if (!_nodes.TryGetValue(stageRef.NodeKey, out var node))
                return;

            switch (stageRef.Stage)
            {
                case InternalStage.AtomicVote:
                    await HandleAtomicVoteCompletedAsync(node, completed, ctx, ct);
                    return;
                case InternalStage.DecomposeVote:
                    await HandleDecomposeVoteCompletedAsync(node, completed, ctx, ct);
                    return;
                case InternalStage.LeafSolveVote:
                    await FinalizeNodeFromInternalStepAsync(node, completed, "leaf", ctx, ct);
                    return;
                case InternalStage.ComposeVote:
                    await FinalizeNodeFromInternalStepAsync(node, completed, "composed", ctx, ct);
                    return;
            }
        }

        if (_childToParent.TryGetValue(completionKey, out var parentNodeKey))
        {
            _childToParent.Remove(completionKey);
            if (!_nodes.TryGetValue(parentNodeKey, out var parent))
                return;

            parent.ChildResults[completed.StepId] = completed;
            ctx.Logger.LogInformation(
                "maker_recursive: child done parent={Parent} child={Child} ({Done}/{Expected})",
                parent.StepId,
                completed.StepId,
                parent.ChildResults.Count,
                parent.ChildStepIds.Count);

            if (parent.ChildResults.Count < parent.ChildStepIds.Count)
                return;

            await HandleAllChildrenDoneAsync(parent, ctx, ct);
        }
    }

    private async Task HandleAtomicVoteCompletedAsync(
        NodeState node,
        StepCompletedEvent atomicVoteResult,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!atomicVoteResult.Success)
        {
            await FailNodeAsync(node, $"atomic vote failed: {atomicVoteResult.Error}", ctx, ct);
            return;
        }

        var atomicByVote = ParseAtomicDecision(atomicVoteResult.Output);
        var atomicByDepth = node.Depth >= node.MaxDepth;
        node.AtomicDecision = atomicByVote || atomicByDepth;

        if (node.AtomicDecision)
        {
            await DispatchLeafSolveVoteAsync(node, ctx, ct);
            return;
        }

        await DispatchDecomposeVoteAsync(node, ctx, ct);
    }

    private async Task HandleDecomposeVoteCompletedAsync(
        NodeState node,
        StepCompletedEvent decomposeVoteResult,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!decomposeVoteResult.Success)
        {
            await FailNodeAsync(node, $"decompose vote failed: {decomposeVoteResult.Error}", ctx, ct);
            return;
        }

        var subtasks = ParseSubtasks(decomposeVoteResult.Output, node.Delimiter, node.MaxSubtasks);
        var noEffectiveSplit = subtasks.Count <= 1 || IsEquivalentTask(subtasks[0], node.OriginalTask);

        if (noEffectiveSplit)
        {
            ctx.Logger.LogInformation(
                "maker_recursive: fallback leaf parent={StepId} reason={Reason}",
                node.StepId,
                subtasks.Count == 0 ? "no_subtask" : "not_effective_split");
            await DispatchLeafSolveVoteAsync(node, ctx, ct);
            return;
        }

        node.ChildStepIds.Clear();
        node.ChildResults.Clear();

        for (var i = 0; i < subtasks.Count; i++)
        {
            var childStepId = $"{node.StepId}_child_{i}";
            node.ChildStepIds.Add(childStepId);
            _childToParent[new StepRunKey(node.RunId, childStepId)] = node.Key;

            var childRequest = new StepRequestEvent
            {
                StepId = childStepId,
                StepType = "maker_recursive",
                Input = subtasks[i],
                RunId = node.RunId,
            };
            foreach (var (key, value) in node.OriginalParameters)
                childRequest.Parameters[key] = value;
            childRequest.Parameters["depth"] = (node.Depth + 1).ToString();

            await ctx.PublishAsync(childRequest, EventDirection.Self, ct);
        }

        ctx.Logger.LogInformation(
            "maker_recursive: decomposed step={StepId} into {Count} children",
            node.StepId,
            node.ChildStepIds.Count);
    }

    private async Task HandleAllChildrenDoneAsync(NodeState node, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var orderedChildResults = node.ChildStepIds
            .Where(node.ChildResults.ContainsKey)
            .Select(stepId => node.ChildResults[stepId])
            .ToList();

        var failed = orderedChildResults.FirstOrDefault(x => !x.Success);
        if (failed != null)
        {
            await FailNodeAsync(node, $"child step failed: {failed.StepId} - {failed.Error}", ctx, ct);
            return;
        }

        var childOutputs = orderedChildResults.Select(x => x.Output).ToList();
        var composeInput = BuildComposePrompt(node, string.Join(node.Delimiter, childOutputs));
        var composeStepId = $"{node.StepId}_compose_vote";
        await DispatchParallelVoteStepAsync(
            node,
            composeStepId,
            composeInput,
            node.ComposeWorkers,
            InternalStage.ComposeVote,
            ctx,
            ct);
    }

    private async Task FinalizeNodeFromInternalStepAsync(
        NodeState node,
        StepCompletedEvent stageResult,
        string stage,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completed = new StepCompletedEvent
        {
            StepId = node.StepId,
            RunId = node.RunId,
            Success = stageResult.Success,
            Output = stageResult.Output,
            Error = stageResult.Error,
            WorkerId = stageResult.WorkerId,
        };

        foreach (var (key, value) in stageResult.Metadata)
            completed.Metadata[key] = value;
        completed.Metadata["maker.recursive"] = "true";
        completed.Metadata["maker.depth"] = node.Depth.ToString();
        completed.Metadata["maker.max_depth"] = node.MaxDepth.ToString();
        completed.Metadata["maker.atomic_decision"] = node.AtomicDecision.ToString();
        completed.Metadata["maker.stage"] = stage;
        completed.Metadata["maker.child_count"] = node.ChildStepIds.Count.ToString();

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
        CleanupNode(node.Key);
    }

    private async Task FailNodeAsync(NodeState node, string error, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = node.StepId,
            RunId = node.RunId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);

        CleanupNode(node.Key);
    }

    private void CleanupNode(StepRunKey nodeKey)
    {
        _nodes.Remove(nodeKey);

        foreach (var internalKey in _internalStages
                     .Where(x => x.Value.NodeKey.Equals(nodeKey))
                     .Select(x => x.Key)
                     .ToList())
        {
            _internalStages.Remove(internalKey);
        }

        foreach (var childKey in _childToParent
                     .Where(x => x.Value.Equals(nodeKey))
                     .Select(x => x.Key)
                     .ToList())
        {
            _childToParent.Remove(childKey);
        }
    }

    private async Task DispatchAtomicVoteAsync(NodeState node, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var atomicInput = BuildAtomicPrompt(node);
        var stepId = $"{node.StepId}_atomic_vote";
        await DispatchParallelVoteStepAsync(node, stepId, atomicInput, node.AtomicWorkers, InternalStage.AtomicVote, ctx, ct);
    }

    private async Task DispatchDecomposeVoteAsync(NodeState node, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var decomposeInput = BuildDecomposePrompt(node);
        var stepId = $"{node.StepId}_decompose_vote";
        await DispatchParallelVoteStepAsync(node, stepId, decomposeInput, node.DecomposeWorkers, InternalStage.DecomposeVote, ctx, ct);
    }

    private async Task DispatchLeafSolveVoteAsync(NodeState node, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var solveInput = BuildSolvePrompt(node);
        var stepId = $"{node.StepId}_leaf_vote";
        await DispatchParallelVoteStepAsync(node, stepId, solveInput, node.SolveWorkers, InternalStage.LeafSolveVote, ctx, ct);
    }

    private async Task DispatchParallelVoteStepAsync(
        NodeState node,
        string internalStepId,
        string input,
        string workers,
        InternalStage stage,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var workerList = ParseWorkers(workers);
        var req = new StepRequestEvent
        {
            StepId = internalStepId,
            StepType = node.ParallelStepType,
            Input = input,
            RunId = node.RunId,
        };
        req.Parameters["workers"] = string.Join(",", workerList);
        req.Parameters["parallel_count"] = workerList.Count.ToString();
        req.Parameters["vote_step_type"] = node.VoteStepType;
        req.Parameters["vote_param_k"] = node.K.ToString();
        req.Parameters["vote_param_max_response_length"] = node.MaxResponseLength.ToString();

        _internalStages[new StepRunKey(node.RunId, internalStepId)] = new InternalStageRef(node.Key, stage);
        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private static bool IsRecursiveStep(string stepType) =>
        string.Equals(stepType, "maker_recursive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "maker_recursive_solve", StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(IDictionary<string, string> parameters, string key, int fallback, int min, int max)
    {
        if (!parameters.TryGetValue(key, out var raw) || !int.TryParse(raw, out var parsed))
            return fallback;
        if (parsed < min) return min;
        if (parsed > max) return max;
        return parsed;
    }

    private static string ReadOrDefault(IDictionary<string, string> parameters, string key, string fallback) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static List<string> ParseWorkers(string workersCsv)
    {
        var workers = workersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (workers.Count == 0) workers.Add("coordinator");
        return workers;
    }

    private static bool ParseAtomicDecision(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var normalized = output.Trim();
        var upper = normalized.ToUpperInvariant();

        var idxAtomic = upper.IndexOf("ATOMIC", StringComparison.Ordinal);
        var idxDecompose = upper.IndexOf("DECOMPOSE", StringComparison.Ordinal);
        if (idxAtomic >= 0 && idxDecompose < 0) return true;
        if (idxDecompose >= 0 && idxAtomic < 0) return false;
        if (idxAtomic >= 0 && idxDecompose >= 0) return idxAtomic < idxDecompose;

        if (normalized.Contains("原子", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.Contains("分解", StringComparison.OrdinalIgnoreCase)) return false;
        return false;
    }

    private static List<string> ParseSubtasks(string output, string delimiter, int maxSubtasks)
    {
        var items = output.Split(delimiter, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (items.Count <= 1)
        {
            items = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Regex.Replace(x.Trim(), @"^\s*(?:[-*]|\d+[.)])\s*", ""))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        if (items.Count > maxSubtasks)
            items = items.Take(maxSubtasks).ToList();
        return items;
    }

    private static bool IsEquivalentTask(string a, string b)
    {
        static string Normalize(string text) =>
            Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAtomicPrompt(NodeState node) =>
        $"{node.AtomicPrompt}\n\n{BuildContextBlock(node, "atomic_decision")}\n\nTASK:\n{node.OriginalTask}";

    private static string BuildDecomposePrompt(NodeState node) =>
        $"{node.DecomposePrompt}\n\n{BuildContextBlock(node, "decompose")}\n\nDELIMITER:\n{node.Delimiter}\n\nTASK:\n{node.OriginalTask}";

    private static string BuildSolvePrompt(NodeState node) =>
        $"{node.SolvePrompt}\n\n{BuildContextBlock(node, "solve")}\n\nTASK:\n{node.OriginalTask}";

    private static string BuildComposePrompt(NodeState node, string childOutputs) =>
        $"{node.ComposePrompt}\n\n{BuildContextBlock(node, "compose")}\n\nPARENT TASK:\n{node.OriginalTask}\n\nCHILD SOLUTIONS (use delimiter {node.Delimiter}):\n{childOutputs}";

    private static string BuildContextBlock(NodeState node, string phase) =>
        $"MAKER_CONTEXT:\nPHASE: {phase}\nDEPTH: {node.Depth}\nMAX_DEPTH: {node.MaxDepth}\nREMAINING_DEPTH: {Math.Max(0, node.MaxDepth - node.Depth)}\nMAX_SUBTASKS: {node.MaxSubtasks}\nVOTE_K: {node.K}\nMAX_RESPONSE_LENGTH: {node.MaxResponseLength}";

    private enum InternalStage
    {
        AtomicVote,
        DecomposeVote,
        LeafSolveVote,
        ComposeVote,
    }

    private readonly record struct StepRunKey(string RunId, string StepId);
    private sealed record InternalStageRef(StepRunKey NodeKey, InternalStage Stage);

    private sealed class NodeState
    {
        public required string RunId { get; init; }
        public required string StepId { get; init; }
        public required string OriginalTask { get; init; }
        public required Dictionary<string, string> OriginalParameters { get; init; }
        public required int Depth { get; init; }
        public required int MaxDepth { get; init; }
        public required int MaxSubtasks { get; init; }
        public required string Delimiter { get; init; }
        public required int K { get; init; }
        public required int MaxResponseLength { get; init; }
        public required string ParallelStepType { get; init; }
        public required string VoteStepType { get; init; }
        public required string AtomicWorkers { get; init; }
        public required string DecomposeWorkers { get; init; }
        public required string SolveWorkers { get; init; }
        public required string ComposeWorkers { get; init; }
        public required string AtomicPrompt { get; init; }
        public required string DecomposePrompt { get; init; }
        public required string SolvePrompt { get; init; }
        public required string ComposePrompt { get; init; }

        public bool AtomicDecision { get; set; }
        public List<string> ChildStepIds { get; } = [];
        public Dictionary<string, StepCompletedEvent> ChildResults { get; } = new(StringComparer.Ordinal);
        public StepRunKey Key => new(RunId, StepId);

        public static NodeState Create(StepRequestEvent request, string runId)
        {
            var parameters = request.Parameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            return new NodeState
            {
                RunId = runId,
                StepId = request.StepId,
                OriginalTask = request.Input,
                OriginalParameters = parameters,
                Depth = ParseInt(parameters, "depth", 0, 0, 32),
                MaxDepth = ParseInt(parameters, "max_depth", 3, 0, 32),
                MaxSubtasks = ParseInt(parameters, "max_subtasks", 4, 1, 12),
                Delimiter = ReadOrDefault(parameters, "delimiter", "\n---\n"),
                K = ParseInt(parameters, "k", 1, 1, 10),
                MaxResponseLength = ParseInt(parameters, "max_response_length", 2200, 128, 12000),
                ParallelStepType = ReadOrDefault(parameters, "parallel_step_type", "parallel"),
                VoteStepType = ReadOrDefault(parameters, "vote_step_type", "maker_vote"),
                AtomicWorkers = ReadOrDefault(parameters, "atomic_workers", "coordinator,coordinator,coordinator"),
                DecomposeWorkers = ReadOrDefault(parameters, "decompose_workers", "coordinator,coordinator,coordinator"),
                SolveWorkers = ReadOrDefault(parameters, "solve_workers", "worker_a,worker_b,worker_c"),
                ComposeWorkers = ReadOrDefault(parameters, "compose_workers", "coordinator,coordinator,coordinator"),
                AtomicPrompt = ReadOrDefault(parameters, "atomic_prompt",
                    "You are a MAKER atomicity judge. Decide whether this task is already atomic (single micro-step) or requires further decomposition. Return exactly one token: ATOMIC or DECOMPOSE."),
                DecomposePrompt = ReadOrDefault(parameters, "decompose_prompt",
                    "You are a MAKER decomposer. Break the task into 2-5 independent subtasks that are each closer to atomic. Output only subtasks separated by the delimiter."),
                SolvePrompt = ReadOrDefault(parameters, "solve_prompt",
                    "You are a MAKER worker. Solve this atomic task directly. Return only the answer."),
                ComposePrompt = ReadOrDefault(parameters, "compose_prompt",
                    "You are a MAKER composer. Merge child solutions into one coherent parent-level answer without losing key details."),
            };
        }
    }
}
