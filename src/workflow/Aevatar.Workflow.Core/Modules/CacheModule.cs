using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Result caching module. Caches step results by key; on cache hit, completes
/// immediately without executing the child step. On miss, dispatches the child
/// step and caches the result on completion.
/// </summary>
public sealed class CacheModule : IEventModule
{
    private const string ModuleStateKey = "cache";

    public string Name => "cache";
    public int Priority => 3;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "cache") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var now = DateTimeOffset.UtcNow;
            var state = WorkflowRunModuleStateAccess.Load<CacheModuleState>(ctx, ModuleStateKey);

            var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? "");
            var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var t) ? t : 3600;
            ttlSeconds = Math.Clamp(ttlSeconds, 1, 86_400);
            var waiter = new CacheWaiterState { ParentStepId = request.StepId, RunId = runId };

            if (state.CacheEntries.TryGetValue(cacheKey, out var existingCache) && existingCache.ExpiresAt <= now)
                state.CacheEntries.Remove(cacheKey);

            if (state.CacheEntries.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            {
                ctx.Logger.LogInformation("Cache {StepId}: HIT key={Key}", request.StepId, ShortenKey(cacheKey));
                var hit = new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = true,
                    Output = cached.Value,
                };
                hit.Metadata["cache.hit"] = "true";
                hit.Metadata["cache.key"] = ShortenKey(cacheKey);
                await ctx.PublishAsync(hit, EventDirection.Self, ct);
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

            state.PendingByCacheKey[cacheKey] = new PendingCacheCallState
            {
                TtlSeconds = ttlSeconds,
                Waiters = [waiter],
            };
            state.ChildStepToCacheKey[BuildChildKey(runId, childStepId)] = cacheKey;
            await SaveStateAsync(state, ctx, ct);

            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = childStepId,
                StepType = childType,
                RunId = runId,
                Input = request.Input ?? "",
                TargetRole = childRole ?? "",
            }, EventDirection.Self, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowRunModuleStateAccess.Load<CacheModuleState>(ctx, ModuleStateKey);
            var childKey = BuildChildKey(runId, evt.StepId);
            if (!state.ChildStepToCacheKey.Remove(childKey, out var cacheKey))
                return;
            if (!state.PendingByCacheKey.Remove(cacheKey, out var pending))
                return;

            if (evt.Success)
            {
                state.CacheEntries[cacheKey] = new CacheEntryState
                {
                    Value = evt.Output ?? string.Empty,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(pending.TtlSeconds),
                };
            }
            await SaveStateAsync(state, ctx, ct);

            foreach (var waiter in pending.Waiters)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = waiter.ParentStepId,
                    RunId = waiter.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                completed.Metadata["cache.hit"] = "false";
                completed.Metadata["cache.key"] = ShortenKey(cacheKey);
                await ctx.PublishAsync(completed, EventDirection.Self, ct);
            }
        }
    }

    private static string ShortenKey(string key) => key.Length > 60 ? key[..60] + "..." : key;

    private static string BuildChildKey(string runId, string childStepId) =>
        $"{runId}:{childStepId}";

    private static Task SaveStateAsync(
        CacheModuleState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (state.CacheEntries.Count == 0 &&
            state.PendingByCacheKey.Count == 0 &&
            state.ChildStepToCacheKey.Count == 0)
        {
            return WorkflowRunModuleStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowRunModuleStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    public sealed class CacheModuleState
    {
        public Dictionary<string, CacheEntryState> CacheEntries { get; set; } = [];
        public Dictionary<string, PendingCacheCallState> PendingByCacheKey { get; set; } = [];
        public Dictionary<string, string> ChildStepToCacheKey { get; set; } = [];
    }

    public sealed class CacheEntryState
    {
        public string Value { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class CacheWaiterState
    {
        public string RunId { get; set; } = string.Empty;
        public string ParentStepId { get; set; } = string.Empty;
    }

    public sealed class PendingCacheCallState
    {
        public int TtlSeconds { get; set; }
        public List<CacheWaiterState> Waiters { get; set; } = [];
    }
}
