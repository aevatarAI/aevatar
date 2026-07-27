using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkOrderAssignmentValidator
{
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IScopeBindingReadinessQueryPort _readinessQueryPort;

    public WorkOrderAssignmentValidator(
        IStudioTeamQueryPort teamQueryPort,
        IStudioMemberQueryPort memberQueryPort,
        IScopeBindingReadinessQueryPort readinessQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _readinessQueryPort = readinessQueryPort ?? throw new ArgumentNullException(nameof(readinessQueryPort));
    }

    public async Task<WorkOrderValidatedAssignment> ValidateAsync(
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        string endpointId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedTeamId = NormalizeRequired(teamId, nameof(teamId));
        var normalizedMemberId = NormalizeRequired(memberId, nameof(memberId));
        var normalizedServiceId = NormalizeRequired(publishedServiceId, nameof(publishedServiceId));
        var normalizedEndpointId = NormalizeRequired(endpointId, nameof(endpointId));

        var team = await _teamQueryPort.GetAsync(normalizedScopeId, normalizedTeamId, ct);
        if (team == null || !string.Equals(team.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder Team was not found in the requested Scope.");
        if (!string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Active, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder Team is not active.");

        var member = await _memberQueryPort.GetAsync(normalizedScopeId, normalizedMemberId, ct);
        if (member == null || !string.Equals(member.Summary.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder member was not found in the requested Scope.");
        if (!string.Equals(member.Summary.TeamId, normalizedTeamId, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder member does not belong to the requested Team.");
        if (!string.Equals(member.Summary.PublishedServiceId, normalizedServiceId, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder publishedServiceId does not match the member read model.");
        if (!string.Equals(member.Summary.LifecycleStage, MemberLifecycleStageNames.BindReady, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder member is not bind-ready.");

        var binding = member.LastBinding;
        if (binding == null ||
            !string.Equals(binding.PublishedServiceId, normalizedServiceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(binding.RevisionId))
        {
            throw new InvalidOperationException(
                "WorkOrder member has no authoritative published-service binding for the requested service.");
        }

        var readiness = await _readinessQueryPort.GetReadinessAsync(
            new ScopeBindingReadinessRequest(
                normalizedScopeId,
                normalizedServiceId,
                ExpectedRevisionId: binding.RevisionId,
                ExpectedEndpointIds: [normalizedEndpointId]),
            ct);
        if (!readiness.InvokeReady || readiness.Status != ScopeBindingReadinessStatus.Ready)
        {
            throw new InvalidOperationException(
                $"WorkOrder published service is not callable: {readiness.Status}.");
        }
        if (!string.Equals(readiness.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new InvalidOperationException("WorkOrder service readiness points to a stale revision.");

        var workflowId = string.Equals(
                member.Summary.ImplementationKind,
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal)
            ? NormalizeOptional(member.ImplementationRef?.WorkflowId)
            : null;
        if (string.Equals(
                member.Summary.ImplementationKind,
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(workflowId))
        {
            throw new InvalidOperationException("Workflow member has no authoritative workflow identity.");
        }

        return new WorkOrderValidatedAssignment(
            normalizedMemberId,
            normalizedServiceId,
            workflowId,
            binding.RevisionId.Trim(),
            NormalizeRequired(member.Summary.ImplementationKind, nameof(member.Summary.ImplementationKind)));
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException($"{fieldName} is required.")
            : normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
