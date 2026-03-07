using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowCacheCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "cache",
        SupportedStepTypes: ["cache"]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        HandleCacheStepAsync(request, read, write, effects, ct);

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read)
    {
        foreach (var pending in read.State.PendingCacheCalls.Values)
        {
            if (string.Equals(pending.ChildStepId, evt.StepId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        foreach (var (cacheKey, pending) in state.PendingCacheCalls)
        {
            if (!string.Equals(pending.ChildStepId, evt.StepId, StringComparison.Ordinal))
                continue;

            var next = state.Clone();
            next.PendingCacheCalls.Remove(cacheKey);
            next.StepExecutions.Remove(evt.StepId);
            if (evt.Success)
            {
                next.CacheEntries[cacheKey] = new WorkflowCacheEntry
                {
                    Value = evt.Output ?? string.Empty,
                    ExpiresAtUnixTimeMs = DateTimeOffset.UtcNow.AddSeconds(pending.TtlSeconds).ToUnixTimeMilliseconds(),
                };
            }
            await write.PersistStateAsync(next, ct);

            foreach (var waiter in pending.Waiters)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = waiter.ParentStepId,
                    RunId = state.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                completed.Metadata["cache.hit"] = "false";
                completed.Metadata["cache.key"] = WorkflowCapabilityValueParsers.ShortenKey(cacheKey);
                await write.PublishAsync(completed, EventDirection.Self, ct);
            }

            return;
        }
    }

    public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read) => false;

    public Task HandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResponse(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleChildRunCompletion(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static async Task HandleCacheStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? string.Empty);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = read.State;
        if (state.CacheEntries.TryGetValue(cacheKey, out var existing) &&
            existing.ExpiresAtUnixTimeMs > nowMs)
        {
            var hit = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = read.RunId,
                Success = true,
                Output = existing.Value,
            };
            hit.Metadata["cache.hit"] = "true";
            hit.Metadata["cache.key"] = WorkflowCapabilityValueParsers.ShortenKey(cacheKey);
            await write.PublishAsync(hit, EventDirection.Self, ct);
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
            await write.PersistStateAsync(next, ct);
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
        await write.PersistStateAsync(nextState, ct);

        await effects.DispatchInternalStepAsync(
            read.RunId,
            request.StepId,
            childStepId,
            childType,
            request.Input ?? string.Empty,
            childRole ?? string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ct);
    }
}
