using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;

namespace Aevatar.GAgentService.Application.Bindings;

/// <summary>
/// Transitional resolver kept only while team invoke still has a platform
/// GAgentService route. It is not actor-owned team authority, and callers
/// must not treat <c>teamId == entryMemberId == publishedServiceId</c> as a
/// durable business fact. Once Team authority lives only in Studio, delete
/// this resolver and require the owning module to provide
/// <see cref="ITeamEntryMemberResolver"/>.
/// </summary>
public sealed class DefaultTeamEntryMemberResolver : ITeamEntryMemberResolver
{
    public Task<TeamEntryMemberResolution> ResolveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedTeamId = NormalizeTeamId(teamId);

        // Transitional compatibility only. This deterministic mapping exists
        // while Team is still migrating out of GAgentService; Studio replaces
        // it with a read-model resolver when Studio owns the team authority.
        return Task.FromResult(new TeamEntryMemberResolution(
            normalizedScopeId,
            normalizedTeamId,
            normalizedTeamId,
            normalizedTeamId));
    }

    private static string NormalizeTeamId(string teamId)
    {
        var normalized = ScopeWorkflowCapabilityOptions.NormalizeRequired(teamId, nameof(teamId));
        if (normalized.IndexOfAny([':', '/', '\\', '?', '#']) >= 0)
            throw new InvalidOperationException("teamId must not contain ':', '/', '\\', '?' or '#'.");

        return normalized;
    }
}
