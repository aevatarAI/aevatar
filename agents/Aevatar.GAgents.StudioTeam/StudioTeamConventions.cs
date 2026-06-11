namespace Aevatar.GAgents.StudioTeam;

/// <summary>
/// Canonical naming for StudioTeam actor IDs. Mirrors
/// <c>StudioMemberConventions</c> in shape so the two agent families are
/// interchangeable as far as identity normalization is concerned.
///
/// All helpers are pure functions of their inputs; they do not read any
/// runtime state and they produce no side effects.
/// </summary>
public static class StudioTeamConventions
{
    public const string ActorIdPrefix = "studio-team";

    /// <summary>
    /// Builds the actor id used by <see cref="StudioTeamGAgent"/>.
    /// </summary>
    public static string BuildActorId(string scopeId, string teamId)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var normalizedTeamId = NormalizeTeamId(teamId);
        return $"{ActorIdPrefix}:{normalizedScopeId}:{normalizedTeamId}";
    }

    public static string NormalizeScopeId(string? scopeId)
    {
        var trimmed = scopeId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (ContainsActorIdSeparator(trimmed))
            throw new ArgumentException(
                "scopeId must not contain ':' (it is the actor-id separator).", nameof(scopeId));
        return trimmed;
    }

    public static string NormalizeTeamId(string? teamId)
    {
        var trimmed = teamId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("teamId is required.", nameof(teamId));
        if (ContainsActorIdSeparator(trimmed))
            throw new ArgumentException(
                "teamId must not contain ':' (it is the actor-id separator).", nameof(teamId));
        return trimmed;
    }

    private static bool ContainsActorIdSeparator(string value) => value.Contains(':');
}
