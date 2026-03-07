namespace Aevatar.Workflow.Core;

internal static class WorkflowPendingTokenLookup
{
    public static bool TryResolvePendingSignalWait(
        WorkflowRunState state,
        string? waitToken,
        out WorkflowPendingSignalWaitState pending)
    {
        pending = default!;
        var normalizedToken = (waitToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in state.PendingSignalWaits.Values)
        {
            if (!string.Equals(candidate.WaitToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }

    public static bool TryResolvePendingHumanGate(
        WorkflowRunState state,
        string? resumeToken,
        out WorkflowPendingHumanGateState pending)
    {
        pending = default!;
        var normalizedToken = (resumeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in state.PendingHumanGates.Values)
        {
            if (!string.Equals(candidate.ResumeToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }
}
