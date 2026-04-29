using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Helpers for constructing actor identifiers and validating
/// <see cref="ExternalSubjectRef"/> instances at module boundaries.
/// </summary>
public static class ExternalSubjectRefExtensions
{
    /// <summary>
    /// Prefix for actor IDs of <c>ExternalIdentityBindingGAgent</c>.
    /// Mirrors the <c>channel-conversation:</c> prefix used by ConversationGAgent
    /// so actor types are immediately recognizable in logs.
    /// </summary>
    public const string ActorIdPrefix = "external-identity-binding";

    /// <summary>
    /// Builds the deterministic actor id for an external subject. Caller MUST
    /// ensure the fields are normalized (no separator characters, see
    /// <see cref="EnsureValid"/>); the builder colon-joins them as-is.
    /// </summary>
    public static string ToActorId(this ExternalSubjectRef externalSubject)
    {
        ArgumentNullException.ThrowIfNull(externalSubject);
        EnsureValid(externalSubject);
        return $"{ActorIdPrefix}:{externalSubject.Platform}:{externalSubject.Tenant ?? string.Empty}:{externalSubject.ExternalUserId}";
    }

    /// <summary>
    /// Throws when <paramref name="externalSubject"/> is missing required fields
    /// or contains the actor-id separator (<c>:</c>) in a field value.
    /// </summary>
    public static void EnsureValid(ExternalSubjectRef externalSubject)
    {
        ArgumentNullException.ThrowIfNull(externalSubject);
        if (string.IsNullOrWhiteSpace(externalSubject.Platform))
            throw new ArgumentException("ExternalSubjectRef.platform is required.", nameof(externalSubject));
        if (string.IsNullOrWhiteSpace(externalSubject.ExternalUserId))
            throw new ArgumentException("ExternalSubjectRef.external_user_id is required.", nameof(externalSubject));

        // Tenant is allowed to be null/empty (some platforms have no tenant
        // scope) but the colon-separator invariant still applies when set.
        // Coalesce defensively so manually-constructed protos with a null
        // Tenant don't NRE here.
        var tenant = externalSubject.Tenant ?? string.Empty;
        if (externalSubject.Platform.Contains(':', StringComparison.Ordinal)
            || tenant.Contains(':', StringComparison.Ordinal)
            || externalSubject.ExternalUserId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ExternalSubjectRef fields must not contain ':' (used as actor-id separator).",
                nameof(externalSubject));
        }
    }
}
