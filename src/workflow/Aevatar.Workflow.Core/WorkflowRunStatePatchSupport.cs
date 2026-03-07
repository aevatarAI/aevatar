using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunStatePatchSupport
{
    public static WorkflowRunStatePatchedEvent? BuildPatch(WorkflowRunState current, WorkflowRunState next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        var patch = new WorkflowRunStatePatchedEvent();
        var changed = false;

        if (!string.Equals(current.WorkflowName, next.WorkflowName, StringComparison.Ordinal) ||
            !string.Equals(current.WorkflowYaml, next.WorkflowYaml, StringComparison.Ordinal) ||
            current.Compiled != next.Compiled ||
            !string.Equals(current.CompilationError, next.CompilationError, StringComparison.Ordinal) ||
            !MapEquals(current.InlineWorkflowYamls, next.InlineWorkflowYamls))
        {
            patch.Binding = new WorkflowRunBindingFacts
            {
                WorkflowName = next.WorkflowName ?? string.Empty,
                WorkflowYaml = next.WorkflowYaml ?? string.Empty,
                Compiled = next.Compiled,
                CompilationError = next.CompilationError ?? string.Empty,
            };
            CopyMap(next.InlineWorkflowYamls, patch.Binding.InlineWorkflowYamls);
            changed = true;
        }

        if (!string.Equals(current.RunId, next.RunId, StringComparison.Ordinal) ||
            !string.Equals(current.Status, next.Status, StringComparison.Ordinal) ||
            !string.Equals(current.ActiveStepId, next.ActiveStepId, StringComparison.Ordinal) ||
            !string.Equals(current.FinalOutput, next.FinalOutput, StringComparison.Ordinal) ||
            !string.Equals(current.FinalError, next.FinalError, StringComparison.Ordinal))
        {
            patch.Lifecycle = new WorkflowRunLifecycleFacts
            {
                RunId = next.RunId ?? string.Empty,
                Status = next.Status ?? string.Empty,
                ActiveStepId = next.ActiveStepId ?? string.Empty,
                FinalOutput = next.FinalOutput ?? string.Empty,
                FinalError = next.FinalError ?? string.Empty,
            };
            changed = true;
        }

        changed |= AssignMapSliceIfChanged(current.Variables, next.Variables, patch.Variables, value => patch.Variables = value, CreateStringFacts);
        changed |= AssignMapSliceIfChanged(current.StepExecutions, next.StepExecutions, patch.StepExecutions, value => patch.StepExecutions = value, CreateStepExecutionFacts);
        changed |= AssignMapSliceIfChanged(current.RetryAttemptsByStepId, next.RetryAttemptsByStepId, patch.RetryAttemptsByStepId, value => patch.RetryAttemptsByStepId = value, CreateRetryFacts);
        changed |= AssignMapSliceIfChanged(current.PendingTimeouts, next.PendingTimeouts, patch.PendingTimeouts, value => patch.PendingTimeouts = value, CreatePendingTimeoutFacts);
        changed |= AssignMapSliceIfChanged(current.PendingRetryBackoffs, next.PendingRetryBackoffs, patch.PendingRetryBackoffs, value => patch.PendingRetryBackoffs = value, CreatePendingRetryBackoffFacts);
        changed |= AssignMapSliceIfChanged(current.PendingDelays, next.PendingDelays, patch.PendingDelays, value => patch.PendingDelays = value, CreatePendingDelayFacts);
        changed |= AssignMapSliceIfChanged(current.PendingSignalWaits, next.PendingSignalWaits, patch.PendingSignalWaits, value => patch.PendingSignalWaits = value, CreatePendingSignalWaitFacts);
        changed |= AssignMapSliceIfChanged(current.PendingHumanGates, next.PendingHumanGates, patch.PendingHumanGates, value => patch.PendingHumanGates = value, CreatePendingHumanGateFacts);
        changed |= AssignMapSliceIfChanged(current.PendingLlmCalls, next.PendingLlmCalls, patch.PendingLlmCalls, value => patch.PendingLlmCalls = value, CreatePendingLlmCallFacts);
        changed |= AssignMapSliceIfChanged(current.PendingEvaluations, next.PendingEvaluations, patch.PendingEvaluations, value => patch.PendingEvaluations = value, CreatePendingEvaluateFacts);
        changed |= AssignMapSliceIfChanged(current.PendingReflections, next.PendingReflections, patch.PendingReflections, value => patch.PendingReflections = value, CreatePendingReflectFacts);
        changed |= AssignMapSliceIfChanged(current.PendingParallelSteps, next.PendingParallelSteps, patch.PendingParallelSteps, value => patch.PendingParallelSteps = value, CreateParallelFacts);
        changed |= AssignMapSliceIfChanged(current.PendingForeachSteps, next.PendingForeachSteps, patch.PendingForeachSteps, value => patch.PendingForeachSteps = value, CreateForEachFacts);
        changed |= AssignMapSliceIfChanged(current.PendingMapReduceSteps, next.PendingMapReduceSteps, patch.PendingMapReduceSteps, value => patch.PendingMapReduceSteps = value, CreateMapReduceFacts);
        changed |= AssignMapSliceIfChanged(current.PendingRaceSteps, next.PendingRaceSteps, patch.PendingRaceSteps, value => patch.PendingRaceSteps = value, CreateRaceFacts);
        changed |= AssignMapSliceIfChanged(current.PendingWhileSteps, next.PendingWhileSteps, patch.PendingWhileSteps, value => patch.PendingWhileSteps = value, CreateWhileFacts);
        changed |= AssignMapSliceIfChanged(current.PendingSubWorkflows, next.PendingSubWorkflows, patch.PendingSubWorkflows, value => patch.PendingSubWorkflows = value, CreatePendingSubWorkflowFacts);
        changed |= AssignMapSliceIfChanged(current.CacheEntries, next.CacheEntries, patch.CacheEntries, value => patch.CacheEntries = value, CreateCacheEntryFacts);
        changed |= AssignMapSliceIfChanged(current.PendingCacheCalls, next.PendingCacheCalls, patch.PendingCacheCalls, value => patch.PendingCacheCalls = value, CreatePendingCacheFacts);
        changed |= AssignRepeatedSliceIfChanged(current.ChildActorIds, next.ChildActorIds, patch.ChildActorIds, value => patch.ChildActorIds = value);
        changed |= AssignMapSliceIfChanged(current.SubWorkflowBindings, next.SubWorkflowBindings, patch.SubWorkflowBindings, value => patch.SubWorkflowBindings = value, CreateSubWorkflowBindingFacts);
        changed |= AssignMapSliceIfChanged(
            current.PendingChildRunIdsByParentRunId,
            next.PendingChildRunIdsByParentRunId,
            patch.PendingChildRunIdsByParentRunId,
            value => patch.PendingChildRunIdsByParentRunId = value,
            CreatePendingChildRunIdsFacts);

        return changed ? patch : null;
    }

    public static WorkflowRunState ApplyPatch(WorkflowRunState current, WorkflowRunStatePatchedEvent patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);

        var next = current.Clone();

        if (patch.Binding != null)
        {
            next.WorkflowName = patch.Binding.WorkflowName;
            next.WorkflowYaml = patch.Binding.WorkflowYaml;
            next.Compiled = patch.Binding.Compiled;
            next.CompilationError = patch.Binding.CompilationError;
            ReplaceMap(next.InlineWorkflowYamls, patch.Binding.InlineWorkflowYamls);
        }

        if (patch.Lifecycle != null)
        {
            next.RunId = patch.Lifecycle.RunId;
            next.Status = patch.Lifecycle.Status;
            next.ActiveStepId = patch.Lifecycle.ActiveStepId;
            next.FinalOutput = patch.Lifecycle.FinalOutput;
            next.FinalError = patch.Lifecycle.FinalError;
        }

        ReplaceMapIfPresent(next.Variables, patch.Variables?.Entries);
        ReplaceMapIfPresent(next.StepExecutions, patch.StepExecutions?.Entries);
        ReplaceMapIfPresent(next.RetryAttemptsByStepId, patch.RetryAttemptsByStepId?.Entries);
        ReplaceMapIfPresent(next.PendingTimeouts, patch.PendingTimeouts?.Entries);
        ReplaceMapIfPresent(next.PendingRetryBackoffs, patch.PendingRetryBackoffs?.Entries);
        ReplaceMapIfPresent(next.PendingDelays, patch.PendingDelays?.Entries);
        ReplaceMapIfPresent(next.PendingSignalWaits, patch.PendingSignalWaits?.Entries);
        ReplaceMapIfPresent(next.PendingHumanGates, patch.PendingHumanGates?.Entries);
        ReplaceMapIfPresent(next.PendingLlmCalls, patch.PendingLlmCalls?.Entries);
        ReplaceMapIfPresent(next.PendingEvaluations, patch.PendingEvaluations?.Entries);
        ReplaceMapIfPresent(next.PendingReflections, patch.PendingReflections?.Entries);
        ReplaceMapIfPresent(next.PendingParallelSteps, patch.PendingParallelSteps?.Entries);
        ReplaceMapIfPresent(next.PendingForeachSteps, patch.PendingForeachSteps?.Entries);
        ReplaceMapIfPresent(next.PendingMapReduceSteps, patch.PendingMapReduceSteps?.Entries);
        ReplaceMapIfPresent(next.PendingRaceSteps, patch.PendingRaceSteps?.Entries);
        ReplaceMapIfPresent(next.PendingWhileSteps, patch.PendingWhileSteps?.Entries);
        ReplaceMapIfPresent(next.PendingSubWorkflows, patch.PendingSubWorkflows?.Entries);
        ReplaceMapIfPresent(next.CacheEntries, patch.CacheEntries?.Entries);
        ReplaceMapIfPresent(next.PendingCacheCalls, patch.PendingCacheCalls?.Entries);
        ReplaceRepeatedIfPresent(next.ChildActorIds, patch.ChildActorIds?.ActorIds);
        ReplaceMapIfPresent(next.SubWorkflowBindings, patch.SubWorkflowBindings?.Entries);
        ReplaceMapIfPresent(next.PendingChildRunIdsByParentRunId, patch.PendingChildRunIdsByParentRunId?.Entries);

        return next;
    }

    private static WorkflowRunVariablesFacts CreateStringFacts(MapField<string, string> source)
    {
        var facts = new WorkflowRunVariablesFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunStepExecutionFacts CreateStepExecutionFacts(MapField<string, WorkflowStepExecutionState> source)
    {
        var facts = new WorkflowRunStepExecutionFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunRetryAttemptsFacts CreateRetryFacts(MapField<string, int> source)
    {
        var facts = new WorkflowRunRetryAttemptsFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingTimeoutFacts CreatePendingTimeoutFacts(MapField<string, WorkflowPendingTimeoutState> source)
    {
        var facts = new WorkflowRunPendingTimeoutFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingRetryBackoffFacts CreatePendingRetryBackoffFacts(MapField<string, WorkflowPendingRetryBackoffState> source)
    {
        var facts = new WorkflowRunPendingRetryBackoffFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingDelayFacts CreatePendingDelayFacts(MapField<string, WorkflowPendingDelayState> source)
    {
        var facts = new WorkflowRunPendingDelayFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingSignalWaitFacts CreatePendingSignalWaitFacts(MapField<string, WorkflowPendingSignalWaitState> source)
    {
        var facts = new WorkflowRunPendingSignalWaitFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingHumanGateFacts CreatePendingHumanGateFacts(MapField<string, WorkflowPendingHumanGateState> source)
    {
        var facts = new WorkflowRunPendingHumanGateFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingLlmCallFacts CreatePendingLlmCallFacts(MapField<string, WorkflowPendingLlmCallState> source)
    {
        var facts = new WorkflowRunPendingLlmCallFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingEvaluateFacts CreatePendingEvaluateFacts(MapField<string, WorkflowPendingEvaluateState> source)
    {
        var facts = new WorkflowRunPendingEvaluateFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingReflectFacts CreatePendingReflectFacts(MapField<string, WorkflowPendingReflectState> source)
    {
        var facts = new WorkflowRunPendingReflectFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunParallelFacts CreateParallelFacts(MapField<string, WorkflowParallelState> source)
    {
        var facts = new WorkflowRunParallelFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunForEachFacts CreateForEachFacts(MapField<string, WorkflowForEachState> source)
    {
        var facts = new WorkflowRunForEachFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunMapReduceFacts CreateMapReduceFacts(MapField<string, WorkflowMapReduceState> source)
    {
        var facts = new WorkflowRunMapReduceFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunRaceFacts CreateRaceFacts(MapField<string, WorkflowRaceState> source)
    {
        var facts = new WorkflowRunRaceFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunWhileFacts CreateWhileFacts(MapField<string, WorkflowWhileState> source)
    {
        var facts = new WorkflowRunWhileFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingSubWorkflowFacts CreatePendingSubWorkflowFacts(MapField<string, WorkflowPendingSubWorkflowState> source)
    {
        var facts = new WorkflowRunPendingSubWorkflowFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunCacheEntryFacts CreateCacheEntryFacts(MapField<string, WorkflowCacheEntry> source)
    {
        var facts = new WorkflowRunCacheEntryFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingCacheFacts CreatePendingCacheFacts(MapField<string, WorkflowPendingCacheState> source)
    {
        var facts = new WorkflowRunPendingCacheFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunSubWorkflowBindingFacts CreateSubWorkflowBindingFacts(MapField<string, WorkflowSubWorkflowBindingState> source)
    {
        var facts = new WorkflowRunSubWorkflowBindingFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static WorkflowRunPendingChildRunIdsFacts CreatePendingChildRunIdsFacts(MapField<string, WorkflowChildRunIdSet> source)
    {
        var facts = new WorkflowRunPendingChildRunIdsFacts();
        CopyMap(source, facts.Entries);
        return facts;
    }

    private static bool AssignMapSliceIfChanged<TValue, TFacts>(
        MapField<string, TValue> current,
        MapField<string, TValue> next,
        TFacts? _,
        Action<TFacts> assign,
        Func<MapField<string, TValue>, TFacts> createFacts)
        where TValue : class
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    private static bool AssignMapSliceIfChanged<TFacts>(
        MapField<string, string> current,
        MapField<string, string> next,
        TFacts? _,
        Action<TFacts> assign,
        Func<MapField<string, string>, TFacts> createFacts)
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    private static bool AssignMapSliceIfChanged<TFacts>(
        MapField<string, int> current,
        MapField<string, int> next,
        TFacts? _,
        Action<TFacts> assign,
        Func<MapField<string, int>, TFacts> createFacts)
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    private static bool AssignRepeatedSliceIfChanged(
        RepeatedField<string> current,
        RepeatedField<string> next,
        WorkflowRunChildActorIdsFacts? _,
        Action<WorkflowRunChildActorIdsFacts> assign)
    {
        if (RepeatedEquals(current, next))
            return false;

        var facts = new WorkflowRunChildActorIdsFacts();
        facts.ActorIds.Add(next);
        assign(facts);
        return true;
    }

    private static void ReplaceMapIfPresent<TValue>(MapField<string, TValue> target, MapField<string, TValue>? source)
    {
        if (source == null)
            return;

        ReplaceMap(target, source);
    }

    private static void ReplaceMap<TValue>(MapField<string, TValue> target, MapField<string, TValue> source)
    {
        target.Clear();
        CopyMap(source, target);
    }

    private static void ReplaceRepeatedIfPresent(RepeatedField<string> target, RepeatedField<string>? source)
    {
        if (source == null)
            return;

        target.Clear();
        target.Add(source);
    }

    private static void CopyMap<TValue>(MapField<string, TValue> source, MapField<string, TValue> target)
    {
        foreach (var (key, value) in source)
            target[key] = value;
    }

    private static bool MapEquals<TValue>(MapField<string, TValue> left, MapField<string, TValue> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        var comparer = EqualityComparer<TValue>.Default;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !comparer.Equals(value, other))
                return false;
        }

        return true;
    }

    private static bool RepeatedEquals(RepeatedField<string> left, RepeatedField<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
