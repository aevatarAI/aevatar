using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunCacheRuntime
{
    private readonly WorkflowRunRuntimeContext _context;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;

    public WorkflowRunCacheRuntime(
        WorkflowRunRuntimeContext context,
        WorkflowRunDispatchRuntime dispatchRuntime)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatchRuntime = dispatchRuntime ?? throw new ArgumentNullException(nameof(dispatchRuntime));
    }

    public async Task HandleCacheStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? string.Empty);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = _context.State;
        if (state.CacheEntries.TryGetValue(cacheKey, out var existing) &&
            existing.ExpiresAtUnixTimeMs > nowMs)
        {
            var hit = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = existing.Value,
            };
            hit.Metadata["cache.hit"] = "true";
            hit.Metadata["cache.key"] = WorkflowRunSupport.ShortenKey(cacheKey);
            await _context.PublishAsync(hit, EventDirection.Self, ct);
            return;
        }

        if (state.PendingCacheCalls.TryGetValue(cacheKey, out var pending))
        {
            var nextPending = pending.Clone();
            nextPending.Waiters.Add(new WorkflowCacheWaiter
            {
                ParentStepId = request.StepId,
            });
            var next = state.Clone();
            next.PendingCacheCalls[cacheKey] = nextPending;
            await _context.PersistStateAsync(next, ct);
            return;
        }

        var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var ttl)
            ? Math.Clamp(ttl, 1, 86_400)
            : 3600;
        var childType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.GetValueOrDefault("child_step_type", "llm_call"));
        var childRole = request.Parameters.GetValueOrDefault("child_target_role", request.TargetRole);
        var childStepId = $"{request.StepId}_cached_{Guid.NewGuid():N}";

        var nextState = state.Clone();
        var pendingState = new WorkflowPendingCacheState
        {
            ChildStepId = childStepId,
            TtlSeconds = ttlSeconds,
        };
        pendingState.Waiters.Add(new WorkflowCacheWaiter
        {
            ParentStepId = request.StepId,
        });
        nextState.PendingCacheCalls[cacheKey] = pendingState;
        await _context.PersistStateAsync(nextState, ct);

        await _dispatchRuntime.DispatchInternalStepAsync(
            runId,
            request.StepId,
            childStepId,
            childType,
            request.Input ?? string.Empty,
            childRole ?? string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ct);
    }
}
