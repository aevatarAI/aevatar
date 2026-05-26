namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Shared admission control logic for parallel execution modules (ParallelFanOut, ForEach, MapReduce).
/// All mutable state lives in BackpressureQueueState (proto), not in this class.
/// Refactor (iter11/cluster-021): Old drain removed Queue[0] and shifted the protobuf repeated field.
/// Refactor (iter11/cluster-021): New drain advances the persisted HeadIndex cursor and compacts occasionally.
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

        NormalizeHeadIndex(bp);

        if (QueuedCount(bp) == 0)
            return null;

        var next = bp.Queue[bp.HeadIndex];
        bp.HeadIndex++;
        bp.ActiveWorkers++;
        CompactIfNeeded(bp);
        return next;
    }

    public static int QueuedCount(BackpressureQueueState bp)
    {
        NormalizeHeadIndex(bp);
        return bp.Queue.Count - bp.HeadIndex;
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

    private static void NormalizeHeadIndex(BackpressureQueueState bp)
    {
        if (bp.HeadIndex < 0 || bp.HeadIndex > bp.Queue.Count)
            bp.HeadIndex = 0;
    }

    private static void CompactIfNeeded(BackpressureQueueState bp)
    {
        if (bp.HeadIndex <= 0)
            return;

        if (bp.HeadIndex <= bp.Queue.Count / 2)
            return;

        var consumed = bp.HeadIndex;
        var remaining = bp.Queue.Count - consumed;
        for (var i = 0; i < remaining; i++)
            bp.Queue[i] = bp.Queue[consumed + i];

        while (bp.Queue.Count > remaining)
            bp.Queue.RemoveAt(bp.Queue.Count - 1);

        bp.HeadIndex = 0;
    }
}
