namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Inspects the live Elasticsearch cluster to determine whether the read grant
/// on the <c>aevatar-oauth-clients</c> index — which stores state-token HMAC key
/// material — is actually restricted to internal grain/event-store services,
/// rather than trusting an operator-declared configuration flag.
/// </summary>
/// <remarks>
/// Implementations reuse the same Elasticsearch endpoint + credentials the
/// projection document store uses. When Elasticsearch is not configured (dev /
/// InMemory projection provider) the default implementation returns
/// <see cref="EsAclProbeStatus.Unavailable"/>; enforcement mode determines whether
/// that result is logged or rejected. Elasticsearch-backed Mainnet composition
/// preserves exactly one deployment-provided implementation registered before
/// the host is composed; multiple custom verifiers fail composition.
/// </remarks>
public interface IOAuthClientEsAclProbe
{
    /// <summary>
    /// Queries the Elasticsearch security API and reports the observed state of
    /// the <c>aevatar-oauth-clients</c> read grant. Never throws for an expected
    /// "cannot determine" outcome; those are reported as
    /// <see cref="EsAclProbeStatus.Unavailable"/> or
    /// <see cref="EsAclProbeStatus.Unverifiable"/> so the guard can decide policy.
    /// </summary>
    Task<EsAclProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Classification of what the probe observed about the OAuth-clients index read grant.
/// </summary>
public enum EsAclProbeStatus
{
    /// <summary>
    /// Elasticsearch is not configured for this host (dev / InMemory provider),
    /// so there is no cluster to probe. Strict enforcement rejects this status.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The security API could not be reached or did not return enough
    /// information to classify the grant (e.g. security disabled, HTTP error,
    /// timeout). The guard treats this as "not confirmed restricted".
    /// </summary>
    Unverifiable = 1,

    /// <summary>
    /// The security API is reachable and the observed role mappings / privilege
    /// state indicate the index read grant is limited (restricted).
    /// </summary>
    Restricted = 2,

    /// <summary>
    /// The security API is reachable and definitively reports the index read
    /// grant is NOT restricted (broadly readable).
    /// </summary>
    Unrestricted = 3,
}

/// <summary>
/// Immutable result of an <see cref="IOAuthClientEsAclProbe"/> probe. Carries the
/// classification plus a human-readable description of the observed cluster state
/// for the guard to log.
/// </summary>
public sealed record EsAclProbeResult(EsAclProbeStatus Status, string ObservedState)
{
    public static EsAclProbeResult Unavailable(string observedState) =>
        new(EsAclProbeStatus.Unavailable, observedState);

    public static EsAclProbeResult Unverifiable(string observedState) =>
        new(EsAclProbeStatus.Unverifiable, observedState);

    public static EsAclProbeResult Restricted(string observedState) =>
        new(EsAclProbeStatus.Restricted, observedState);

    public static EsAclProbeResult Unrestricted(string observedState) =>
        new(EsAclProbeStatus.Unrestricted, observedState);
}

/// <summary>
/// Default probe used when no Elasticsearch-backed probe is registered — i.e. the
/// projection store runs on the InMemory provider (dev/tests). Reports
/// <see cref="EsAclProbeStatus.Unavailable"/> without claiming a restriction it
/// cannot verify. Warn mode logs the result; Strict mode rejects it.
/// </summary>
public sealed class UnavailableOAuthClientEsAclProbe : IOAuthClientEsAclProbe
{
    public Task<EsAclProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EsAclProbeResult.Unavailable(
            "Elasticsearch is not configured for this host (InMemory projection provider); no cluster ACL to probe."));
    }
}
