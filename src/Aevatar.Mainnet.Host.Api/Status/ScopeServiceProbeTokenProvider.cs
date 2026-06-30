using System.Collections.Concurrent;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.GAgents.StatusDashboard.Executors;

namespace Aevatar.Mainnet.Host.Api.Status;

/// <summary>
/// Host-side <see cref="IProbeServiceTokenProvider"/> backed by the scope service token issuer. It
/// mints a short-lived self-issued bearer per probe scope and caches it until shortly before expiry
/// (the canon warns against pinning a long-lived static token). When the issuer is not registered —
/// i.e. <c>Aevatar:Authentication:ScopeServiceTokens:Enabled</c> is off — it returns <c>null</c>, so
/// the credentialed orchestration probes degrade to <c>unknown</c> rather than a false <c>down</c>.
/// </summary>
internal sealed class ScopeServiceProbeTokenProvider : IProbeServiceTokenProvider
{
    private const string ProbeServiceId = "status-probe";
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly IScopeServiceTokenIssuer? _issuer;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

    public ScopeServiceProbeTokenProvider(TimeProvider timeProvider, IScopeServiceTokenIssuer? issuer)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _issuer = issuer;
    }

    public Task<string?> GetScopeBearerAsync(string scopeId, CancellationToken ct)
    {
        if (_issuer is null || string.IsNullOrWhiteSpace(scopeId))
            return Task.FromResult<string?>(null);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(scopeId, out var cached) && cached.ExpiresAt > now.Add(RefreshSkew))
            return Task.FromResult<string?>(cached.AccessToken);

        var minted = _issuer.Issue(new ScopeServiceTokenRequest(
            ScopeId: scopeId,
            ServiceId: ProbeServiceId,
            ServiceKey: ProbeServiceId));
        _cache[scopeId] = new CachedToken(minted.AccessToken, minted.ExpiresAt);
        return Task.FromResult<string?>(minted.AccessToken);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}
