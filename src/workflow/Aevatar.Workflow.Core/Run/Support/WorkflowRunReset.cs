namespace Aevatar.Workflow.Core;

internal static class WorkflowRunReset
{
    public static void ResetRuntimeState(WorkflowRunState state, bool clearChildActors)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.RunId = string.Empty;
        state.Status = string.Empty;
        state.ActiveStepId = string.Empty;
        state.FinalOutput = string.Empty;
        state.FinalError = string.Empty;
        state.Variables.Clear();
        state.StepExecutions.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.PendingTimeouts.Clear();
        state.PendingRetryBackoffs.Clear();
        state.PendingDelays.Clear();
        state.PendingSignalWaits.Clear();
        state.PendingHumanGates.Clear();
        state.PendingLlmCalls.Clear();
        state.PendingEvaluations.Clear();
        state.PendingReflections.Clear();
        state.PendingParallelSteps.Clear();
        state.PendingForeachSteps.Clear();
        state.PendingMapReduceSteps.Clear();
        state.PendingRaceSteps.Clear();
        state.PendingWhileSteps.Clear();
        state.PendingSubWorkflows.Clear();
        state.CacheEntries.Clear();
        state.PendingCacheCalls.Clear();
        if (clearChildActors)
            state.ChildActorIds.Clear();
    }
}
