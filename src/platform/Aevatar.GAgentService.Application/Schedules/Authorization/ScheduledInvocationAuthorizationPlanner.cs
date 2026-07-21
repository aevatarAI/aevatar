using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
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

        var grants = ResolveGrants(
            evidence.RequiredServices,
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
                NodeGrantRequirement = grants.NodeGrants.Count == 0
                    ? AuthorizationGrantRequirement.NotRequired
                    : AuthorizationGrantRequirement.Required,
                ExpiresAt = Timestamp.FromDateTimeOffset(request.ExpiresAtUtc),
                PolicyVersion = PolicyVersion,
            },
            CatalogAuthority = new NyxIdCatalogAuthorityStamp
            {
                ActorStateVersion = snapshot.StateVersion,
                ObservedAt = Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc),
                FreshUntil = Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc),
                ExternalRevision = snapshot.ExternalRevision,
                ContentDigest = snapshot.ContentDigest,
            },
        };
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Read);
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Proxy);
        plan.NyxIdServiceGrants.Add(grants.ServiceGrants);
        plan.NyxIdNodeGrants.Add(grants.NodeGrants);
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
            var directServices = NormalizeRequiredServices(request.RequiredNyxIdServices);
            if (directServices.Failure != null)
                return TargetEvidenceResolution.Failed(directServices.Failure);
            var directRequiredServices = directServices.Services.ToList();
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
                    directRequiredServices,
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
                directRequiredServices,
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

        var capabilities = ResolveWorkflowCapabilities(workflow.ExternalCapabilities);
        if (capabilities.Failure != null)
            return TargetEvidenceResolution.Failed(capabilities.Failure);
        var requiredServices = capabilities.Services.ToList();
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

        var connectorRefs = capabilities.ConnectorCapabilityRefs;
        if (connectorRefs.Count > 0)
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
                requiredServices,
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
            requiredServices,
            serviceGrantRequirement,
            canonicalStamps.SourceStamps,
            null);
    }

    private async Task<OwnerLLMEvidenceResolution> ResolveOwnerLLMEvidenceAsync(
        string scopeId,
        List<NyxIdUserServiceCapabilityRef> requiredServices,
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
            if (ownerServiceId.Length == 0)
            {
                return OwnerLLMEvidenceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                    "owner_llm_exact_service_identity_unavailable"));
            }
            requiredServices.Add(new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = ownerServiceId,
                ServiceSlugSnapshot = ownerServiceSlug,
            });
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
        IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
        AuthorizationGrantRequirement serviceGrantRequirement,
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> services)
    {
        var servicesById = new Dictionary<string, NyxIdAuthorizationServiceEvidence>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (!TryValidateServiceEvidence(service, out var failureCode, out var detail))
                return GrantResolution.Failed(Failed(failureCode, detail));
            if (!servicesById.TryAdd(service.UserServiceId.Trim(), service))
            {
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous,
                    $"nyxid_service_identity_ambiguous:{service.UserServiceId.Trim()}"));
            }
        }

        if (requiredServices.Count == 0 && serviceGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            return GrantResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                "nyxid_exact_service_identity_unavailable"));
        }

        var serviceGrants = new List<NyxIdServiceGrant>();
        var nodeGrants = new List<NyxIdNodeGrant>();
        foreach (var requiredService in requiredServices)
        {
            var serviceId = requiredService.UserServiceId.Trim();
            if (!servicesById.TryGetValue(serviceId, out var service))
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceNotFound,
                    $"nyxid_service_not_found:{serviceId}"));
            if (service.Access != NyxIdAuthorizationAccess.Permitted)
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied,
                    $"nyxid_service_access_denied:{serviceId}"));
            var slugSnapshot = requiredService.ServiceSlugSnapshot?.Trim() ?? string.Empty;
            if (slugSnapshot.Length > 0 &&
                !string.Equals(slugSnapshot, service.ServiceSlug.Trim(), StringComparison.Ordinal))
            {
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                    $"nyxid_service_slug_snapshot_changed:{serviceId}"));
            }

            serviceGrants.Add(new NyxIdServiceGrant
            {
                UserServiceId = serviceId,
                ServiceSlug = service.ServiceSlug.Trim(),
                DisplayName = service.DisplayName.Trim(),
            });
            foreach (var node in service.Nodes)
            {
                nodeGrants.Add(new NyxIdNodeGrant
                {
                    UserServiceId = serviceId,
                    NodeId = node.NodeId.Trim(),
                    DisplayName = node.DisplayName.Trim(),
                    Role = node.Role,
                    EdgeKind = node.EdgeKind,
                    BindingId = node.BindingId.Trim(),
                    RoutePriority = node.RoutePriority,
                });
            }
        }
        return new GrantResolution(serviceGrants, nodeGrants, null);
    }

    private static bool TryValidateServiceEvidence(
        NyxIdAuthorizationServiceEvidence service,
        out ScheduledInvocationAuthorizationFailureCode failureCode,
        out string detail)
    {
        failureCode = ScheduledInvocationAuthorizationFailureCode.UnknownEnum;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(service.UserServiceId))
        {
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = "nyxid_exact_service_identity_unavailable";
            return false;
        }
        if (string.IsNullOrWhiteSpace(service.ServiceSlug))
        {
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = $"nyxid_service_route_snapshot_unavailable:{service.UserServiceId.Trim()}";
            return false;
        }
        if (service.Access == NyxIdAuthorizationAccess.Unspecified ||
            !Enum.IsDefined(service.Access) ||
            service.NodeGrantRequirement == AuthorizationGrantRequirement.Unspecified ||
            !Enum.IsDefined(service.NodeGrantRequirement))
        {
            detail = "nyxid_service_evidence_invalid";
            return false;
        }

        var primaryNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in service.Nodes)
        {
            if (node.Role == NyxIdNodeRole.Unspecified ||
                !Enum.IsDefined(node.Role) ||
                node.EdgeKind == NyxIdNodeEdgeKind.Unspecified ||
                !Enum.IsDefined(node.EdgeKind))
            {
                detail = $"nyxid_node_evidence_invalid:{service.UserServiceId.Trim()}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(node.NodeId) ||
                node.EdgeKind == NyxIdNodeEdgeKind.NodeBinding &&
                string.IsNullOrWhiteSpace(node.BindingId) ||
                node.EdgeKind == NyxIdNodeEdgeKind.UserServicePrimary &&
                (!string.IsNullOrWhiteSpace(node.BindingId) || node.Role != NyxIdNodeRole.Primary))
            {
                failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
                detail = $"nyxid_node_authorization_topology_unavailable:{service.UserServiceId.Trim()}";
                return false;
            }
            if (node.Role == NyxIdNodeRole.Primary)
                primaryNodeIds.Add(node.NodeId.Trim());
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.Required &&
            (service.Nodes.Count == 0 || primaryNodeIds.Count != 1))
        {
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = $"nyxid_node_authorization_topology_unavailable:{service.UserServiceId.Trim()}";
            return false;
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired && service.Nodes.Count != 0)
        {
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = $"nyxid_node_authorization_topology_unavailable:{service.UserServiceId.Trim()}";
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
            .ToArray();

    private static RequiredServiceResolution NormalizeRequiredServices(
        IEnumerable<NyxIdUserServiceCapabilityRef> services)
    {
        var normalized = new List<NyxIdUserServiceCapabilityRef>();
        foreach (var service in services)
        {
            if (service == null || string.IsNullOrWhiteSpace(service.UserServiceId))
            {
                return RequiredServiceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                    "nyxid_exact_service_identity_unavailable"));
            }

            var clone = service.Clone();
            clone.UserServiceId = clone.UserServiceId.Trim();
            clone.ServiceSlugSnapshot = clone.ServiceSlugSnapshot.Trim();
            normalized.Add(clone);
        }
        return new RequiredServiceResolution(normalized, null);
    }

    private static WorkflowCapabilityResolution ResolveWorkflowCapabilities(
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities)
    {
        var connectorRefs = new List<string>();
        var services = new List<NyxIdUserServiceCapabilityRef>();
        foreach (var capability in capabilities)
        {
            if (capability == null)
            {
                return WorkflowCapabilityResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                    "workflow_external_capability_identity_unavailable"));
            }

            switch (capability.CapabilityCase)
            {
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector:
                    if (string.IsNullOrWhiteSpace(capability.HostConnector.ConnectorCapabilityRef))
                    {
                        return WorkflowCapabilityResolution.Failed(Failed(
                            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                            "connector_exact_capability_identity_unavailable"));
                    }
                    connectorRefs.Add(capability.HostConnector.ConnectorCapabilityRef.Trim());
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                {
                    var required = NormalizeRequiredServices([capability.NyxIdUserService]);
                    if (required.Failure != null)
                        return WorkflowCapabilityResolution.Failed(required.Failure);
                    services.Add(required.Services[0]);
                    break;
                }
                default:
                    return WorkflowCapabilityResolution.Failed(Failed(
                        ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                        "workflow_external_capability_identity_unavailable"));
            }
        }
        return new WorkflowCapabilityResolution(connectorRefs, services, null);
    }

    private static bool OwnerEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority.Trim(), right.Authority.Trim(), StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject.Trim(), right.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode code,
        string detail) => ScheduledInvocationAuthorizationPlanResult.Failed(code, detail);

    private sealed record TargetEvidenceResolution(
        IReadOnlyList<NyxIdUserServiceCapabilityRef> RequiredServices,
        AuthorizationGrantRequirement ServiceGrantRequirement,
        IReadOnlyList<AuthorizationSourceStamp> SourceStamps,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static TargetEvidenceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], AuthorizationGrantRequirement.Unspecified, [], failure);
    }

    private sealed record RequiredServiceResolution(
        IReadOnlyList<NyxIdUserServiceCapabilityRef> Services,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static RequiredServiceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], failure);
    }

    private sealed record WorkflowCapabilityResolution(
        IReadOnlyList<string> ConnectorCapabilityRefs,
        IReadOnlyList<NyxIdUserServiceCapabilityRef> Services,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static WorkflowCapabilityResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], [], failure);
    }

    private sealed record GrantResolution(
        IReadOnlyList<NyxIdServiceGrant> ServiceGrants,
        IReadOnlyList<NyxIdNodeGrant> NodeGrants,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static GrantResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], [], failure);
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
