namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Canonical naming for StudioMember actor IDs and the rename-safe
/// <c>publishedServiceId</c> derived from the immutable member id.
///
/// Both helpers are pure functions of their inputs; they do not read any
/// runtime state and they produce no side effects. The <c>publishedServiceId</c>
/// computed here is meant to be persisted on first <c>create_member</c> and
/// then read back from state — never recomputed from a mutable display name.
/// </summary>
public static class StudioMemberConventions
{
    public const string ActorIdPrefix = "studio-member";
    public const string PublishedServiceIdPrefix = "member";

    /// <summary>
    /// Builds the actor id used by <see cref="StudioMemberGAgent"/>.
    /// </summary>
    public static string BuildActorId(string scopeId, string memberId)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var normalizedMemberId = NormalizeMemberId(memberId);
        return $"{ActorIdPrefix}:{normalizedScopeId}:{normalizedMemberId}";
    }

    /// <summary>
    /// Derives the rename-safe published service id from the immutable
    /// <paramref name="memberId"/>. Should be invoked once at creation time
    /// and the result persisted on the actor state — callers must not
    /// recompute it on read.
    /// </summary>
    public static string BuildPublishedServiceId(string memberId)
    {
        var normalizedMemberId = NormalizeMemberId(memberId);
        return $"{PublishedServiceIdPrefix}-{normalizedMemberId}";
    }

    public static string NormalizeScopeId(string? scopeId)
    {
        var trimmed = scopeId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        return trimmed;
    }

    public static string NormalizeMemberId(string? memberId)
    {
        var trimmed = memberId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("memberId is required.", nameof(memberId));
        return trimmed;
    }
}
