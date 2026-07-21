using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public sealed class ScheduledInvocationAuthorizationPlanner : IScheduledInvocationAuthorizationPlanner
{
    public const string SchemaVersion = ScheduledInvocationAuthorizationContractVersions.Schema;
    public const string PolicyVersion = ScheduledInvocationAuthorizationContractVersions.CredentialPolicy;

    private readonly INyxIdAuthorizationCatalogQueryPort _catalogQueryPort;
    private readonly IScheduledInvocationMemberEvidenceQueryPort _memberQueryPort;
    private readonly IScheduledInvocationWorkflowEvidenceQueryPort _workflowQueryPort;
    private readonly IScheduledInvocationConnectorEvidenceQueryPort _connectorQueryPort;
    private readonly IScheduledInvocationOwnerLLMEvidenceQueryPort _ownerLLMQueryPort;

    public ScheduledInvocationAuthorizationPlanner(
        INyxIdAuthorizationCatalogQueryPort catalogQueryPort,
        IScheduledInvocationMemberEvidenceQueryPort? memberQueryPort = null,
        IScheduledInvocationWorkflowEvidenceQueryPort? workflowQueryPort = null,
        IScheduledInvocationConnectorEvidenceQueryPort? connectorQueryPort = null,
        IScheduledInvocationOwnerLLMEvidenceQueryPort? ownerLLMQueryPort = null)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _memberQueryPort = memberQueryPort ?? UnavailableTargetEvidenceQueryPorts.Instance;
        _workflowQueryPort = workflowQueryPort ?? UnavailableTargetEvidenceQueryPorts.Instance;
        _connectorQueryPort = connectorQueryPort ?? UnavailableTargetEvidenceQueryPorts.Instance;
        _ownerLLMQueryPort = ownerLLMQueryPort ?? UnavailableTargetEvidenceQueryPorts.Instance;
    }

    public async Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestFailure = ValidateRequest(request);
        if (requestFailure != null)
            return requestFailure;

        var evidence = await ResolveTargetEvidenceAsync(request, ct);
        if (evidence.Failure != null)
            return evidence.Failure;

        var snapshot = await _catalogQueryPort.GetAsync(request.Owner, ct);
        if (snapshot == null)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "nyxid_catalog_snapshot_not_found");
        if (!OwnerEquals(request.Owner, snapshot.Owner))
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerMismatch, "nyxid_catalog_owner_mismatch");
        if (snapshot.Invalidated)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound, "nyxid_catalog_snapshot_invalidated");
        if (snapshot.ObservedAtUtc > request.EvaluatedAtUtc || snapshot.FreshUntilUtc <= request.EvaluatedAtUtc)
            return Failed(ScheduledInvocationAuthorizationFailureCode.SnapshotStale, "nyxid_catalog_snapshot_stale");
        if (string.IsNullOrWhiteSpace(snapshot.ContractVersion) ||
            string.IsNullOrWhiteSpace(snapshot.PolicyVersion) ||
            snapshot.EvaluatedAtUtc == default)
        {
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_contract_evidence_invalid");
        }

        var grants = ResolveGrants(
            evidence.RequiredServiceIds,
            evidence.RequiredServiceSlugs,
            evidence.ServiceGrantRequirement,
            snapshot.Services);
        if (grants.Failure != null)
            return grants.Failure;

        var plan = new ScheduledInvocationAuthorizationPlan
        {
            SchemaVersion = SchemaVersion,
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                AllowAllServices = false,
                AllowAllNodes = false,
                ServiceGrantRequirement = evidence.ServiceGrantRequirement,
                NodeGrantRequirement = grants.ServiceGrants.Any(static grant =>
                    grant.NodeGrantRequirement == AuthorizationGrantRequirement.Required)
                        ? AuthorizationGrantRequirement.Required
                        : AuthorizationGrantRequirement.NotRequired,
                ExpiresAt = Timestamp.FromDateTimeOffset(request.ExpiresAtUtc),
                PolicyVersion = PolicyVersion,
            },
            CatalogAuthority = new NyxIdCatalogAuthorityStamp
            {
                ActorStateVersion = snapshot.StateVersion,
                ObservedAt = Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc),
                FreshUntil = Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc),
                ContentDigest = snapshot.ContentDigest,
                ContractVersion = snapshot.ContractVersion,
                PolicyVersion = snapshot.PolicyVersion,
                EvaluatedAt = Timestamp.FromDateTimeOffset(snapshot.EvaluatedAtUtc),
            },
        };
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Read);
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Proxy);
        plan.NyxIdServiceGrants.Add(grants.ServiceGrants);
        plan.SourceStamps.Add(evidence.SourceStamps);
        plan.Disclosures.Add(new[]
        {
            ScheduledInvocationDisclosure.DedicatedCredential,
            ScheduledInvocationDisclosure.AevatarSecretCustody,
            ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
            ScheduledInvocationDisclosure.DeleteRevokesCredential,
            ScheduledInvocationDisclosure.PauseResumePreservesCredential,
            ScheduledInvocationDisclosure.NodeIdsArePermissionSet,
        });
        plan.PermissionDigest = ComputeDigest(plan);
        return ScheduledInvocationAuthorizationPlanResult.Succeeded(plan);
    }

    public static string ComputeDigest(ScheduledInvocationAuthorizationPlan plan)
        => ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

    private async Task<TargetEvidenceResolution> ResolveTargetEvidenceAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct)
    {
        if (request.InvocationTarget.TargetCase != ScheduledInvocationTarget.TargetOneofCase.StudioMember)
        {
            var directServiceIds = NormalizeStrings(request.RequiredNyxIdServiceIds).ToList();
            var directServiceSlugs = NormalizeStrings(request.RequiredNyxIdServiceSlugs).ToList();
            var directGrantRequirement = request.ServiceGrantRequirement;
            var directSourceStamps = (request.SourceStamps ?? []).Select(static stamp => stamp.Clone()).ToList();
            var ownerLLMScopeId = request.InvocationTarget.TargetCase ==
                                  ScheduledInvocationTarget.TargetOneofCase.ScheduledAgent
                ? request.InvocationTarget.ScheduledAgent.ExecutionScopeId.Trim()
                : string.Empty;
            if (ownerLLMScopeId.Length > 0)
            {
                var ownerLLM = await ResolveOwnerLLMEvidenceAsync(
                    ownerLLMScopeId,
                    directServiceIds,
                    directServiceSlugs,
                    directGrantRequirement,
                    directSourceStamps,
                    ct);
                if (ownerLLM.Failure != null)
                    return TargetEvidenceResolution.Failed(ownerLLM.Failure);
                directGrantRequirement = ownerLLM.ServiceGrantRequirement;
            }

            var stamps = CanonicalizeSourceStamps(directSourceStamps);
            if (stamps.Failure != null)
                return TargetEvidenceResolution.Failed(stamps.Failure);
            return new TargetEvidenceResolution(
                directServiceIds,
                directServiceSlugs,
                directGrantRequirement,
                stamps.SourceStamps,
                null);
        }

        var target = request.InvocationTarget.StudioMember;
        var member = await _memberQueryPort.GetAsync(target.ScopeId, target.MemberId, ct);
        if (member == null)
            return TargetEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "studio_member_evidence_not_found"));
        if (!string.Equals(member.DraftWorkflowId, target.DraftWorkflowId, StringComparison.Ordinal) ||
            !string.Equals(member.WorkflowRevisionId, target.WorkflowRevisionId, StringComparison.Ordinal) ||
            !string.Equals(member.PublishedServiceId, target.PublishedServiceId, StringComparison.Ordinal))
        {
            return TargetEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                "studio_member_evidence_changed"));
        }

        var workflow = await _workflowQueryPort.GetAsync(
            target.ScopeId,
            member.PublishedServiceId,
            member.WorkflowRevisionId,
            ct);
        if (workflow == null)
            return TargetEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "workflow_authorization_evidence_not_found"));
        if (workflow.ServiceGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(workflow.ServiceGrantRequirement))
        {
            return TargetEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.UnknownEnum,
                "workflow_service_grant_requirement_invalid"));
        }

        var requiredServiceIds = NormalizeStrings(workflow.NyxIdServiceIds).ToList();
        var requiredServiceSlugs = NormalizeStrings(workflow.NyxIdServiceSlugs).ToList();
        var serviceGrantRequirement = workflow.ServiceGrantRequirement;
        var sourceStamps = new List<AuthorizationSourceStamp>
        {
            new()
            {
                SourceKind = AuthorizationSourceKind.StudioMember,
                SourceId = target.MemberId,
                StateVersion = member.StateVersion,
            },
            new()
            {
                SourceKind = AuthorizationSourceKind.WorkflowRevision,
                SourceId = target.WorkflowRevisionId,
                StateVersion = workflow.StateVersion,
            },
        };

        var connectorRefs = NormalizeStrings(workflow.ConnectorCapabilityRefs);
        if (connectorRefs.Length > 0)
        {
            var connector = await _connectorQueryPort.GetAsync(target.ScopeId, ct);
            if (connector == null)
            {
                return TargetEvidenceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                    "connector_authorization_evidence_not_found"));
            }

            var availableConnectors = connector.ConnectorCapabilityRefs
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingConnector = connectorRefs.FirstOrDefault(reference => !availableConnectors.Contains(reference));
            if (missingConnector != null)
            {
                return TargetEvidenceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                    $"connector_authorization_evidence_not_found:{missingConnector}"));
            }

            sourceStamps.Add(new AuthorizationSourceStamp
            {
                SourceKind = AuthorizationSourceKind.ConnectorCatalog,
                SourceId = target.ScopeId,
                StateVersion = connector.StateVersion,
            });
        }

        if (workflow.OwnerLLMRouteRequired)
        {
            var ownerLLM = await ResolveOwnerLLMEvidenceAsync(
                target.ScopeId,
                requiredServiceIds,
                requiredServiceSlugs,
                serviceGrantRequirement,
                sourceStamps,
                ct);
            if (ownerLLM.Failure != null)
                return TargetEvidenceResolution.Failed(ownerLLM.Failure);
            serviceGrantRequirement = ownerLLM.ServiceGrantRequirement;
        }

        sourceStamps.AddRange(request.SourceStamps ?? []);
        var canonicalStamps = CanonicalizeSourceStamps(sourceStamps);
        if (canonicalStamps.Failure != null)
            return TargetEvidenceResolution.Failed(canonicalStamps.Failure);
        return new TargetEvidenceResolution(
            requiredServiceIds,
            requiredServiceSlugs,
            serviceGrantRequirement,
            canonicalStamps.SourceStamps,
            null);
    }

    private async Task<OwnerLLMEvidenceResolution> ResolveOwnerLLMEvidenceAsync(
        string scopeId,
        List<string> requiredServiceIds,
        List<string> requiredServiceSlugs,
        AuthorizationGrantRequirement serviceGrantRequirement,
        List<AuthorizationSourceStamp> sourceStamps,
        CancellationToken ct)
    {
        var ownerLLM = await _ownerLLMQueryPort.GetAsync(scopeId, ct);
        if (ownerLLM == null)
        {
            return OwnerLLMEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "owner_llm_authorization_evidence_not_found"));
        }
        if (ownerLLM.ServiceGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(ownerLLM.ServiceGrantRequirement))
        {
            return OwnerLLMEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.UnknownEnum,
                "owner_llm_service_grant_requirement_invalid"));
        }

        var ownerServiceId = ownerLLM.NyxIdServiceId?.Trim() ?? string.Empty;
        var ownerServiceSlug = ownerLLM.NyxIdServiceSlug?.Trim() ?? string.Empty;
        if (ownerLLM.ServiceGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            if ((ownerServiceId.Length == 0) == (ownerServiceSlug.Length == 0))
            {
                return OwnerLLMEvidenceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous,
                    "owner_llm_service_identity_invalid"));
            }
            if (ownerServiceId.Length > 0)
                requiredServiceIds.Add(ownerServiceId);
            else
                requiredServiceSlugs.Add(ownerServiceSlug);
            serviceGrantRequirement = AuthorizationGrantRequirement.Required;
        }
        else if (ownerServiceId.Length > 0 || ownerServiceSlug.Length > 0)
        {
            return OwnerLLMEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.UnknownEnum,
                "owner_llm_direct_route_has_service_identity"));
        }

        sourceStamps.Add(new AuthorizationSourceStamp
        {
            SourceKind = AuthorizationSourceKind.OwnerLlmRoute,
            SourceId = scopeId,
            StateVersion = ownerLLM.StateVersion,
        });
        return new OwnerLLMEvidenceResolution(serviceGrantRequirement, null);
    }

    private static GrantResolution ResolveGrants(
        IReadOnlyList<string> requiredServiceIds,
        IReadOnlyList<string> requiredServiceSlugs,
        AuthorizationGrantRequirement serviceGrantRequirement,
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> services)
    {
        var servicesById = new Dictionary<string, NyxIdAuthorizationServiceEvidence>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (!TryValidateServiceEvidence(service, out var detail))
                return GrantResolution.Failed(Failed(ScheduledInvocationAuthorizationFailureCode.UnknownEnum, detail));
            if (!servicesById.TryAdd(service.UserServiceId.Trim(), service))
            {
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous,
                    $"nyxid_service_identity_ambiguous:{service.UserServiceId.Trim()}"));
            }
        }

        var selectedIds = new List<string>(requiredServiceIds);
        foreach (var slug in requiredServiceSlugs)
        {
            var matches = services.Where(service =>
                    string.Equals(service.ServiceSlug.Trim(), slug, StringComparison.Ordinal))
                .Select(static service => service.UserServiceId.Trim())
                .ToArray();
            if (matches.Length == 0)
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceNotFound,
                    $"nyxid_service_slug_not_found:{slug}"));
            if (matches.Length != 1)
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous,
                    $"nyxid_service_slug_ambiguous:{slug}"));
            selectedIds.Add(matches[0]);
        }
        selectedIds = NormalizeStrings(selectedIds).ToList();

        if (selectedIds.Count == 0 && serviceGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            return GrantResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.ServiceNotFound,
                "nyxid_service_grants_empty"));
        }

        var serviceGrants = new List<NyxIdServiceGrant>();
        foreach (var serviceId in selectedIds)
        {
            if (!servicesById.TryGetValue(serviceId, out var service))
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceNotFound,
                    $"nyxid_service_not_found:{serviceId}"));
            if (service.Access != NyxIdAuthorizationAccess.Permitted)
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied,
                    $"nyxid_service_access_denied:{serviceId}"));

            var grant = new NyxIdServiceGrant
            {
                UserServiceId = serviceId,
                ServiceSlug = service.ServiceSlug.Trim(),
                DisplayName = service.DisplayName.Trim(),
                ResourceOwner = service.ResourceOwner.Clone(),
                NodeGrantRequirement = service.NodeGrantRequirement,
            };
            grant.NodeIds.Add(service.NodeIds);
            serviceGrants.Add(grant);
        }
        return new GrantResolution(serviceGrants, null);
    }

    private static bool TryValidateServiceEvidence(
        NyxIdAuthorizationServiceEvidence service,
        out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(service.UserServiceId) ||
            !string.Equals(service.UserServiceId, service.UserServiceId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.ServiceSlug) ||
            !string.Equals(service.ServiceSlug, service.ServiceSlug.Trim(), StringComparison.Ordinal) ||
            service.Access == NyxIdAuthorizationAccess.Unspecified ||
            !Enum.IsDefined(service.Access) ||
            service.NodeGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(service.NodeGrantRequirement))
        {
            detail = "nyxid_service_evidence_invalid";
            return false;
        }

        if (!TryValidateOwner(service.ResourceOwner))
        {
            detail = $"nyxid_resource_owner_invalid:{service.UserServiceId}";
            return false;
        }

        string? previousNodeId = null;
        foreach (var nodeId in service.NodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId) ||
                !string.Equals(nodeId, nodeId.Trim(), StringComparison.Ordinal) ||
                previousNodeId != null && string.CompareOrdinal(previousNodeId, nodeId) >= 0)
            {
                detail = $"nyxid_node_ids_not_canonical:{service.UserServiceId}";
                return false;
            }
            previousNodeId = nodeId;
        }

        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.Required &&
            service.NodeIds.Count == 0)
        {
            detail = $"nyxid_node_grant_missing:{service.UserServiceId}";
            return false;
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired && service.NodeIds.Count != 0)
        {
            detail = $"nyxid_direct_service_has_node_evidence:{service.UserServiceId}";
            return false;
        }
        return true;
    }

    private static ScheduledInvocationAuthorizationPlanResult? ValidateRequest(
        ScheduledInvocationAuthorizationRequest request)
    {
        if (request.InvocationTarget == null ||
            request.InvocationTarget.TargetCase == ScheduledInvocationTarget.TargetOneofCase.None ||
            !ValidateTarget(request.InvocationTarget))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.TargetInvalid, "invocation_target_invalid");
        }
        if (request.OwnerContext?.Owner == null ||
            string.IsNullOrWhiteSpace(request.Owner.Authority) ||
            request.Owner.OwnerKind == AuthorizationOwnerKind.Unspecified ||
            !Enum.IsDefined(request.Owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(request.Owner.OwnerSubject) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectPlatform) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.SubjectExternalUserId) ||
            string.IsNullOrWhiteSpace(request.OwnerContext.VerifiedBindingId))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.OwnerInvalid, "authenticated_owner_context_incomplete");
        }
        if (request.ServiceGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(request.ServiceGrantRequirement))
        {
            return Failed(ScheduledInvocationAuthorizationFailureCode.UnknownEnum, "service_grant_requirement_invalid");
        }
        if (request.ExpiresAtUtc <= request.EvaluatedAtUtc)
            return Failed(ScheduledInvocationAuthorizationFailureCode.TargetInvalid, "credential_expiry_invalid");
        return null;
    }

    private static bool ValidateTarget(ScheduledInvocationTarget target) => target.TargetCase switch
    {
        ScheduledInvocationTarget.TargetOneofCase.StudioMember =>
            !string.IsNullOrWhiteSpace(target.StudioMember.ScopeId) &&
            !string.IsNullOrWhiteSpace(target.StudioMember.TeamId) &&
            !string.IsNullOrWhiteSpace(target.StudioMember.MemberId) &&
            !string.IsNullOrWhiteSpace(target.StudioMember.PublishedServiceId) &&
            !string.IsNullOrWhiteSpace(target.StudioMember.DraftWorkflowId) &&
            !string.IsNullOrWhiteSpace(target.StudioMember.WorkflowRevisionId),
        ScheduledInvocationTarget.TargetOneofCase.ScheduledAgent =>
            !string.IsNullOrWhiteSpace(target.ScheduledAgent.RegistrationScopeId) &&
            !string.IsNullOrWhiteSpace(target.ScheduledAgent.ExecutionScopeId) &&
            !string.IsNullOrWhiteSpace(target.ScheduledAgent.ScheduledAgentId),
        ScheduledInvocationTarget.TargetOneofCase.Delivery =>
            !string.IsNullOrWhiteSpace(target.Delivery.RegistrationScopeId) &&
            !string.IsNullOrWhiteSpace(target.Delivery.DeliveryTargetId),
        _ => false,
    };

    private static SourceStampResolution CanonicalizeSourceStamps(
        IEnumerable<AuthorizationSourceStamp> stamps)
    {
        var normalizedStamps = new List<AuthorizationSourceStamp>();
        var identities = new HashSet<(AuthorizationSourceKind Kind, string Id)>();
        foreach (var stamp in stamps)
        {
            if (stamp == null ||
                stamp.SourceKind == AuthorizationSourceKind.Unspecified ||
                !Enum.IsDefined(stamp.SourceKind) ||
                string.IsNullOrWhiteSpace(stamp.SourceId) ||
                stamp.StateVersion < 0)
            {
                return SourceStampResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.UnknownEnum,
                    "authorization_source_stamp_invalid"));
            }
            var normalized = stamp.Clone();
            normalized.SourceId = normalized.SourceId.Trim();
            normalized.ContentDigest = normalized.ContentDigest.Trim();
            var key = (normalized.SourceKind, normalized.SourceId);
            if (!identities.Add(key))
            {
                return SourceStampResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                    $"authorization_source_stamp_conflict:{normalized.SourceKind}:{normalized.SourceId}"));
            }
            normalizedStamps.Add(normalized);
        }
        return new SourceStampResolution(normalizedStamps, null);
    }

    private static string[] NormalizeStrings(IEnumerable<string> values) =>
        values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool TryValidateOwner(AuthorizationOwnerIdentity? owner) =>
        owner != null &&
        !string.IsNullOrWhiteSpace(owner.Authority) &&
        string.Equals(owner.Authority, owner.Authority.Trim(), StringComparison.Ordinal) &&
        owner.OwnerKind != AuthorizationOwnerKind.Unspecified &&
        Enum.IsDefined(owner.OwnerKind) &&
        !string.IsNullOrWhiteSpace(owner.OwnerSubject) &&
        string.Equals(owner.OwnerSubject, owner.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool OwnerEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority.Trim(), right.Authority.Trim(), StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject.Trim(), right.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode code,
        string detail) => ScheduledInvocationAuthorizationPlanResult.Failed(code, detail);

    private sealed record TargetEvidenceResolution(
        IReadOnlyList<string> RequiredServiceIds,
        IReadOnlyList<string> RequiredServiceSlugs,
        AuthorizationGrantRequirement ServiceGrantRequirement,
        IReadOnlyList<AuthorizationSourceStamp> SourceStamps,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static TargetEvidenceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], [], AuthorizationGrantRequirement.Unspecified, [], failure);
    }

    private sealed record GrantResolution(
        IReadOnlyList<NyxIdServiceGrant> ServiceGrants,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static GrantResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], failure);
    }

    private sealed record SourceStampResolution(
        IReadOnlyList<AuthorizationSourceStamp> SourceStamps,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static SourceStampResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], failure);
    }

    private sealed record OwnerLLMEvidenceResolution(
        AuthorizationGrantRequirement ServiceGrantRequirement,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static OwnerLLMEvidenceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new(AuthorizationGrantRequirement.Unspecified, failure);
    }

    private sealed class UnavailableTargetEvidenceQueryPorts :
        IScheduledInvocationMemberEvidenceQueryPort,
        IScheduledInvocationWorkflowEvidenceQueryPort,
        IScheduledInvocationConnectorEvidenceQueryPort,
        IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public static readonly UnavailableTargetEvidenceQueryPorts Instance = new();

        Task<ScheduledInvocationMemberEvidence?> IScheduledInvocationMemberEvidenceQueryPort.GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationMemberEvidence?>(null);

        Task<ScheduledInvocationWorkflowEvidence?> IScheduledInvocationWorkflowEvidenceQueryPort.GetAsync(
            string scopeId,
            string publishedServiceId,
            string workflowRevisionId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationWorkflowEvidence?>(null);

        Task<ScheduledInvocationConnectorEvidence?> IScheduledInvocationConnectorEvidenceQueryPort.GetAsync(
            string scopeId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationConnectorEvidence?>(null);

        Task<ScheduledInvocationOwnerLLMEvidence?> IScheduledInvocationOwnerLLMEvidenceQueryPort.GetAsync(
            string scopeId,
            CancellationToken ct) => Task.FromResult<ScheduledInvocationOwnerLLMEvidence?>(null);
    }
}
