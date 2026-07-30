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
    public const int MaxConcurrentWorkersHardLimit = 200;

    /// <summary>
    /// Reads max_concurrent_workers from step parameters.
    /// Defaults to the safe baseline and allows explicit opt-in above that baseline up to a hard limit.
    /// </summary>
    public static int ResolveMaxConcurrent(
        IDictionary<string, string>? parameters,
        int fallback = DefaultMaxConcurrentWorkers,
        int hardLimit = MaxConcurrentWorkersHardLimit)
    {
        var boundedFallback = Math.Clamp(fallback, 1, Math.Max(1, hardLimit));
        if (parameters != null &&
            parameters.TryGetValue("max_concurrent_workers", out var raw) &&
            int.TryParse(raw, out var parsed) &&
            parsed > 0)
        {
            return Math.Clamp(parsed, 1, Math.Max(1, hardLimit));
        }

        return boundedFallback;
    }

    /// <summary>
    /// Reads min_concurrent_workers from step parameters and clamps it into [0, maxConcurrent].
    /// When omitted or invalid, no explicit floor is applied and the helper falls back to max concurrency behavior.
    /// </summary>
    public static int ResolveMinConcurrent(IDictionary<string, string>? parameters, int maxConcurrent)
    {
        if (maxConcurrent <= 0)
            return 0;

        if (parameters != null &&
            TryGetPositiveInt(parameters, out var parsed, "min_concurrent_workers", "min_workers", "concurrency_floor"))
        {
            return Math.Clamp(parsed, 0, maxConcurrent);
        }

        return 0;
    }

    /// <summary>
     /// Attempts to admit a worker for dispatch.
    /// Returns true if under the immediate dispatch target (caller should dispatch immediately).
    /// Returns false if at target (entry has been queued for later dispatch).
     /// </summary>
    public static bool TryAdmit(BackpressureQueueState bp, BackpressureQueueEntry entry)
    {
        if (bp.ActiveWorkers < ResolveDispatchTarget(bp))
        {
            bp.ActiveWorkers++;
            return true;
        }

        bp.Queue.Add(entry);
        return false;
    }

    /// <summary>
    /// Called when a worker completes. Decrements active count and dequeues enough queued work
    /// to restore the configured floor/target.
    /// </summary>
    public static IReadOnlyList<BackpressureQueueEntry> CompleteAndTopUp(BackpressureQueueState bp)
    {
        bp.ActiveWorkers = Math.Max(0, bp.ActiveWorkers - 1);
        return TopUpToTarget(bp);
    }

    /// <summary>
    /// Dequeues enough queued work to restore the configured floor/target without exceeding max.
    /// </summary>
    public static IReadOnlyList<BackpressureQueueEntry> TopUpToTarget(BackpressureQueueState bp)
    {
        NormalizeHeadIndex(bp);

        var target = ResolveDispatchTarget(bp);
        if (target <= 0 || bp.ActiveWorkers >= target || QueuedCount(bp) == 0)
            return [];

        var drained = new List<BackpressureQueueEntry>();
        while (bp.ActiveWorkers < target && QueuedCount(bp) > 0)
        {
            drained.Add(DequeueOne(bp));
            bp.ActiveWorkers++;
        }

        CompactIfNeeded(bp);
        return drained;
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

        var next = DequeueOne(bp);
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
            InputFileRefs = { entry.InputFileRefs.Select(static fileRef => fileRef.Clone()) },
            ExternalInvocation = entry.ExternalInvocation?.Clone(),
        };

    /// <summary>Creates a queue entry from step request fields.</summary>
    public static BackpressureQueueEntry ToQueueEntry(
        string stepId, string stepType, string runId, string input,
        string targetRole, IDictionary<string, string>? parameters,
        IEnumerable<WorkflowFileRef>? inputFileRefs = null,
        ExternalToolInvocationSpec? externalInvocation = null) =>
        new()
        {
            StepId = stepId,
            StepType = stepType,
            RunId = runId,
            Input = input,
            TargetRole = targetRole,
            Parameters = { parameters ?? new Dictionary<string, string>() },
            InputFileRefs = { inputFileRefs?.Select(static fileRef => fileRef.Clone()) ?? [] },
            ExternalInvocation = externalInvocation?.Clone(),
        };

    /// <summary>Initializes backpressure state with the resolved max/min concurrency.</summary>
    public static BackpressureQueueState Initialize(int maxConcurrent, int minConcurrent = 0) =>
        new()
        {
            MaxConcurrentWorkers = Math.Max(1, maxConcurrent),
            MinConcurrentWorkers = Math.Clamp(minConcurrent, 0, Math.Max(1, maxConcurrent)),
        };

    /// <summary>
    /// Ensures a usable backpressure state exists. Proto message fields may be null on older
    /// persisted state or on paths that complete before the admission path initialized them.
    /// </summary>
    public static BackpressureQueueState EnsureInitialized(
        BackpressureQueueState? current,
        int maxConcurrent,
        int minConcurrent = 0)
    {
        if (current == null || current.MaxConcurrentWorkers <= 0)
            return Initialize(maxConcurrent, minConcurrent);

        current.MaxConcurrentWorkers = Math.Max(1, current.MaxConcurrentWorkers);
        current.MinConcurrentWorkers = Math.Clamp(current.MinConcurrentWorkers, 0, current.MaxConcurrentWorkers);
        return current;
    }

    private static BackpressureQueueEntry DequeueOne(BackpressureQueueState bp)
    {
        NormalizeHeadIndex(bp);
        var next = bp.Queue[bp.HeadIndex];
        bp.HeadIndex++;
        return next;
    }

    private static int ResolveDispatchTarget(BackpressureQueueState bp)
    {
        var maxConcurrent = Math.Max(1, bp.MaxConcurrentWorkers);
        var minConcurrent = Math.Clamp(bp.MinConcurrentWorkers, 0, maxConcurrent);
        return minConcurrent > 0 ? minConcurrent : maxConcurrent;
    }

    private static bool TryGetPositiveInt(
        IDictionary<string, string> parameters,
        out int value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            if (!int.TryParse(raw.Trim(), out var parsed) || parsed <= 0)
                continue;

            value = parsed;
            return true;
        }

        value = default;
        return false;
    }

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
