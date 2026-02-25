using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
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
    private readonly Dictionary<(string RunId, string StepId), (string RunId, string StepId)> _childToParent = [];

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
            var runId = NormalizeRunId(request.RunId);

            var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? "");
            var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var t) ? t : 3600;
            ttlSeconds = Math.Clamp(ttlSeconds, 1, 86_400);

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                ctx.Logger.LogInformation("Cache {StepId}: HIT key={Key}", request.StepId, ShortenKey(cacheKey));
                var hit = new StepCompletedEvent
                {
                    StepId = request.StepId, RunId = request.RunId, Success = true, Output = cached.Value,
                };
                hit.Metadata["cache.hit"] = "true";
                hit.Metadata["cache.key"] = ShortenKey(cacheKey);
                await ctx.PublishAsync(hit, EventDirection.Self, ct);
                return;
            }

            ctx.Logger.LogInformation("Cache {StepId}: MISS key={Key}, dispatching child", request.StepId, ShortenKey(cacheKey));

            var childType = request.Parameters.GetValueOrDefault("child_step_type", "llm_call");
            var childRole = request.Parameters.GetValueOrDefault("child_target_role", request.TargetRole);
            var childStepId = $"{request.StepId}_cached";

            var childKey = (runId, childStepId);
            _childToParent[childKey] = (runId, request.StepId);
            _cache[cacheKey] = new CacheEntry("", DateTimeOffset.UtcNow.AddSeconds(ttlSeconds), request.StepId, runId, cacheKey);

            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = childStepId,
                StepType = childType,
                RunId = request.RunId,
                Input = request.Input ?? "",
                TargetRole = childRole ?? "",
            }, EventDirection.Self, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = NormalizeRunId(evt.RunId);
            var childKey = (runId, evt.StepId);
            if (!_childToParent.Remove(childKey, out var parentKey)) return;

            var cacheKey = _cache.Values
                .Where(e => e.ParentStepId == parentKey.StepId && e.ParentRunId == parentKey.RunId)
                .Select(e => e.Key)
                .FirstOrDefault();

            if (cacheKey != null && evt.Success)
            {
                var existing = _cache[cacheKey];
                _cache[cacheKey] = existing with { Value = evt.Output };
            }

            var completed = new StepCompletedEvent
            {
                StepId = parentKey.StepId, RunId = parentKey.RunId, Success = evt.Success, Output = evt.Output, Error = evt.Error,
            };
            completed.Metadata["cache.hit"] = "false";
            if (cacheKey != null) completed.Metadata["cache.key"] = ShortenKey(cacheKey);
            await ctx.PublishAsync(completed, EventDirection.Self, ct);
        }
    }

    private static string NormalizeRunId(string runId) => string.IsNullOrWhiteSpace(runId) ? string.Empty : runId;

    private static string ShortenKey(string key) => key.Length > 60 ? key[..60] + "..." : key;

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt, string ParentStepId, string ParentRunId, string Key);
}
