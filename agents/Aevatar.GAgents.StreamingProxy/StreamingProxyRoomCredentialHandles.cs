using Microsoft.Extensions.Caching.Memory;

namespace Aevatar.GAgents.StreamingProxy;

internal interface IStreamingProxyRoomCredentialHandleStore
{
    string Create(
        string? bearerToken,
        StreamingProxyRoomCredentialHandleScope scope);

    string? Consume(
        string? handleId,
        StreamingProxyRoomCredentialHandleScope scope);
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
    private readonly TimeSpan _handleTtl;
    private readonly TimeProvider _timeProvider;

    public StreamingProxyRoomCredentialHandleStore(IMemoryCache cache)
        : this(cache, HandleTtl, TimeProvider.System)
    {
    }

    internal StreamingProxyRoomCredentialHandleStore(
        IMemoryCache cache,
        TimeSpan handleTtl,
        TimeProvider? timeProvider = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _handleTtl = handleTtl > TimeSpan.Zero
            ? handleTtl
            : throw new ArgumentOutOfRangeException(nameof(handleTtl), "Handle TTL must be positive.");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Create(
        string? bearerToken,
        StreamingProxyRoomCredentialHandleScope scope)
    {
        scope = scope.Normalize();
        var handleId = Guid.NewGuid().ToString("N");
        var normalized = Normalize(bearerToken) ?? string.Empty;

        _cache.Set(
            BuildKey(handleId),
            new StreamingProxyRoomCredentialHandleEntry(
                normalized,
                scope,
                _timeProvider.GetUtcNow().Add(_handleTtl)),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _handleTtl,
                Priority = CacheItemPriority.NeverRemove,
            });

        return handleId;
    }

    public string? Consume(
        string? handleId,
        StreamingProxyRoomCredentialHandleScope scope)
    {
        var normalizedHandle = Normalize(handleId);
        if (normalizedHandle is null)
            return null;

        var key = BuildKey(normalizedHandle);
        if (!_cache.TryGetValue(key, out StreamingProxyRoomCredentialHandleEntry? entry) ||
            entry is null)
            return null;

        scope = scope.Normalize();
        if (!entry.Scope.Equals(scope))
            return null;

        if (_timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            _cache.Remove(key);
            return null;
        }

        _cache.Remove(key);
        return Normalize(entry.BearerToken);
    }

    private static string BuildKey(string handleId) =>
        $"streaming-proxy-room-chat-credential:{handleId}";

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record StreamingProxyRoomCredentialHandleEntry(
        string BearerToken,
        StreamingProxyRoomCredentialHandleScope Scope,
        DateTimeOffset ExpiresAt);
}

internal readonly record struct StreamingProxyRoomCredentialHandleScope(
    string RoomId,
    string ScopeId,
    string SessionId)
{
    public StreamingProxyRoomCredentialHandleScope Normalize() =>
        new(NormalizeRequired(RoomId, nameof(RoomId)),
            NormalizeRequired(ScopeId, nameof(ScopeId)),
            NormalizeRequired(SessionId, nameof(SessionId)));

    private static string NormalizeRequired(string value, string name)
    {
        var normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException($"{name} is required.", name)
            : normalized;
    }
}
