using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberScopeWorkflowDescriptorSource
    : IScopeWorkflowPublishedServiceDescriptorSource
{
    private const int PageSize = 200;
    private const int MaxScanCount = 10_000;
    private const int MaxPageCount = MaxScanCount / PageSize + 1;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public StudioMemberScopeWorkflowDescriptorSource(IStudioMemberQueryPort memberQueryPort)
    {
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> ListAsync(
        string scopeId,
        int take,
        CancellationToken ct = default) =>
        ReadAsync(scopeId, Math.Clamp(take, 1, MaxScanCount), workflowId: null, ct);

    public Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> FindByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedWorkflowId = NormalizeRequired(workflowId, nameof(workflowId));
        return ReadAsync(scopeId, MaxScanCount, normalizedWorkflowId, ct);
    }

    private async Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> ReadAsync(
        string scopeId,
        int take,
        string? workflowId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var descriptors = new List<ScopeWorkflowPublishedServiceDescriptor>();
        string? pageToken = null;
        var scanned = 0;
        var pageCount = 0;

        do
        {
            if (++pageCount > MaxPageCount)
                throw new InvalidOperationException($"Studio workflow descriptor scan exceeded {MaxPageCount} pages.");

            var roster = await _memberQueryPort.ListAsync(
                normalizedScopeId,
                new StudioMemberRosterPageRequest(
                    PageSize: Math.Min(PageSize, MaxScanCount - scanned),
                    PageToken: pageToken),
                ct);
            scanned += roster.Members.Count;

            foreach (var member in roster.Members)
            {
                var descriptor = Map(member, normalizedScopeId);
                if (descriptor == null ||
                    (workflowId != null && !string.Equals(descriptor.WorkflowId, workflowId, StringComparison.Ordinal)))
                {
                    continue;
                }

                descriptors.Add(descriptor);
                if (workflowId == null && descriptors.Count >= take)
                    return descriptors;
            }

            var nextPageToken = string.IsNullOrWhiteSpace(roster.NextPageToken)
                ? null
                : roster.NextPageToken;
            if (nextPageToken != null && string.Equals(nextPageToken, pageToken, StringComparison.Ordinal))
                throw new InvalidOperationException("Studio member roster returned a non-advancing page token.");

            pageToken = nextPageToken;
        }
        while (pageToken != null && scanned < MaxScanCount);

        if (pageToken != null)
            throw new InvalidOperationException($"Studio workflow descriptor scan exceeded {MaxScanCount} members.");

        return descriptors;
    }

    private static ScopeWorkflowPublishedServiceDescriptor? Map(
        StudioMemberSummaryResponse member,
        string expectedScopeId)
    {
        var implementationRef = member.ImplementationRef;
        if (!string.Equals(member.ScopeId, expectedScopeId, StringComparison.Ordinal) ||
            !string.Equals(member.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal) ||
            implementationRef == null ||
            !string.Equals(implementationRef.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(implementationRef.WorkflowId) ||
            string.IsNullOrWhiteSpace(member.PublishedServiceId))
        {
            return null;
        }

        var workflowId = implementationRef.WorkflowId!.Trim();
        return new ScopeWorkflowPublishedServiceDescriptor(
            expectedScopeId,
            workflowId,
            StudioMemberPublishedServiceIdentity.AppId,
            StudioMemberPublishedServiceIdentity.Namespace,
            member.PublishedServiceId.Trim(),
            string.IsNullOrWhiteSpace(member.DisplayName) ? workflowId : member.DisplayName.Trim(),
            member.UpdatedAt);
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }
}
