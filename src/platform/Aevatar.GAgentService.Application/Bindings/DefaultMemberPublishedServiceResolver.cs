using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;

namespace Aevatar.GAgentService.Application.Bindings;

public sealed class DefaultMemberPublishedServiceResolver : IMemberPublishedServiceResolver
{
    public Task<MemberPublishedServiceResolution> ResolveAsync(
        MemberPublishedServiceResolveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var memberId = NormalizeMemberId(request.MemberId);

        // TODO: replace this deterministic development resolver with an actor-owned member
        // catalog that defines authoritative membership, ownership, and cleanup semantics.
        return Task.FromResult(new MemberPublishedServiceResolution(
            scopeId,
            memberId,
            memberId));
    }

    private static string NormalizeMemberId(string memberId)
    {
        var normalized = ScopeWorkflowCapabilityOptions.NormalizeRequired(memberId, nameof(MemberPublishedServiceResolveRequest.MemberId));
        if (normalized.IndexOfAny([':', '/', '\\', '?', '#']) >= 0)
            throw new InvalidOperationException("memberId must not contain ':', '/', '\\', '?' or '#'.");

        return normalized;
    }
}
