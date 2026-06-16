using System.Collections.Concurrent;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// In-memory <see cref="IVoiceSessionCredentialStore" />: holds the caller's NyxID token per voice
/// session in a concurrent dictionary, with an expiry backstop. Never persisted — same host-co-located,
/// per-session volatility as <see cref="VoiceVolatileMediaStreamPort" />.
/// </summary>
public sealed class VoiceVolatileSessionCredentialStore : IVoiceSessionCredentialStore
{
    private readonly ConcurrentDictionary<string, Entry> _bySession = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public VoiceVolatileSessionCredentialStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public void Set(string sessionId, string nyxIdAccessToken, DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(nyxIdAccessToken))
            return;

        _bySession[sessionId] = new Entry(nyxIdAccessToken, expiresAtUtc);
    }

    public bool TryGet(string sessionId, out string nyxIdAccessToken)
    {
        nyxIdAccessToken = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || !_bySession.TryGetValue(sessionId, out var entry))
            return false;

        if (entry.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            _bySession.TryRemove(sessionId, out _);
            return false;
        }

        nyxIdAccessToken = entry.Token;
        return true;
    }

    public void Remove(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            _bySession.TryRemove(sessionId, out _);
    }

    private readonly record struct Entry(string Token, DateTimeOffset ExpiresAtUtc);
}
