using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Result caching module. Caches step results by key; on cache hit, completes
/// immediately without executing the child step. On miss, dispatches the child
/// step and caches the result on completion.
/// </summary>
public sealed class CacheModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "cache";

    public string Name => "cache";
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
            if (request.StepType != "cache") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            // Refactor (iter89/cluster-089-workflow-module-clock-state):
            //   Old: Cache TTL checks read DateTimeOffset.UtcNow directly.
            //   New: Cache business time comes from the workflow context clock.
            var now = ctx.UtcNow;
            var state = WorkflowExecutionStateAccess.Load<CacheModuleState>(ctx, ModuleStateKey);

            var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? "");
            var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var t) ? t : 3600;
            ttlSeconds = Math.Clamp(ttlSeconds, 1, 86_400);
            var waiter = new CacheWaiterState
            {
                ParentStepId = request.StepId,
                ParentExecutionId = request.ExecutionId,
                RunId = runId,
            };

            if (state.CacheEntries.TryGetValue(cacheKey, out var existingCache) &&
                WorkflowTimestampCodec.ToDateTimeOffset(existingCache.ExpiresAt) <= now)
                state.CacheEntries.Remove(cacheKey);

            if (state.CacheEntries.TryGetValue(cacheKey, out var cached) &&
                WorkflowTimestampCodec.ToDateTimeOffset(cached.ExpiresAt) > now)
            {
                ctx.Logger.LogInformation("Cache {StepId}: HIT key={Key}", request.StepId, ShortenKey(cacheKey));
                var hit = new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    ExecutionId = request.ExecutionId,
                    Success = true,
                    Output = cached.Value,
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                };
                hit.Annotations["cache.hit"] = "true";
                hit.Annotations["cache.key"] = ShortenKey(cacheKey);
                await ctx.PublishAsync(hit, TopologyAudience.Self, ct);
                return;
            }

            if (state.PendingByCacheKey.TryGetValue(cacheKey, out var pending))
            {
                pending.Waiters.Add(waiter);
                state.PendingByCacheKey[cacheKey] = pending;
                await SaveStateAsync(state, ctx, ct);
                ctx.Logger.LogInformation(
                    "Cache {StepId}: PENDING key={Key}, join waiters={Waiters}",
                    request.StepId,
                    ShortenKey(cacheKey),
                    pending.Waiters.Count);
                return;
            }

            ctx.Logger.LogInformation("Cache {StepId}: MISS key={Key}, dispatching child", request.StepId, ShortenKey(cacheKey));

            var childType = WorkflowPrimitiveCatalog.ToCanonicalType(
                request.Parameters.GetValueOrDefault("child_step_type", "llm_call"));
            var childRole = request.Parameters.GetValueOrDefault("child_target_role", request.TargetRole);
            var childStepId = $"{request.StepId}_cached_{Guid.NewGuid():N}";
            var childExecutionId = WorkflowExecutionValueStore.BuildInternalExecutionId(
                runId,
                request.ExecutionId,
                childStepId);

            var pendingCall = new PendingCacheCallState
            {
                TtlSeconds = ttlSeconds,
                ChildStepId = childStepId,
                ChildExecutionId = childExecutionId,
            };
            pendingCall.Waiters.Add(waiter);
            state.PendingByCacheKey[cacheKey] = pendingCall;
            state.ChildStepToCacheKey[BuildChildKey(runId, childStepId)] = cacheKey;
            await SaveStateAsync(state, ctx, ct);

            var childRequest = new StepRequestEvent
            {
                StepId = childStepId,
                StepType = childType,
                RunId = runId,
                Input = request.Input ?? "",
                TargetRole = childRole ?? "",
                ExecutionId = childExecutionId,
                InputValueId = request.InputValueId,
                ExternalInvocation = request.ExternalInvocation?.Clone(),
            };
            foreach (var (key, value) in request.Parameters)
            {
                if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                    childRequest.Parameters[key["sub_param_".Length..]] = value;
            }

            await ctx.PublishAsync(childRequest, TopologyAudience.Self, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowExecutionStateAccess.Load<CacheModuleState>(ctx, ModuleStateKey);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var childKey = BuildChildKey(runId, evt.StepId);
            if (!state.ChildStepToCacheKey.TryGetValue(childKey, out var cacheKey))
                return;
            if (!state.PendingByCacheKey.TryGetValue(cacheKey, out var pending))
            {
                ctx.Logger.LogWarning(
                    "Cache child {ChildStepId} mapped to key={Key} but no pending call remains. run={RunId}",
                    evt.StepId,
                    ShortenKey(cacheKey),
                    runId);
                return;
            }
            if (normalizedValues &&
                (!string.Equals(pending.ChildStepId, evt.StepId, StringComparison.Ordinal) ||
                 !string.Equals(pending.ChildExecutionId, evt.ExecutionId, StringComparison.Ordinal)))
            {
                ctx.Logger.LogWarning(
                    "Cache ignored stale child completion. run={RunId} child={ChildStepId} " +
                    "expectedExecution={ExpectedExecutionId} actualExecution={ActualExecutionId}",
                    runId,
                    evt.StepId,
                    pending.ChildExecutionId,
                    evt.ExecutionId);
                return;
            }

            if (!normalizedValues)
            {
                state.ChildStepToCacheKey.Remove(childKey);
                state.PendingByCacheKey.Remove(cacheKey);
                if (evt.Success)
                {
                    state.CacheEntries[cacheKey] = new CacheEntryState
                    {
                        Value = evt.Output ?? string.Empty,
                        ExpiresAt = WorkflowTimestampCodec.ToTimestamp(
                            ctx.UtcNow.AddSeconds(pending.TtlSeconds)),
                    };
                }
                await SaveStateAsync(state, ctx, ct);
                foreach (var waiter in pending.Waiters)
                {
                    var completed = new StepCompletedEvent
                    {
                        StepId = waiter.ParentStepId,
                        RunId = waiter.RunId,
                        ExecutionId = waiter.ParentExecutionId,
                        Success = evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    };
                    completed.Annotations["cache.hit"] = "false";
                    completed.Annotations["cache.key"] = ShortenKey(cacheKey);
                    await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
                }
                ctx.Logger.LogInformation(
                    "Cache child {ChildStepId} completed key={Key}; released {WaiterCount} waiter(s). run={RunId}",
                    evt.StepId,
                    ShortenKey(cacheKey),
                    pending.Waiters.Count,
                    runId);
                return;
            }

            if (pending.PendingCompletions.Count == 0)
            {
                if (evt.Success)
                {
                    state.CacheEntries[cacheKey] = new CacheEntryState
                    {
                        Value = evt.Output ?? string.Empty,
                        ExpiresAt = WorkflowTimestampCodec.ToTimestamp(ctx.UtcNow.AddSeconds(pending.TtlSeconds)),
                    };
                }
                foreach (var waiter in pending.Waiters)
                {
                    var completed = new StepCompletedEvent
                    {
                        StepId = waiter.ParentStepId,
                        RunId = waiter.RunId,
                        ExecutionId = waiter.ParentExecutionId,
                        Success = evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                    };
                    completed.Annotations["cache.hit"] = "false";
                    completed.Annotations["cache.key"] = ShortenKey(cacheKey);
                    pending.PendingCompletions.Add(completed);
                }
                if (normalizedValues && pending.PendingCompletions.Count > 0 &&
                    pending.PendingCompletions[0].OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput)
                {
                    foreach (var completion in pending.PendingCompletions)
                    {
                        await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                            completion,
                            evt,
                            ctx,
                            ct);
                    }
                }
                state.PendingByCacheKey[cacheKey] = pending;
                await SaveStateAsync(state, ctx, ct);
            }

            foreach (var completed in pending.PendingCompletions)
            {
                if (string.IsNullOrWhiteSpace(completed.OutputReferenceId))
                {
                    await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(completed, evt, ctx, ct);
                    state.PendingByCacheKey[cacheKey] = pending;
                    await SaveStateAsync(state, ctx, ct);
                }
                await ctx.PublishAsync(
                    WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completed, ctx),
                    TopologyAudience.Self,
                    ct);
            }

            state.ChildStepToCacheKey.Remove(childKey);
            state.PendingByCacheKey.Remove(cacheKey);
            await SaveStateAsync(state, ctx, CancellationToken.None);
            ctx.Logger.LogInformation(
                "Cache child {ChildStepId} completed key={Key}; released {WaiterCount} waiter(s). run={RunId}",
                evt.StepId,
                ShortenKey(cacheKey),
                pending.Waiters.Count,
                runId);
        }
    }

    private static string ShortenKey(string key) => key.Length > 60 ? key[..60] + "..." : key;

    private static string BuildChildKey(string runId, string childStepId) =>
        $"{runId}:{childStepId}";

    private static Task SaveStateAsync(
        CacheModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.CacheEntries.Count == 0 &&
            state.PendingByCacheKey.Count == 0 &&
            state.ChildStepToCacheKey.Count == 0)
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
