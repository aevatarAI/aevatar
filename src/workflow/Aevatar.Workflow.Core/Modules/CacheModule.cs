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
public sealed class CacheModule : IEventModule
{
    private readonly Dictionary<string, CacheEntry> _cache = [];
    private readonly Dictionary<string, PendingCacheCall> _pendingByCacheKey = [];
    private readonly Dictionary<(string RunId, string ChildStepId), string> _childToCacheKey = [];

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

            var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? "");
            var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var t) ? t : 3600;
            ttlSeconds = Math.Clamp(ttlSeconds, 1, 86_400);
            var waiter = new CacheWaiter(runId, request.StepId);

            if (_cache.TryGetValue(cacheKey, out var existingCache) && existingCache.ExpiresAt <= now)
                _cache.Remove(cacheKey);

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
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

            if (_pendingByCacheKey.TryGetValue(cacheKey, out var pending))
            {
                pending.Waiters.Add(waiter);
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

            var pendingCall = new PendingCacheCall(ttlSeconds, [waiter]);
            _pendingByCacheKey[cacheKey] = pendingCall;
            _childToCacheKey[(runId, childStepId)] = cacheKey;

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
            if (!_childToCacheKey.Remove((runId, evt.StepId), out var cacheKey))
                return;
            if (!_pendingByCacheKey.Remove(cacheKey, out var pending))
                return;

            if (evt.Success)
            {
                _cache[cacheKey] = new CacheEntry(
                    evt.Output ?? string.Empty,
                    DateTimeOffset.UtcNow.AddSeconds(pending.TtlSeconds));
            }

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

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt);

    private sealed record CacheWaiter(string RunId, string ParentStepId);

    private sealed class PendingCacheCall(
        int ttlSeconds,
        List<CacheWaiter> waiters)
    {
        public int TtlSeconds { get; } = ttlSeconds;
        public List<CacheWaiter> Waiters { get; } = waiters;
    }
}
