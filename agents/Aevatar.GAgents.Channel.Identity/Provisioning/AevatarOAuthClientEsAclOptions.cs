namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientEsAclOptions
{
    public const string SectionName = "ChannelIdentity:OAuthClient:ElasticsearchAcl";

    /// <summary>
    /// How the startup guard reacts to the Elasticsearch ACL probe result.
    /// <see cref="AevatarOAuthClientEsAclEnforcementMode.Warn"/> (the default)
    /// runs the probe and logs an actionable warning when the
    /// <c>aevatar-oauth-clients</c> read grant cannot be confirmed restricted,
    /// but never blocks startup — ending the pre-probe self-attestation without
    /// turning a misconfigured cluster into a crash loop.
    /// </summary>
    public AevatarOAuthClientEsAclEnforcementMode EnforcementMode { get; set; } =
        AevatarOAuthClientEsAclEnforcementMode.Warn;

    /// <summary>
    /// Operator-declared intent that the <c>aevatar-oauth-clients</c> index read
    /// grant is limited to the same internal services that can read actor events.
    /// This is an attestation, not a verified fact: the probe (see
    /// <see cref="AevatarOAuthClientEsAclEnforcementMode"/>) is what actually
    /// inspects Elasticsearch. <see cref="AevatarOAuthClientEsAclEnforcementMode.Strict"/>
    /// requires both a positive probe result and a declared-true attestation.
    /// </summary>
    public bool GrantMatchesGrainEventStoreInternal { get; set; }

    public string? GrantDescription { get; set; }
}

/// <summary>
/// Controls how <c>AevatarOAuthClientEsAclStartupGuard</c> enforces the
/// Elasticsearch ACL probe result for the <c>aevatar-oauth-clients</c> read grant.
/// </summary>
public enum AevatarOAuthClientEsAclEnforcementMode
{
    /// <summary>The guard performs no probe and no attestation check.</summary>
    Disabled = 0,

    /// <summary>
    /// The guard probes Elasticsearch and logs a warning when the read grant
    /// cannot be confirmed restricted (or the probe is unverifiable/unavailable),
    /// but does not throw. This is the non-breaking default.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// The guard requires the live probe to positively classify the read grant as
    /// restricted and also requires the operator attestation to be true. Every
    /// other probe status fails closed.
    /// </summary>
    Strict = 2,
}
