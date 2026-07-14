using System.Security.Cryptography;
using Google.Protobuf;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Studio.Application.Authorization;

public sealed class ScheduledInvocationAuthorizationPlanner : IScheduledInvocationAuthorizationPlanner
{
    public const string PolicyVersion = "scheduled-invocation-auth/v1";
    private readonly INyxIdCatalogSnapshotQueryPort _snapshotQueryPort;
    private readonly IScheduledInvocationMemberQueryPort _memberQueryPort;
    private readonly IScheduledInvocationWorkflowQueryPort _workflowQueryPort;
    private readonly IScheduledInvocationConnectorQueryPort _connectorQueryPort;
    private readonly IScheduledInvocationOwnerLLMQueryPort _ownerLLMQueryPort;

    public ScheduledInvocationAuthorizationPlanner(
        INyxIdCatalogSnapshotQueryPort snapshotQueryPort,
        IScheduledInvocationMemberQueryPort memberQueryPort,
        IScheduledInvocationWorkflowQueryPort workflowQueryPort,
        IScheduledInvocationConnectorQueryPort connectorQueryPort,
        IScheduledInvocationOwnerLLMQueryPort ownerLLMQueryPort)
    {
        _snapshotQueryPort = snapshotQueryPort ?? throw new ArgumentNullException(nameof(snapshotQueryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _workflowQueryPort = workflowQueryPort ?? throw new ArgumentNullException(nameof(workflowQueryPort));
        _connectorQueryPort = connectorQueryPort ?? throw new ArgumentNullException(nameof(connectorQueryPort));
        _ownerLLMQueryPort = ownerLLMQueryPort ?? throw new ArgumentNullException(nameof(ownerLLMQueryPort));
    }

    public async Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default)
    {
        var contextFailure = ValidateContext(request);
        if (contextFailure is not null)
            return contextFailure;

        if (request.InvocationTarget.TargetCase != ScheduledInvocationTarget.TargetOneofCase.Studio)
            return await PlanWithAuthorityAsync(request, request.Authority.Clone(), null, ct);

        var target = request.InvocationTarget.Studio;
        var member = await _memberQueryPort.GetAsync(target.ScopeId, target.MemberId, ct);
        if (member is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "member_current_state_not_found");
        if (!string.Equals(member.PublishedServiceId, target.PublishedServiceId, StringComparison.Ordinal) ||
            !string.Equals(member.WorkflowRevision, target.WorkflowRevision, StringComparison.Ordinal))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotStale, "member_current_state_stale");
        }

        var workflow = await _workflowQueryPort.GetAsync(member.WorkflowId, ct);
        if (workflow is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "workflow_current_state_not_found");
        var connector = await _connectorQueryPort.GetAsync(target.ScopeId, ct);
        if (connector is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "connector_current_state_not_found");
        var ownerLLM = await _ownerLLMQueryPort.GetAsync(target.ScopeId, ct);
        if (ownerLLM is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "owner_llm_current_state_not_found");

