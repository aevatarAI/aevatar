using System.Collections.Concurrent;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public interface INyxIdRelayReplayGuard
{
    bool TryClaim(string replayKey, DateTimeOffset observedAtUtc);
}

public sealed class NyxIdRelayReplayGuard : INyxIdRelayReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;

    public NyxIdRelayReplayGuard()
        : this(TimeSpan.FromMinutes(5), TimeProvider.System)
    {
    }

    public NyxIdRelayReplayGuard(TimeSpan window, TimeProvider timeProvider)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "Replay window must be positive.");
        _window = window;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryClaim(string replayKey, DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayKey);

        var now = _timeProvider.GetUtcNow();
        SweepExpired(now);

        var expiry = observedAtUtc + _window;
        if (expiry <= now)
            return false;

        return _claims.TryAdd(replayKey.Trim(), expiry);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var claim in _claims)
        {
            if (claim.Value <= now)
                _claims.TryRemove(claim);
        }
    }
}
