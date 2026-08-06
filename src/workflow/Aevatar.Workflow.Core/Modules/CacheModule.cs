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

    /// <summary>Marker embedded in dispatched child step ids so orphaned completions stay attributable.</summary>
    private const string ChildStepMarker = "_cached_";

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
                RunId = runId,
                ExecutionId = request.ExecutionId ?? string.Empty,
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
                    Success = true,
                    Output = cached.Value,
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
            var childStepId = $"{request.StepId}{ChildStepMarker}{Guid.NewGuid():N}";

            var pendingCall = new PendingCacheCallState
            {
                TtlSeconds = ttlSeconds,
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
            };
            // The kernel synthesizes the sub-step call site on the cache step itself and expects
            // primitives that dispatch sub-steps to copy it onto every child they publish; without
            // it an external child (tool_call/connector_call) loses its admission identity.
            if (request.ExternalInvocation != null)
                childRequest.ExternalInvocation = request.ExternalInvocation.Clone();

            await ctx.PublishAsync(childRequest, TopologyAudience.Self, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowExecutionStateAccess.Load<CacheModuleState>(ctx, ModuleStateKey);
            var childKey = BuildChildKey(runId, evt.StepId);
            if (!state.ChildStepToCacheKey.Remove(childKey, out var cacheKey))
            {
                // Not a cache child (the vast majority of completions) — stay quiet. Only warn when
                // the step id carries the cache child marker, which means the parent↔child mapping
                // was lost and every waiter on it is now stranded with no other recovery path.
                if (evt.StepId.Contains(ChildStepMarker, StringComparison.Ordinal))
                {
                    ctx.Logger.LogWarning(
                        "Cache {StepId}: orphan child completion, no parent mapping. run={RunId} success={Success}",
                        evt.StepId,
                        runId,
                        evt.Success);
                }

                return;
            }

            if (!state.PendingByCacheKey.Remove(cacheKey, out var pending))
            {
                ctx.Logger.LogWarning(
                    "Cache {StepId}: child mapped to key={Key} but no pending call remains; waiters stranded. run={RunId}",
                    evt.StepId,
                    ShortenKey(cacheKey),
                    runId);
                return;
            }

            if (evt.Success)
            {
                state.CacheEntries[cacheKey] = new CacheEntryState
                {
                    Value = evt.Output ?? string.Empty,
                    ExpiresAt = WorkflowTimestampCodec.ToTimestamp(ctx.UtcNow.AddSeconds(pending.TtlSeconds)),
                };
            }
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation(
                "Cache {StepId}: child completed key={Key} success={Success}, releasing waiters={Waiters}",
                evt.StepId,
                ShortenKey(cacheKey),
                evt.Success,
                pending.Waiters.Count);

            foreach (var waiter in pending.Waiters)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = waiter.ParentStepId,
                    RunId = waiter.RunId,
                    // Carry the dispatch identity the kernel assigned to the parent step so a
                    // completion synthesized for a superseded dispatch is rejected instead of
                    // silently advancing the run.
                    ExecutionId = waiter.ExecutionId ?? string.Empty,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                completed.Annotations["cache.hit"] = "false";
                completed.Annotations["cache.key"] = ShortenKey(cacheKey);
                await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
            }
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