        var authority = new ScheduledInvocationAuthorizationAuthority
        {
            MemberStateVersion = member.StateVersion,
            WorkflowStateVersion = workflow.StateVersion,
            ConnectorStateVersion = connector.StateVersion,
            OwnerLlmStateVersion = ownerLLM.StateVersion,
        };
        return await PlanWithAuthorityAsync(request, authority, workflow.Dependencies, ct);
    }

    private async Task<ScheduledInvocationAuthorizationPlanResult> PlanWithAuthorityAsync(
        ScheduledInvocationAuthorizationRequest request,
        ScheduledInvocationAuthorizationAuthority authority,
        WorkflowAuthorizationDependencies? workflowDependencies,
        CancellationToken ct)
    {
        var snapshot = await _snapshotQueryPort.GetAsync(request.Owner, ct);
        if (snapshot is null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "nyxid_catalog_snapshot_not_found");
        if (!OwnerEquals(request.Owner, snapshot.Owner))
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "nyxid_catalog_owner_mismatch");
        if (snapshot.FreshUntilUtc <= request.EvaluatedAtUtc)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotStale, "nyxid_catalog_snapshot_stale");

        var serviceGrantsNotRequired = workflowDependencies == null
            ? request.ServiceGrantsNotRequired
            : workflowDependencies.ServiceGrantPolicy == WorkflowServiceGrantPolicy.NotRequiredNoExternalService;
        if (workflowDependencies?.ServiceGrantPolicy == WorkflowServiceGrantPolicy.Unspecified)
            return Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "workflow_service_grant_policy_missing");
        var grantResolution = ResolveServiceGrants(request, workflowDependencies, snapshot);
        if (grantResolution.Failure is not null)
            return grantResolution.Failure;
        var selected = grantResolution.Grants;
        if (selected.Count == 0 && !serviceGrantsNotRequired)
            return Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, "nyxid_service_grants_empty");

        var plan = new ScheduledInvocationAuthorizationPlan
        {
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                Scopes = "read proxy",
                AllowAllServices = false,
                AllowAllNodes = false,
                ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(request.ExpiresAtUtc),
                PolicyVersion = PolicyVersion,
                ServiceGrantsNotRequired = serviceGrantsNotRequired,
            },
            Authority = authority,
            Disclosure = new ScheduledInvocationAuthorizationDisclosure
            {
                DedicatedToSchedule = true,
                SecretManagedByAevatar = true,
                BrowserReceivesRawKey = false,
                DeleteRevokesCredential = true,
                PauseResumeRevokesCredential = false,
            },
        };
        plan.Authority.CatalogStateVersion = snapshot.StateVersion;
        plan.Authority.CatalogObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc);
        plan.Authority.CatalogFreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc);
        plan.Authority.CatalogExternalRevision = snapshot.ExternalRevision;
        plan.Authority.CatalogContentDigest = snapshot.ContentDigest;
        plan.NyxIdServiceGrants.Add(selected);
        plan.PermissionDigest = ComputeDigest(plan);
        return ScheduledInvocationAuthorizationPlanResult.Succeeded(plan);
    }

    private static ServiceGrantResolution ResolveServiceGrants(
        ScheduledInvocationAuthorizationRequest request,
        WorkflowAuthorizationDependencies? workflowDependencies,
        NyxIdCatalogSnapshot snapshot)
    {
        var services = snapshot.Services.ToDictionary(static service => service.UserServiceId, StringComparer.Ordinal);
        var requiredServiceIds = workflowDependencies?.NyxIdServiceIds.ToList() ?? request.RequiredNyxIdServiceIds.ToList();
        foreach (var slug in (workflowDependencies?.NyxIdServiceSlugs ?? request.RequiredNyxIdServiceSlugs)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Select(static value => value.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            var matches = snapshot.Services
                .Where(service => string.Equals(service.ServiceSlug, slug, StringComparison.Ordinal))
                .Select(static service => service.UserServiceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matches.Length != 1)
                return ServiceGrantResolution.Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, $"nyxid_service_slug_not_unique:{slug}");
            requiredServiceIds.Add(matches[0]);
        }

        var selected = new List<NyxIdServiceGrant>();
        foreach (var serviceId in requiredServiceIds.Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Select(static id => id.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!services.TryGetValue(serviceId, out var service))
                return ServiceGrantResolution.Failed(ScheduledInvocationAuthorizationFailureCode.ServiceNotFound, $"nyxid_service_not_found:{serviceId}");
            if (snapshot.UnreachableServiceIds?.Contains(serviceId) == true)
                return ServiceGrantResolution.Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, $"nyxid_service_unreachable:{serviceId}");
            if (!service.NodeGrantsNotRequired && service.NodeGrants.Count == 0)
                return ServiceGrantResolution.Failed(ScheduledInvocationAuthorizationFailureCode.NodeGrantMissing, $"nyxid_node_grant_missing:{serviceId}");
            selected.Add(Normalize(service));
        }

        return new ServiceGrantResolution(selected, null);
    }

    private sealed record ServiceGrantResolution(
        IReadOnlyList<NyxIdServiceGrant> Grants,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static ServiceGrantResolution Failed(ScheduledInvocationAuthorizationFailureCode code, string detail) =>
            new([], ScheduledInvocationAuthorizationPlanResult.Failed(code, detail));
    }

    private static ScheduledInvocationAuthorizationPlanResult? ValidateContext(
        ScheduledInvocationAuthorizationRequest request)
    {
        if (request.InvocationTarget.TargetCase == ScheduledInvocationTarget.TargetOneofCase.None)
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "invocation_target_missing");
        if (request.OwnerContext.Owner == null ||
            string.IsNullOrWhiteSpace(request.Owner.Authority) ||
            request.Owner.OwnerKind == NyxIdCatalogOwnerKind.Unspecified ||
            string.IsNullOrWhiteSpace(request.Owner.OwnerSubject) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectPlatform) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectExternalUserId) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.VerifiedBindingId))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "authenticated_owner_context_incomplete");
        }

        return null;
    }

    public static string ComputeDigest(ScheduledInvocationAuthorizationPlan plan)
    {
        var canonical = plan.Clone();
        canonical.PermissionDigest = string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    private static NyxIdServiceGrant Normalize(NyxIdServiceGrant service)
    {
        var normalized = new NyxIdServiceGrant
        {
            UserServiceId = service.UserServiceId.Trim(),
            DisplayName = service.DisplayName.Trim(),
            NodeGrantsNotRequired = service.NodeGrantsNotRequired,
            ServiceSlug = service.ServiceSlug.Trim(),
        };
        normalized.NodeGrants.Add(service.NodeGrants
            .OrderByDescending(static node => node.Primary)
            .ThenBy(static node => node.NodeId, StringComparer.Ordinal)
            .Select(static node => new NyxIdNodeGrant
            {
                NodeId = node.NodeId.Trim(),
                DisplayName = node.DisplayName.Trim(),
                Primary = node.Primary,
            }));
        return normalized;
    }

    private static bool OwnerEquals(NyxIdCatalogOwnerIdentity left, NyxIdCatalogOwnerIdentity right) =>
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode code,
        string detail) => ScheduledInvocationAuthorizationPlanResult.Failed(code, detail);
}
