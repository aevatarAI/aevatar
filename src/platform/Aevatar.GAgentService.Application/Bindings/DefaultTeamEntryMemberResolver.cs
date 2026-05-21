using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;

namespace Aevatar.GAgentService.Application.Bindings;

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

        // TODO: replace this deterministic development resolver with an actor-owned team
        // catalog that defines authoritative entry membership, ownership, and cleanup semantics.
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
