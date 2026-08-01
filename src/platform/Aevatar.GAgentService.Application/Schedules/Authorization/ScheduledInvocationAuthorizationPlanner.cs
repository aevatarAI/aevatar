using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
        if (!TryResolveAuthenticatedActor(request.OwnerContext, out var authenticatedActor))
        {
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.OwnerInvalid,
                "nyxid_authenticated_actor_invalid");
        }

        var evidence = await ResolveTargetEvidenceAsync(request, ct);
        if (evidence.Failure != null)
            return evidence.Failure;
        if (CanAuthorizeWithoutCatalog(evidence))
        {
            var plan = BuildPlan(
                request,
                authenticatedActor,
                evidence,
                [],
                catalogAuthority: null);
            return ScheduledInvocationAuthorizationPlanResult.Succeeded(plan);
        }

        var snapshot = await _catalogQueryPort.GetAsync(request.Owner, ct);
        if (snapshot == null)
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_snapshot_not_found",
                requiredServices: evidence.RequiredServices,
                llmRefreshRequirement: evidence.LLMRefreshRequirement);
        if (!OwnerEquals(request.Owner, snapshot.Owner))
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.OwnerMismatch,
                "nyxid_catalog_owner_mismatch",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);
        if (snapshot.Invalidated)
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_snapshot_invalidated",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);
        if (snapshot.Cleaned)
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_lifecycle_invalid",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);
        if (snapshot.StateVersion <= 0 ||
            !snapshot.Activated ||
            snapshot.ObservedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContractVersion) ||
            string.IsNullOrWhiteSpace(snapshot.PolicyVersion) ||
            snapshot.EvaluatedAtUtc == default)
        {
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_lifecycle_invalid",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);
        }
        if (string.IsNullOrWhiteSpace(snapshot.ContentDigest) ||
            !string.Equals(
                snapshot.ContentDigest,
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                    snapshot.Owner,
                    snapshot.Services,
                    snapshot.GatewayLLMTarget),
                StringComparison.Ordinal))
        {
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "nyxid_catalog_content_digest_invalid",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);
        }
        if (!HasFreshAuthorityForRequiredServices(snapshot, evidence.RequiredServices, request.EvaluatedAtUtc))
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                "nyxid_catalog_snapshot_stale",
                snapshot.StateVersion,
                evidence.RequiredServices,
                evidence.LLMRefreshRequirement);

        var llmTargetFailure = ValidateOwnerLLMTarget(
            snapshot,
            evidence,
            request.EvaluatedAtUtc);
        if (llmTargetFailure != null)
            return llmTargetFailure;

        var grants = ResolveGrants(
            evidence.RequiredServices,
            evidence.ServiceGrantRequirement,
            snapshot.Services);
        if (grants.Failure != null)
        {
            return grants.Failure with
            {
                ObservedCatalogStateVersion = snapshot.StateVersion,
                RequiredNyxIdServices = CloneRequiredServices(evidence.RequiredServices),
                LLMRefreshRequirement = evidence.LLMRefreshRequirement,
            };
        }

        var catalogAuthority = new NyxIdCatalogAuthorityStamp
        {
            ActorStateVersion = snapshot.StateVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc),
            ContentDigest = snapshot.ContentDigest,
            ContractVersion = snapshot.ContractVersion,
            PolicyVersion = snapshot.PolicyVersion,
            EvaluatedAt = Timestamp.FromDateTimeOffset(snapshot.EvaluatedAtUtc),
        };
        return ScheduledInvocationAuthorizationPlanResult.Succeeded(BuildPlan(
            request,
            authenticatedActor,
            evidence,
            grants.ServiceGrants,
            catalogAuthority));
    }

    public static string ComputeDigest(ScheduledInvocationAuthorizationPlan plan)
        => ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

    private static bool CanAuthorizeWithoutCatalog(TargetEvidenceResolution evidence) =>
        evidence.RequiredServices.Count == 0 &&
        evidence.ServiceGrantRequirement == AuthorizationGrantRequirement.NotRequired &&
        evidence.OwnerLLMSelection is null;

    private static ScheduledInvocationAuthorizationPlanResult? ValidateOwnerLLMTarget(
        NyxIdAuthorizationCatalogSnapshot snapshot,
        TargetEvidenceResolution evidence,
        DateTimeOffset evaluatedAtUtc)
    {
        var selection = evidence.OwnerLLMSelection;
        if (selection == null)
            return null;

        var target = ResolveExactLLMTarget(snapshot, selection);

        if (target == null ||
            !TargetMatchesSelection(target, selection) ||
            target.ObservedAt == null ||
            target.FreshUntil == null ||
            target.EvaluatedAt == null ||
            target.ObservedAt.ToDateTimeOffset() > evaluatedAtUtc ||
            target.FreshUntil.ToDateTimeOffset() <= evaluatedAtUtc ||
            string.IsNullOrWhiteSpace(target.AuthorityContractVersion) ||
            string.IsNullOrWhiteSpace(target.AuthorityPolicyVersion))
        {
            return FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable,
                "owner_llm_route_unavailable",
                snapshot,
                evidence);
        }

        if (target.ModelCatalog == null)
        {
            return FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
                "owner_llm_model_not_verifiable",
                snapshot,
                evidence);
        }

        try
        {
            LLMSelectionPolicy.ValidateCatalog(target.ModelCatalog);
        }
        catch (InvalidOperationException)
        {
            return FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
                "owner_llm_model_not_verifiable",
                snapshot,
                evidence);
        }

        return target.ModelCatalog.Certainty switch
        {
            LLMModelCatalogCertainty.Unavailable => FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable,
                "owner_llm_route_unavailable",
                snapshot,
                evidence),
            LLMModelCatalogCertainty.NotVerifiable => FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
                "owner_llm_model_not_verifiable",
                snapshot,
                evidence),
            LLMModelCatalogCertainty.Enumerated when !target.ModelCatalog.ModelIds.Contains(
                selection.Model,
                StringComparer.Ordinal) => FailedLLMTarget(
                    ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelUnavailable,
                    "owner_llm_model_unavailable",
                    snapshot,
                    evidence),
            LLMModelCatalogCertainty.Enumerated => null,
            _ => FailedLLMTarget(
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
                "owner_llm_model_not_verifiable",
                snapshot,
                evidence),
        };
    }

    private static NyxIdAuthorizationLLMTargetEvidence? ResolveExactLLMTarget(
        NyxIdAuthorizationCatalogSnapshot snapshot,
        ScheduledInvocationOwnerLLMSelection selection)
    {
        if (selection.RouteKind == LLMRouteKind.Gateway)
            return snapshot.GatewayLLMTarget;
        if (selection.RouteKind != LLMRouteKind.NyxIdUserService)
            return null;

        var matches = snapshot.Services
            .Where(service =>
                string.Equals(
                    service.UserServiceId,
                    selection.NyxIdUserServiceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    service.ServiceSlug,
                    selection.ServiceSlugSnapshot,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].LlmTarget : null;
    }

    private static bool TargetMatchesSelection(
        NyxIdAuthorizationLLMTargetEvidence target,
        ScheduledInvocationOwnerLLMSelection selection) =>
        target.RouteKind == selection.RouteKind &&
        string.Equals(target.RouteValue, selection.RouteValue, StringComparison.Ordinal) &&
        string.Equals(
            target.NyxIdUserServiceId,
            selection.NyxIdUserServiceId,
            StringComparison.Ordinal) &&
        string.Equals(
            target.ServiceSlugSnapshot,
            selection.ServiceSlugSnapshot,
            StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult FailedLLMTarget(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail,
        NyxIdAuthorizationCatalogSnapshot snapshot,
        TargetEvidenceResolution evidence) =>
        Failed(
            failureCode,
            detail,
            snapshot.StateVersion,
            evidence.RequiredServices,
            evidence.LLMRefreshRequirement);

    private static ScheduledInvocationAuthorizationPlan BuildPlan(
        ScheduledInvocationAuthorizationRequest request,
        AuthorizationOwnerIdentity authenticatedActor,
        TargetEvidenceResolution evidence,
        IReadOnlyList<NyxIdServiceGrant> serviceGrants,
        NyxIdCatalogAuthorityStamp? catalogAuthority)
    {
        var plan = new ScheduledInvocationAuthorizationPlan
        {
            SchemaVersion = SchemaVersion,
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            AuthenticatedActor = authenticatedActor.Clone(),
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                AllowAllServices = false,
                AllowAllNodes = false,
                ServiceGrantRequirement = evidence.ServiceGrantRequirement,
                NodeGrantRequirement = serviceGrants.Any(static grant =>
                    grant.NodeGrantRequirement == AuthorizationGrantRequirement.Required)
                        ? AuthorizationGrantRequirement.Required
                        : AuthorizationGrantRequirement.NotRequired,
                ExpiresAt = Timestamp.FromDateTimeOffset(request.ExpiresAtUtc),
                PolicyVersion = PolicyVersion,
            },
        };
        if (catalogAuthority is not null)
            plan.CatalogAuthority = catalogAuthority.Clone();
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Read);
        plan.CredentialPolicy.Scopes.Add(NyxIdCredentialScope.Proxy);
        plan.NyxIdServiceGrants.Add(serviceGrants.Select(static grant => grant.Clone()));
        plan.SourceStamps.Add(evidence.SourceStamps.Select(static stamp => stamp.Clone()));
        plan.Disclosures.Add(new[]
        {
            ScheduledInvocationDisclosure.DedicatedCredential,
            ScheduledInvocationDisclosure.AevatarSecretCustody,
            ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
            ScheduledInvocationDisclosure.DeleteRevokesCredential,
            ScheduledInvocationDisclosure.PauseResumePreservesCredential,
            ScheduledInvocationDisclosure.NodeIdsArePermissionSet,
        });
        if (evidence.OwnerLLMSelection is not null)
            plan.OwnerLlmSelection = evidence.OwnerLLMSelection.Clone();
        plan.PermissionDigest = ComputeDigest(plan);
        return plan;
    }

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
            ScheduledInvocationOwnerLLMSelection? directOwnerLLMSelection = null;
            ScheduledInvocationLLMRefreshRequirement? directLLMRefreshRequirement = null;
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
                    selectionRequired: false,
                    ct);
                if (ownerLLM.Failure != null)
                    return TargetEvidenceResolution.Failed(ownerLLM.Failure);
                directGrantRequirement = ownerLLM.ServiceGrantRequirement;
                directOwnerLLMSelection = ownerLLM.Selection;
                directLLMRefreshRequirement = ownerLLM.LLMRefreshRequirement;
            }

            var stamps = CanonicalizeSourceStamps(directSourceStamps);
            if (stamps.Failure != null)
                return TargetEvidenceResolution.Failed(stamps.Failure);
            return new TargetEvidenceResolution(
                directRequiredServices,
                directGrantRequirement,
                stamps.SourceStamps,
                directOwnerLLMSelection,
                directLLMRefreshRequirement,
                null);
        }

        var target = request.InvocationTarget.StudioMember;
        var member = request.TrustedMemberEvidence ??
            await _memberQueryPort.GetAsync(target.ScopeId, target.MemberId, ct);
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

        var workflow = request.TrustedWorkflowEvidence ??
            await _workflowQueryPort.GetAsync(
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
        ScheduledInvocationOwnerLLMSelection? ownerLLMSelection = null;
        ScheduledInvocationLLMRefreshRequirement? llmRefreshRequirement = null;
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
                selectionRequired: true,
                ct);
            if (ownerLLM.Failure != null)
                return TargetEvidenceResolution.Failed(ownerLLM.Failure);
            serviceGrantRequirement = ownerLLM.ServiceGrantRequirement;
            ownerLLMSelection = ownerLLM.Selection;
            llmRefreshRequirement = ownerLLM.LLMRefreshRequirement;
        }

        sourceStamps.AddRange(request.SourceStamps ?? []);
        var canonicalStamps = CanonicalizeSourceStamps(sourceStamps);
        if (canonicalStamps.Failure != null)
            return TargetEvidenceResolution.Failed(canonicalStamps.Failure);
        return new TargetEvidenceResolution(
            requiredServices,
            serviceGrantRequirement,
            canonicalStamps.SourceStamps,
            ownerLLMSelection,
            llmRefreshRequirement,
            null);
    }

    private async Task<OwnerLLMEvidenceResolution> ResolveOwnerLLMEvidenceAsync(
        string scopeId,
        List<NyxIdUserServiceCapabilityRef> requiredServices,
        AuthorizationGrantRequirement serviceGrantRequirement,
        List<AuthorizationSourceStamp> sourceStamps,
        bool selectionRequired,
        CancellationToken ct)
    {
        var ownerLLM = await _ownerLLMQueryPort.GetAsync(scopeId, ct);
        if (ownerLLM == null)
        {
            if (!selectionRequired)
            {
                return new OwnerLLMEvidenceResolution(
                    serviceGrantRequirement,
                    null,
                    null,
                    null);
            }
            return OwnerLLMEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "owner_llm_authorization_evidence_not_found"));
        }
        if (ownerLLM.StateVersion <= 0)
        {
            return OwnerLLMEvidenceResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                "owner_llm_source_version_invalid"));
        }
        if (!ScheduledInvocationOwnerLLMSelectionPolicy.IsDurableSelectionValid(ownerLLM.Selection))
        {
            var isExplicitModelCanonical = IsExplicitModelCanonical(ownerLLM.Selection.Model);
            return OwnerLLMEvidenceResolution.Failed(Failed(
                isExplicitModelCanonical
                    ? ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable
                    : ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable,
                isExplicitModelCanonical
                    ? "owner_llm_route_unavailable"
                    : "owner_llm_explicit_model_required"));
        }

        var selection = ownerLLM.Selection;
        if (selection.RouteKind == LLMRouteKind.NyxIdUserService)
        {
            requiredServices.Add(new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = selection.NyxIdUserServiceId,
                ServiceSlugSnapshot = selection.ServiceSlugSnapshot,
            });
            serviceGrantRequirement = AuthorizationGrantRequirement.Required;
        }

        sourceStamps.Add(new AuthorizationSourceStamp
        {
            SourceKind = AuthorizationSourceKind.OwnerLlmRoute,
            SourceId = scopeId,
            StateVersion = ownerLLM.StateVersion,
        });
        return new OwnerLLMEvidenceResolution(
            serviceGrantRequirement,
            selection,
            new ScheduledInvocationLLMRefreshRequirement(
                selection.RouteKind,
                selection.RouteValue,
                selection.NyxIdUserServiceId,
                selection.ServiceSlugSnapshot,
                selection.Model,
                ownerLLM.StateVersion),
            null);
    }

    private static bool IsExplicitModelCanonical(string model)
    {
        try
        {
            LLMSelectionPolicy.ValidateSelection(new LLMSelection
            {
                RouteKind = LLMRouteKind.Gateway,
                RouteValue = LLMSelectionPolicy.GatewayRoute,
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = model ?? string.Empty,
                },
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static GrantResolution ResolveGrants(
        IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
        AuthorizationGrantRequirement serviceGrantRequirement,
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> services)
    {
        var normalizedRequiredServices = NormalizeRequiredServices(requiredServices);
        if (normalizedRequiredServices.Failure != null)
            return GrantResolution.Failed(normalizedRequiredServices.Failure);

        var requiredServiceIds = normalizedRequiredServices.Services
            .Select(static service => service.UserServiceId)
            .ToHashSet(StringComparer.Ordinal);
        var servicesById = new Dictionary<string, NyxIdAuthorizationServiceEvidence>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var serviceId = service.UserServiceId?.Trim() ?? string.Empty;
            if (requiredServiceIds.Count > 0 && !requiredServiceIds.Contains(serviceId))
                continue;
            if (!TryValidateServiceEvidence(service, out var failureCode, out var detail))
                return GrantResolution.Failed(Failed(failureCode, detail));
            if (!servicesById.TryAdd(serviceId, service))
            {
                return GrantResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous,
                    $"nyxid_service_identity_ambiguous:{serviceId}"));
            }
        }

        if (normalizedRequiredServices.Services.Count == 0 &&
            serviceGrantRequirement == AuthorizationGrantRequirement.Required)
        {
            return GrantResolution.Failed(Failed(
                ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                "nyxid_exact_service_identity_unavailable"));
        }

        var serviceGrants = new List<NyxIdServiceGrant>();
        foreach (var requiredService in normalizedRequiredServices.Services)
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

    private static bool HasFreshAuthorityForRequiredServices(
        NyxIdAuthorizationCatalogSnapshot snapshot,
        IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
        DateTimeOffset evaluatedAtUtc)
    {
        var normalizedRequiredServices = NormalizeRequiredServices(requiredServices);
        if (normalizedRequiredServices.Failure != null || normalizedRequiredServices.Services.Count == 0)
            return snapshot.ObservedAtUtc <= evaluatedAtUtc && snapshot.FreshUntilUtc > evaluatedAtUtc;

        var requiredServiceIds = normalizedRequiredServices.Services
            .Select(static service => service.UserServiceId)
            .ToHashSet(StringComparer.Ordinal);
        var freshServiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in snapshot.Services)
        {
            var serviceId = service.UserServiceId?.Trim() ?? string.Empty;
            if (!requiredServiceIds.Contains(serviceId))
                continue;
            if (service.ObservedAt == null || service.FreshUntil == null)
            {
                if (snapshot.ObservedAtUtc > evaluatedAtUtc || snapshot.FreshUntilUtc <= evaluatedAtUtc)
                    return false;
                freshServiceIds.Add(serviceId);
                continue;
            }
            if (service.ObservedAt.ToDateTimeOffset() > evaluatedAtUtc ||
                service.FreshUntil.ToDateTimeOffset() <= evaluatedAtUtc)
            {
                return false;
            }
            freshServiceIds.Add(serviceId);
        }
        return freshServiceIds.SetEquals(requiredServiceIds);
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
        if (!string.Equals(service.UserServiceId, service.UserServiceId.Trim(), StringComparison.Ordinal) ||
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
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
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
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = $"nyxid_node_authorization_topology_unavailable:{service.UserServiceId}";
            return false;
        }
        if (service.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired && service.NodeIds.Count != 0)
        {
            failureCode = ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable;
            detail = $"nyxid_node_authorization_topology_unavailable:{service.UserServiceId}";
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
        if (!TryResolveAuthenticatedActor(request.OwnerContext, out _))
        {
            return Failed(
                ScheduledInvocationAuthorizationFailureCode.OwnerInvalid,
                "nyxid_authenticated_actor_invalid");
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
        string.Equals(owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        owner.OwnerKind != AuthorizationOwnerKind.Unspecified &&
        Enum.IsDefined(owner.OwnerKind) &&
        !string.IsNullOrWhiteSpace(owner.OwnerSubject) &&
        string.Equals(owner.OwnerSubject, owner.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool TryResolveAuthenticatedActor(
        AuthenticatedAuthorizationOwnerContext context,
        out AuthorizationOwnerIdentity authenticatedActor)
    {
        authenticatedActor = null!;
        var owner = context.Owner;
        if (!IsNormalizedNyxIdIdentity(owner))
            return false;

        var candidate = context.AuthenticatedActor;
        if (owner.OwnerKind == AuthorizationOwnerKind.Personal)
        {
            candidate ??= owner;
            if (!IdentityEquals(candidate, owner))
                return false;
        }
        else if (owner.OwnerKind != AuthorizationOwnerKind.Organization || candidate == null)
        {
            return false;
        }

        if (!IsNormalizedNyxIdIdentity(candidate) ||
            candidate.OwnerKind != AuthorizationOwnerKind.Personal)
        {
            return false;
        }

        authenticatedActor = candidate.Clone();
        return true;
    }

    private static bool IsNormalizedNyxIdIdentity(AuthorizationOwnerIdentity? identity) =>
        identity != null &&
        string.Equals(identity.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        identity.OwnerKind is AuthorizationOwnerKind.Personal or AuthorizationOwnerKind.Organization &&
        !string.IsNullOrWhiteSpace(identity.OwnerSubject) &&
        string.Equals(identity.OwnerSubject, identity.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool IdentityEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private static RequiredServiceResolution NormalizeRequiredServices(
        IEnumerable<NyxIdUserServiceCapabilityRef> services)
    {
        var normalized = new SortedDictionary<string, NyxIdUserServiceCapabilityRef>(StringComparer.Ordinal);
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
            clone.ServiceSlugSnapshot = clone.ServiceSlugSnapshot?.Trim() ?? string.Empty;
            if (!normalized.TryGetValue(clone.UserServiceId, out var existing))
            {
                normalized.Add(clone.UserServiceId, clone);
                continue;
            }

            if (existing.ServiceSlugSnapshot.Length > 0 &&
                clone.ServiceSlugSnapshot.Length > 0 &&
                !string.Equals(
                    existing.ServiceSlugSnapshot,
                    clone.ServiceSlugSnapshot,
                    StringComparison.Ordinal))
            {
                return RequiredServiceResolution.Failed(Failed(
                    ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                    $"nyxid_service_slug_snapshot_conflict:{clone.UserServiceId}"));
            }

            if (existing.ServiceSlugSnapshot.Length == 0 && clone.ServiceSlugSnapshot.Length > 0)
                existing.ServiceSlugSnapshot = clone.ServiceSlugSnapshot;
        }
        return new RequiredServiceResolution(normalized.Values.ToArray(), null);
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
                    services.Add(capability.NyxIdUserService.Clone());
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest:
                    services.Add(new NyxIdUserServiceCapabilityRef
                    {
                        UserServiceId = capability.NyxIdUserRequest.Request?.UserServiceId ?? string.Empty,
                    });
                    break;
                default:
                    return WorkflowCapabilityResolution.Failed(Failed(
                        ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable,
                        "workflow_external_capability_identity_unavailable"));
            }
        }

        var normalizedServices = NormalizeRequiredServices(services);
        if (normalizedServices.Failure != null)
            return WorkflowCapabilityResolution.Failed(normalizedServices.Failure);
        return new WorkflowCapabilityResolution(
            NormalizeStrings(connectorRefs),
            normalizedServices.Services,
            null);
    }

    private static bool OwnerEquals(AuthorizationOwnerIdentity left, AuthorizationOwnerIdentity right) =>
        string.Equals(left.Authority.Trim(), right.Authority.Trim(), StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject.Trim(), right.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode code,
        string detail,
        long observedCatalogStateVersion = 0,
        IReadOnlyList<NyxIdUserServiceCapabilityRef>? requiredServices = null,
        ScheduledInvocationLLMRefreshRequirement? llmRefreshRequirement = null) =>
        ScheduledInvocationAuthorizationPlanResult.Failed(
            code,
            detail,
            observedCatalogStateVersion,
            CloneRequiredServices(requiredServices),
            llmRefreshRequirement);

    private static IReadOnlyList<NyxIdUserServiceCapabilityRef>? CloneRequiredServices(
        IReadOnlyList<NyxIdUserServiceCapabilityRef>? services) =>
        services?.Select(static service => service.Clone()).ToArray();

    private sealed record TargetEvidenceResolution(
        IReadOnlyList<NyxIdUserServiceCapabilityRef> RequiredServices,
        AuthorizationGrantRequirement ServiceGrantRequirement,
        IReadOnlyList<AuthorizationSourceStamp> SourceStamps,
        ScheduledInvocationOwnerLLMSelection? OwnerLLMSelection,
        ScheduledInvocationLLMRefreshRequirement? LLMRefreshRequirement,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static TargetEvidenceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new([], AuthorizationGrantRequirement.Unspecified, [], null, null, failure);
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
        ScheduledInvocationOwnerLLMSelection? Selection,
        ScheduledInvocationLLMRefreshRequirement? LLMRefreshRequirement,
        ScheduledInvocationAuthorizationPlanResult? Failure)
    {
        public static OwnerLLMEvidenceResolution Failed(ScheduledInvocationAuthorizationPlanResult failure) =>
            new(AuthorizationGrantRequirement.Unspecified, null, null, failure);
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
