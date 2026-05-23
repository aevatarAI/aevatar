using Microsoft.Extensions.Caching.Memory;

namespace Aevatar.GAgents.StreamingProxy;

internal interface IStreamingProxyRoomCredentialHandleStore
{
    string Create(string? bearerToken);

    string? Consume(string? handleId);
}

/// <summary>
/// Short-lived, process-local bearer holder for room chat dispatch.
/// The typed actor command carries only this opaque handle so raw bearer
/// values never enter protobuf command payloads or EventEnvelope bytes.
/// </summary>
internal sealed class StreamingProxyRoomCredentialHandleStore : IStreamingProxyRoomCredentialHandleStore
{
    private static readonly TimeSpan HandleTtl = TimeSpan.FromMinutes(2);
    private readonly IMemoryCache _cache;

    public StreamingProxyRoomCredentialHandleStore(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string Create(string? bearerToken)
    {
        var handleId = Guid.NewGuid().ToString("N");
        var normalized = Normalize(bearerToken) ?? string.Empty;

        _cache.Set(
            BuildKey(handleId),
            normalized,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = HandleTtl,
                Priority = CacheItemPriority.NeverRemove,
            });

        return handleId;
    }

    public string? Consume(string? handleId)
    {
        var normalizedHandle = Normalize(handleId);
        if (normalizedHandle is null)
            return null;

        var key = BuildKey(normalizedHandle);
        if (!_cache.TryGetValue(key, out string? bearerToken))
            return null;

        _cache.Remove(key);
        return Normalize(bearerToken);
    }

    private static string BuildKey(string handleId) =>
        $"streaming-proxy-room-chat-credential:{handleId}";

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
