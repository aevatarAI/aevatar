namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Shared admission control logic for parallel execution modules (ParallelFanOut, ForEach, MapReduce).
/// All mutable state lives in BackpressureQueueState (proto), not in this class.
/// </summary>
internal static class BackpressureHelper
{
    public const int DefaultMaxConcurrentWorkers = 20;

    /// <summary>Reads max_concurrent_workers from step parameters, clamped to [1, fallback].</summary>
    public static int ResolveMaxConcurrent(IDictionary<string, string>? parameters, int fallback = DefaultMaxConcurrentWorkers)
    {
        if (parameters != null &&
            parameters.TryGetValue("max_concurrent_workers", out var raw) &&
            int.TryParse(raw, out var parsed) &&
            parsed > 0)
        {
            return Math.Min(parsed, fallback);
        }

        return fallback;
    }

    /// <summary>
    /// Attempts to admit a worker for dispatch.
    /// Returns true if under the concurrency limit (caller should dispatch immediately).
    /// Returns false if at limit (entry has been queued for later dispatch).
    /// </summary>
    public static bool TryAdmit(BackpressureQueueState bp, BackpressureQueueEntry entry)
    {
        if (bp.ActiveWorkers < bp.MaxConcurrentWorkers)
        {
            bp.ActiveWorkers++;
            return true;
        }

        bp.Queue.Add(entry);
        return false;
    }

    /// <summary>
    /// Called when a worker completes. Decrements active count and dequeues next if available.
    /// Returns the next entry to dispatch, or null if the queue is empty.
    /// </summary>
    public static BackpressureQueueEntry? TryDrainOne(BackpressureQueueState bp)
    {
        bp.ActiveWorkers = Math.Max(0, bp.ActiveWorkers - 1);

        if (bp.Queue.Count == 0)
            return null;

        var next = bp.Queue[0];
        bp.Queue.RemoveAt(0);
        bp.ActiveWorkers++;
        return next;
    }

    /// <summary>Converts a queued entry back to a StepRequestEvent for dispatch.</summary>
    public static StepRequestEvent ToStepRequest(BackpressureQueueEntry entry) =>
        new()
        {
            StepId = entry.StepId,
            StepType = entry.StepType,
            RunId = entry.RunId,
            Input = entry.Input,
            TargetRole = entry.TargetRole,
            Parameters = { entry.Parameters },
        };

    /// <summary>Creates a queue entry from step request fields.</summary>
    public static BackpressureQueueEntry ToQueueEntry(
        string stepId, string stepType, string runId, string input,
        string targetRole, IDictionary<string, string>? parameters) =>
        new()
        {
            StepId = stepId,
            StepType = stepType,
            RunId = runId,
            Input = input,
            TargetRole = targetRole,
            Parameters = { parameters ?? new Dictionary<string, string>() },
        };

    /// <summary>Initializes backpressure state with the resolved max concurrency.</summary>
    public static BackpressureQueueState Initialize(int maxConcurrent) =>
        new() { MaxConcurrentWorkers = maxConcurrent };

    /// <summary>
    /// Ensures a usable backpressure state exists. Proto message fields may be null on older
    /// persisted state or on paths that complete before the admission path initialized them.
    /// </summary>
    public static BackpressureQueueState EnsureInitialized(BackpressureQueueState? current, int maxConcurrent) =>
        current != null && current.MaxConcurrentWorkers > 0
            ? current
            : Initialize(maxConcurrent);
}
