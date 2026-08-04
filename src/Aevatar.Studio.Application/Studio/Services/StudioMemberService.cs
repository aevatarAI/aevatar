using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Member-first Studio facade. Bind commands now return an honest accepted
/// receipt and hand the request to actor-owned admission, so a stale
/// StudioMember read model cannot reject a member that the actor already
/// owns.
/// </summary>
public sealed class StudioMemberService : IStudioMemberService
{
    private const string DefaultSmokePrompt = "Hello from Studio Bind.";

    private readonly IStudioMemberCommandPort _memberCommandPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IStudioMemberBindingRunQueryPort _bindingRunQueryPort;
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IScopeBindingReadinessQueryPort _readinessQueryPort;
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;

    public StudioMemberService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IStudioMemberBindingRunQueryPort bindingRunQueryPort,
        IStudioTeamQueryPort teamQueryPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IScopeBindingReadinessQueryPort readinessQueryPort,
        IServiceCommandPort serviceCommandPort,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService)
    {
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _bindingRunQueryPort = bindingRunQueryPort ?? throw new ArgumentNullException(nameof(bindingRunQueryPort));
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort
            ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _readinessQueryPort = readinessQueryPort ?? throw new ArgumentNullException(nameof(readinessQueryPort));
        _serviceCommandPort = serviceCommandPort
            ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _capabilityAdmissionService = capabilityAdmissionService
            ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    public async Task<StudioMemberSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedRequest = StudioMemberCreateRequestValidator.Validate(scopeId, request);

        if (!string.IsNullOrEmpty(normalizedRequest.TeamId))
        {
            var team = await _teamQueryPort.GetAsync(scopeId, normalizedRequest.TeamId, ct);
            if (team == null)
                throw new StudioTeamNotFoundException(scopeId, normalizedRequest.TeamId);
        }

        return await _memberCommandPort.CreateAsync(scopeId, normalizedRequest, ct);
    }

    public Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        StudioMemberRosterPageRequest? page = null,
        CancellationToken ct = default) =>
        _memberQueryPort.ListAsync(scopeId, page, ct);

    public async Task<StudioMemberDetailResponse> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        // Mirrors GetBindingAsync semantics: a missing member is
        // unambiguous "404 STUDIO_MEMBER_NOT_FOUND", not a 200-with-null
        // body that the frontend would have to pattern-match. Endpoints
        // catch the typed exception and return the same body shape from
        // every member-centric endpoint.
        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, memberId);
        return await HydrateCurrentBindingRunAsync(detail, ct);
    }

    public async Task<StudioMemberBindingAcceptedResponse> BindAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedMemberId = NormalizeRequired(memberId, nameof(memberId));
        var implementationKindWire = ResolveBindingImplementationKind(normalizedMemberId, request);
        if (request.Workflow is { } workflow)
        {
            var suppliedAdmission = request.CapabilityAdmission;
            var callerId = suppliedAdmission?.CallerId ?? string.Empty;
            var callerCredential = suppliedAdmission?.NyxIdCallerCredential;
            var organizationBearerToken = suppliedAdmission?.NyxIdOrganizationBearerToken;
            var existingPlan = (workflow.CapabilityAdmissionPlan ?? suppliedAdmission?.ExistingPlan)?.Clone();
            var explicitRequestConfirmations = suppliedAdmission?.ExplicitRequestConfirmations ?? [];
            var executionMode = suppliedAdmission?.ExecutionMode
                ?? ExternalCapabilityExecutionMode.Interactive;
            var capabilityAdmissionPlan = existingPlan is not null
                ? await _capabilityAdmissionService.RevalidatePersistedAsync(
                    PersistedWorkflowCapabilityAdmissionRequest.FromWorkflowYamls(
                        existingPlan,
                        workflow.WorkflowYamls,
                        "studio_member_binding_run",
                        executionMode,
                        workflow.WorkflowId,
                        request.RevisionId),
                    ct)
                : await _capabilityAdmissionService.AdmitAsync(
                    WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls(
                    new ExternalWorkflowCapabilityAccessContext(
                        normalizedScopeId,
                        callerId,
                        callerCredential,
                        organizationBearerToken),
                    workflow.WorkflowYamls,
                    "studio_member_binding_run",
                    executionMode,
                    explicitRequestConfirmations,
                    workflow.WorkflowId,
                    request.RevisionId),
                    ct);
            request = request with
            {
                CapabilityAdmission = null,
                Workflow = workflow with
                {
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                },
            };
        }
        var bindingRunId = GenerateBindingRunId();

        await _memberCommandPort.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: bindingRunId,
                ScopeId: normalizedScopeId,
                MemberId: normalizedMemberId,
                ImplementationKind: implementationKindWire,
                Binding: request),
            ct);

        return new StudioMemberBindingAcceptedResponse(
            Status: StudioMemberBindingRunStatusNames.Accepted,
            BindingRunId: bindingRunId,
            ScopeId: normalizedScopeId,
            MemberId: normalizedMemberId);
    }

    public async Task<StudioMemberBindingViewResponse> GetBindingAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, memberId);

        var currentBindingRun = await ResolveCurrentBindingRunAsync(detail, ct);
        return new StudioMemberBindingViewResponse(detail.LastBinding)
        {
            CurrentBindingRun = currentBindingRun,
        };
    }

    public async Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
        string scopeId,
        string memberId,
        string bindingRunId,
        CancellationToken ct = default)
    {
        var normalizedBindingRunId = NormalizeRequired(bindingRunId, nameof(bindingRunId));
        var run = await _bindingRunQueryPort.GetAsync(scopeId, memberId, normalizedBindingRunId, ct);
        if (run == null)
            throw new StudioMemberBindingRunNotFoundException(scopeId, memberId, normalizedBindingRunId);

        return run;
    }

    private async Task<StudioMemberDetailResponse> HydrateCurrentBindingRunAsync(
        StudioMemberDetailResponse detail,
        CancellationToken ct)
    {
        var currentBindingRun = await ResolveCurrentBindingRunAsync(detail, ct);
        return detail with
        {
            CurrentBindingRun = currentBindingRun,
        };
    }

    private async Task<StudioMemberBindingRunStatusResponse?> ResolveCurrentBindingRunAsync(
        StudioMemberDetailResponse detail,
        CancellationToken ct)
    {
        var memberCurrentRun = detail.CurrentBindingRun;
        if (memberCurrentRun == null || string.IsNullOrEmpty(memberCurrentRun.BindingRunId))
            return null;

        var run = await _bindingRunQueryPort.GetAsync(
            memberCurrentRun.ScopeId,
            memberCurrentRun.MemberId,
            memberCurrentRun.BindingRunId,
            ct);

        return run ?? memberCurrentRun;
    }

    public async Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
        string scopeId,
        string memberId,
        string endpointId,
        CancellationToken ct = default)
    {
        var normalizedEndpointId = NormalizeRequired(endpointId, nameof(endpointId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);
        var contract = BuildMemberEndpointContractResponse(
            context.ScopeId,
            context.MemberId,
            context.PublishedServiceId,
            normalizedEndpointId,
            context.Service,
            context.Revisions,
            StudioMemberInvocationReadinessUnknown(
                revisionId: ServiceEndpointContractMath.ResolveCurrentContractRevision(
                    context.Service,
                    context.Revisions,
                    normalizedEndpointId)?.RevisionId));
        if (contract == null)
            return null;

        var readiness = await ResolveInvocationReadinessAsync(
            context.ScopeId,
            context.PublishedServiceId,
            contract.RevisionId,
            endpointId: normalizedEndpointId,
            ct);
        return contract with { InvocationReadiness = readiness };
    }

    public async Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default)
    {
        var normalizedRevisionId = NormalizeRequired(revisionId, nameof(revisionId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);
        var revision = ResolveRevisionOrThrow(context.Revisions, normalizedRevisionId);

        if (string.Equals(
                revision.Status,
                ServiceRevisionStatus.Retired.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{normalizedRevisionId}' is retired and cannot be activated.");
        }

        // NOTE: Activate is intentionally non-atomic — it dispatches
        // SetDefaultServingRevision then ActivateServiceRevision. If the
        // second command fails after the first succeeds, the revision is
        // marked default-serving but never moves to "active". This matches
        // the legacy scope-default activate path; no compensating action
        // is taken here. Both commands are also idempotent on the
        // platform side, so a retried Activate from the caller will
        // converge.
        await _serviceCommandPort.SetDefaultServingRevisionAsync(
            new SetDefaultServingRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);
        await _serviceCommandPort.ActivateServiceRevisionAsync(
            new ActivateServiceRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);

        return new StudioMemberBindingActivationResponse(
            ScopeId: context.ScopeId,
            MemberId: context.MemberId,
            PublishedServiceId: context.PublishedServiceId,
            DisplayName: context.Service.DisplayName,
            RevisionId: normalizedRevisionId);
    }

    public async Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default)
    {
        var normalizedRevisionId = NormalizeRequired(revisionId, nameof(revisionId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);

        // Verify the revision exists in the catalog before dispatching.
        // The platform's RetireRevision is idempotent, but a typo in the
        // revisionId would silently succeed and the projection would
        // surface the contradiction later — the deterministic 400 here is
        // the friendlier failure mode.
        _ = ResolveRevisionOrThrow(context.Revisions, normalizedRevisionId);

        await _serviceCommandPort.RetireRevisionAsync(
            new RetireServiceRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);

        return new StudioMemberBindingRevisionActionResponse(
            ScopeId: context.ScopeId,
            MemberId: context.MemberId,
            PublishedServiceId: context.PublishedServiceId,
            RevisionId: normalizedRevisionId,
            Status: MemberRevisionLifecycleStatusNames.Retired);
    }

    public async Task<StudioMemberCommandResponse> UpdateAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasChange = request.DisplayName.HasValue
            || request.TeamId.HasValue
            || request.ImplementationRef.HasValue;
        if (!hasChange)
        {
            return new StudioMemberCommandResponse(
                StudioMemberCommandStatusNames.NoChange,
                scopeId,
                memberId);
        }

        string? displayName = null;
        if (request.DisplayName.HasValue)
        {
            displayName = StudioMemberCreateRequestValidator.ValidateAndNormalizeDisplayName(
                request.DisplayName.Value);
        }

        string? targetTeamId = null;
        if (request.TeamId.HasValue)
        {
            var requested = request.TeamId.Value;

            // Application-layer guard: empty / whitespace strings on the wire
            // are rejected before reaching the actor protocol (ADR-0017 §Q6).
            if (requested != null && string.IsNullOrWhiteSpace(requested))
            {
                throw new InvalidOperationException(
                    "teamId must not be empty when present " +
                    "(use null in JSON body to mean 'unassign').");
            }

            targetTeamId = requested?.Trim();
        }

        StudioMemberImplementationRefResponse? implementation = null;
        if (request.ImplementationRef.HasValue)
        {
            var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
                ?? throw new StudioMemberNotFoundException(scopeId, memberId);
            implementation = NormalizePatchImplementationRef(
                memberId,
                detail.Summary.ImplementationKind,
                request.ImplementationRef.Value
                    ?? throw new InvalidOperationException("implementationRef must not be null when present."));
        }

        if (request.DisplayName.HasValue)
        {
            await _memberCommandPort.RenameAsync(
                scopeId,
                memberId,
                displayName!,
                ct);
        }

        if (request.TeamId.HasValue)
        {
            // Refactor (iter96/cluster-545):
            //   Old: service/application layer interpreted current membership and reassignment fanout.
            //   New: service forwards PATCH intent only; StudioMemberGAgent owns the decision and materializer fans out committed facts.
            await _memberCommandPort.PatchTeamAssignmentAsync(
                scopeId,
                memberId,
                targetTeamId: targetTeamId,
                ct);
        }

        if (request.ImplementationRef.HasValue)
        {
            await _memberCommandPort.UpdateImplementationAsync(
                scopeId,
                memberId,
                implementation!,
                ct);
        }

        return new StudioMemberCommandResponse(
            StudioMemberCommandStatusNames.Accepted,
            scopeId,
            memberId,
            DateTimeOffset.UtcNow);
    }

    public async Task<StudioMemberCommandResponse> DeleteAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedMemberId = NormalizeRequired(memberId, nameof(memberId));

        var existing = await _memberQueryPort.GetAsync(normalizedScopeId, normalizedMemberId, ct);
        if (existing == null)
            throw new StudioMemberNotFoundException(normalizedScopeId, normalizedMemberId);

        await _memberCommandPort.DeleteAsync(normalizedScopeId, normalizedMemberId, ct);
        return new StudioMemberCommandResponse(
            StudioMemberCommandStatusNames.DeleteAccepted,
            normalizedScopeId,
            normalizedMemberId,
            DateTimeOffset.UtcNow);
    }

    private async Task<StudioMemberInvocationReadinessResponse> ResolveInvocationReadinessAsync(
        string scopeId,
        string? publishedServiceId,
        string? expectedRevisionId,
        string? endpointId,
        CancellationToken ct)
    {
        var normalizedServiceId = publishedServiceId?.Trim() ?? string.Empty;
        if (normalizedServiceId.Length == 0)
        {
            return StudioMemberInvocationReadinessUnknown(
                expectedRevisionId,
                "Member has no published service yet; bind the member before invoking it.");
        }

        var request = new ScopeBindingReadinessRequest(
            ScopeId: scopeId,
            ServiceId: normalizedServiceId,
            ExpectedRevisionId: NormalizeOptional(expectedRevisionId),
            ExpectedEndpointIds: string.IsNullOrWhiteSpace(endpointId) ? null : [endpointId.Trim()]);
        var snapshot = await _readinessQueryPort.GetReadinessAsync(request, ct);
        return MapInvocationReadiness(snapshot);
    }

    private static StudioMemberInvocationReadinessResponse MapInvocationReadiness(
        ScopeBindingReadinessSnapshot snapshot)
    {
        var status = MapInvocationReadinessStatus(snapshot.Status);
        var canInvoke = snapshot.Status == ScopeBindingReadinessStatus.Ready && snapshot.InvokeReady;
        return new StudioMemberInvocationReadinessResponse(
            CanInvoke: canInvoke,
            Status: status,
            ReasonCode: canInvoke ? StudioMemberInvocationReadinessStatusNames.Ready : status,
            Message: BuildInvocationReadinessMessage(status, canInvoke),
            RevisionId: snapshot.RevisionId,
            DeploymentId: snapshot.DeploymentId,
            ObservedAtUtc: snapshot.ObservedAtUtc);
    }

    private static StudioMemberInvocationReadinessResponse StudioMemberInvocationReadinessUnknown(
        string? revisionId,
        string message = "Invocation readiness is not available yet.") =>
        new(
            CanInvoke: false,
            Status: StudioMemberInvocationReadinessStatusNames.Unknown,
            ReasonCode: StudioMemberInvocationReadinessStatusNames.Unknown,
            Message: message,
            RevisionId: NormalizeOptional(revisionId));

    private static string MapInvocationReadinessStatus(ScopeBindingReadinessStatus status) =>
        status switch
        {
            ScopeBindingReadinessStatus.Ready => StudioMemberInvocationReadinessStatusNames.Ready,
            ScopeBindingReadinessStatus.ServiceCatalogMissing => StudioMemberInvocationReadinessStatusNames.ServiceCatalogMissing,
            ScopeBindingReadinessStatus.ServingSetMissing => StudioMemberInvocationReadinessStatusNames.ServingSetMissing,
            ScopeBindingReadinessStatus.EligibleServingTargetMissing => StudioMemberInvocationReadinessStatusNames.EligibleServingTargetMissing,
            ScopeBindingReadinessStatus.ServiceCatalogTargetMissing => StudioMemberInvocationReadinessStatusNames.ServiceCatalogTargetMissing,
            ScopeBindingReadinessStatus.TrafficViewTargetMissing => StudioMemberInvocationReadinessStatusNames.TrafficViewTargetMissing,
            ScopeBindingReadinessStatus.PreparedArtifactMissing => StudioMemberInvocationReadinessStatusNames.PreparedArtifactMissing,
            _ => StudioMemberInvocationReadinessStatusNames.Unknown,
        };

    private static string BuildInvocationReadinessMessage(string status, bool canInvoke) =>
        canInvoke
            ? "Member endpoint is ready for invocation."
            : status switch
            {
                StudioMemberInvocationReadinessStatusNames.PreparedArtifactMissing =>
                    "Binding is complete, but the runtime artifact is not prepared for invocation yet.",
                StudioMemberInvocationReadinessStatusNames.ServiceCatalogMissing =>
                    "Published service is not visible in the service catalog yet.",
                StudioMemberInvocationReadinessStatusNames.ServingSetMissing =>
                    "Serving target is not visible yet.",
                StudioMemberInvocationReadinessStatusNames.EligibleServingTargetMissing =>
                    "No active serving target is eligible for invocation yet.",
                StudioMemberInvocationReadinessStatusNames.ServiceCatalogTargetMissing =>
                    "The selected endpoint is not visible in the service catalog yet.",
                StudioMemberInvocationReadinessStatusNames.TrafficViewTargetMissing =>
                    "Runtime traffic view has not observed the serving target yet.",
                _ => "Invocation readiness is not available yet.",
            };

    /// <summary>
    /// Resolves the published service the member is currently bound to in
    /// one round-trip: member authority → service catalog → revisions.
    /// Bundling the three queries here means the contract / activate /
    /// retire paths all see the same revision snapshot they validated
    /// against — no TOCTOU window where a revision was retired between
    /// the verify and the dispatch — and the test surface only stubs one
    /// method instead of three. Throws <see cref="StudioMemberNotFoundException"/>
    /// for missing members and <see cref="InvalidOperationException"/> for
    /// "exists but never bound" so the endpoint layer maps each to the
    /// right HTTP status.
    /// </summary>
    private async Task<BoundServiceContext> ResolveBoundServiceContextAsync(
        string scopeId,
        string memberId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));

        var detail = await _memberQueryPort.GetAsync(normalizedScopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(normalizedScopeId, memberId);

        var publishedServiceId = detail.Summary.PublishedServiceId;
        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"member '{memberId}' has no publishedServiceId; this is a backend invariant violation.");
        }

        var identity = new ServiceIdentity
        {
            TenantId = normalizedScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = publishedServiceId,
        };

        // The published service surfaces only after the member is bound — a
        // pre-bind read returns null from the platform query port. We surface
        // that as a 400 (not 404) so the frontend can distinguish "missing
        // member" from "exists but unbound" without parsing error text.
        var service = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct)
            ?? throw BuildMemberNotBoundException(memberId);
        var revisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        return new BoundServiceContext(
            normalizedScopeId,
            detail.Summary.MemberId,
            publishedServiceId,
            identity,
            service,
            revisions);
    }

    private static ServiceRevisionSnapshot ResolveRevisionOrThrow(
        ServiceRevisionCatalogSnapshot? revisions,
        string revisionId)
    {
        var revision = revisions?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (revision == null)
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' was not found on the member's published service.");
        }

        return revision;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static StudioMemberImplementationRefResponse NormalizePatchImplementationRef(
        string memberId,
        string memberImplementationKind,
        StudioMemberImplementationRefResponse implementation)
    {
        try
        {
            return NormalizeImplementationRef(memberImplementationKind, implementation);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith(
            "implementationRef.implementationKind must match implementationKind",
            StringComparison.Ordinal))
        {
            var kind = NormalizeRequired(implementation.ImplementationKind, "implementationRef.implementationKind")
                .ToLowerInvariant();
            throw new InvalidOperationException(
                $"member '{memberId}' implementationKind is locked at create. " +
                $"Was {memberImplementationKind}, attempted {kind}.");
        }
    }

    internal static StudioMemberImplementationRefResponse NormalizeImplementationRef(
        string implementationKind,
        StudioMemberImplementationRefResponse implementation)
    {
        var kind = NormalizeRequired(implementation.ImplementationKind, "implementationRef.implementationKind")
            .ToLowerInvariant();

        if (!string.Equals(kind, implementationKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"implementationRef.implementationKind must match implementationKind '{implementationKind}'. " +
                $"Was {kind}.");
        }

        return kind switch
        {
            MemberImplementationKindNames.Workflow => NormalizeWorkflowImplementationRef(implementation),
            MemberImplementationKindNames.Script => NormalizeScriptImplementationRef(implementation),
            MemberImplementationKindNames.GAgent => NormalizeGAgentImplementationRef(implementation),
            _ => throw new InvalidOperationException(
                $"implementationRef.implementationKind '{implementation.ImplementationKind}' is not supported."),
        };
    }

    private static StudioMemberImplementationRefResponse NormalizeWorkflowImplementationRef(
        StudioMemberImplementationRefResponse implementation)
    {
        RejectPresent(implementation.ScriptId, "implementationRef.scriptId", implementation.ImplementationKind);
        RejectPresent(implementation.ScriptRevision, "implementationRef.scriptRevision", implementation.ImplementationKind);
        RejectPresent(
            implementation.DiagnosticActorTypeName,
            "implementationRef.diagnosticActorTypeName",
            implementation.ImplementationKind);

        return new StudioMemberImplementationRefResponse(
            ImplementationKind: MemberImplementationKindNames.Workflow,
            WorkflowId: NormalizeRequired(implementation.WorkflowId, "implementationRef.workflowId"),
            WorkflowRevision: NormalizeOptional(implementation.WorkflowRevision));
    }

    private static StudioMemberImplementationRefResponse NormalizeScriptImplementationRef(
        StudioMemberImplementationRefResponse implementation)
    {
        RejectPresent(implementation.WorkflowId, "implementationRef.workflowId", implementation.ImplementationKind);
        RejectPresent(implementation.WorkflowRevision, "implementationRef.workflowRevision", implementation.ImplementationKind);
        RejectPresent(
            implementation.DiagnosticActorTypeName,
            "implementationRef.diagnosticActorTypeName",
            implementation.ImplementationKind);

        return new StudioMemberImplementationRefResponse(
            ImplementationKind: MemberImplementationKindNames.Script,
            ScriptId: NormalizeRequired(implementation.ScriptId, "implementationRef.scriptId"),
            ScriptRevision: NormalizeOptional(implementation.ScriptRevision));
    }

    private static StudioMemberImplementationRefResponse NormalizeGAgentImplementationRef(
        StudioMemberImplementationRefResponse implementation)
    {
        RejectPresent(implementation.WorkflowId, "implementationRef.workflowId", implementation.ImplementationKind);
        RejectPresent(implementation.WorkflowRevision, "implementationRef.workflowRevision", implementation.ImplementationKind);
        RejectPresent(implementation.ScriptId, "implementationRef.scriptId", implementation.ImplementationKind);
        RejectPresent(implementation.ScriptRevision, "implementationRef.scriptRevision", implementation.ImplementationKind);

        return new StudioMemberImplementationRefResponse(
            ImplementationKind: MemberImplementationKindNames.GAgent,
            DiagnosticActorTypeName: NormalizeRequired(
                implementation.DiagnosticActorTypeName,
                "implementationRef.diagnosticActorTypeName"));
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    private static void RejectPresent(string? value, string fieldName, string implementationKind)
    {
        if (value != null)
        {
            throw new InvalidOperationException(
                $"{fieldName} is not allowed when implementationRef.implementationKind is '{implementationKind}'.");
        }
    }

    private static string GenerateBindingRunId() =>
        $"bind-{Guid.NewGuid():N}";

    private static string ResolveBindingImplementationKind(
        string memberId,
        UpdateStudioMemberBindingRequest request)
    {
        var count = 0;
        string? implementationKind = null;

        if (request.Workflow != null)
        {
            count++;
            implementationKind = MemberImplementationKindNames.Workflow;
            if (string.IsNullOrWhiteSpace(request.Workflow.WorkflowId))
            {
                throw new InvalidOperationException(
                    $"member '{memberId}' bind: workflowId is required for workflow members.");
            }

            if (request.Workflow.WorkflowYamls.Count == 0)
            {
                throw new InvalidOperationException(
                    $"member '{memberId}' bind: workflow yamls are required for workflow members.");
            }
        }

        if (request.Script != null)
        {
            count++;
            implementationKind = MemberImplementationKindNames.Script;
            if (string.IsNullOrWhiteSpace(request.Script.ScriptId))
            {
                throw new InvalidOperationException(
                    $"member '{memberId}' bind: scriptId is required for script members.");
            }
        }

        if (request.GAgent != null)
        {
            count++;
            implementationKind = MemberImplementationKindNames.GAgent;
            if (string.IsNullOrWhiteSpace(request.GAgent.AgentKind))
            {
                throw new InvalidOperationException(
                    $"member '{memberId}' bind: agentKind is required for gagent members.");
            }
        }

        return count switch
        {
            1 => implementationKind!,
            0 => throw new InvalidOperationException(
                $"member '{memberId}' bind: exactly one binding implementation is required."),
            _ => throw new InvalidOperationException(
                $"member '{memberId}' bind: only one binding implementation may be supplied."),
        };
    }

    private static InvalidOperationException BuildMemberNotBoundException(string memberId) =>
        new($"member '{memberId}' has no published service yet; bind the member before reading or mutating its revisions.");

    private readonly record struct BoundServiceContext(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
        ServiceIdentity Identity,
        ServiceCatalogSnapshot Service,
        ServiceRevisionCatalogSnapshot? Revisions);

    private static StudioMemberEndpointContractResponse? BuildMemberEndpointContractResponse(
        string scopeId,
        string memberId,
        string publishedServiceId,
        string normalizedEndpointId,
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        StudioMemberInvocationReadinessResponse invocationReadiness)
    {
        var currentRevision = ServiceEndpointContractMath.ResolveCurrentContractRevision(
            service, revisions, normalizedEndpointId);
        var endpoint = currentRevision?.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal))
            ?? service.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            return null;

        var implementationKind = ServiceEndpointContractMath.NullIfEmpty(currentRevision?.ImplementationKind);
        var supportsSse = ServiceEndpointContractMath.IsChatEndpoint(endpoint.Kind);
        var streamFrameFormat = ServiceEndpointContractMath.ResolveStreamFrameFormat(
            supportsSse, implementationKind);
        var supportsAguiFrames = string.Equals(
            streamFrameFormat,
            ServiceEndpointContractMath.StreamFrameFormatAgui,
            StringComparison.Ordinal);
        var invokePath = supportsSse
            ? BuildMemberStreamInvokePath(scopeId, memberId, normalizedEndpointId)
            : BuildMemberInvokePath(scopeId, memberId, normalizedEndpointId);
        var responseContentType = supportsSse ? "text/event-stream" : "application/json";
        var defaultSmokeInputMode = supportsSse ? "prompt" : "typed-payload";
        var defaultSmokePrompt = supportsSse ? DefaultSmokePrompt : null;
        var sampleRequestJson = supportsSse
            ? null
            : ServiceEndpointContractMath.BuildTypedInvokeRequestExampleBody(
                endpoint.RequestTypeUrl, prettyPrinted: true);
        var smokeTestSupported = supportsSse || sampleRequestJson != null;

        return new StudioMemberEndpointContractResponse(
            ScopeId: scopeId,
            MemberId: memberId,
            PublishedServiceId: publishedServiceId,
            EndpointId: normalizedEndpointId,
            InvokePath: invokePath,
            Method: "POST",
            RequestContentType: "application/json",
            ResponseContentType: responseContentType,
            RequestTypeUrl: endpoint.RequestTypeUrl,
            ResponseTypeUrl: endpoint.ResponseTypeUrl,
            SupportsSse: supportsSse,
            SupportsWebSocket: false,
            SupportsAguiFrames: supportsAguiFrames,
            StreamFrameFormat: streamFrameFormat,
            SmokeTestSupported: smokeTestSupported,
            DefaultSmokeInputMode: defaultSmokeInputMode,
            DefaultSmokePrompt: defaultSmokePrompt,
            SampleRequestJson: sampleRequestJson,
            DeploymentStatus: service.DeploymentStatus,
            RevisionId: currentRevision?.RevisionId
                ?? ServiceEndpointContractMath.NullIfEmpty(service.DefaultServingRevisionId)
                ?? ServiceEndpointContractMath.NullIfEmpty(service.ActiveServingRevisionId)
                ?? string.Empty,
            InvocationReadiness: invocationReadiness,
            CurlExample: smokeTestSupported
                ? BuildCurlExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null,
            FetchExample: smokeTestSupported
                ? BuildFetchExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null);
    }

    private static string BuildMemberInvokePath(string scopeId, string memberId, string endpointId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(memberId)}/invoke/{Uri.EscapeDataString(endpointId)}";

    private static string BuildMemberStreamInvokePath(string scopeId, string memberId, string endpointId) =>
        $"{BuildMemberInvokePath(scopeId, memberId, endpointId)}:stream";

    private static string BuildCurlExample(string invokePath, bool supportsSse, string? requestTypeUrl)
    {
        if (supportsSse)
        {
            var requestBody = System.Text.Json.JsonSerializer.Serialize(new { prompt = DefaultSmokePrompt });
            return $"""
curl -N -X POST \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{requestBody}'
""";
        }

        var typedBody = ServiceEndpointContractMath.BuildTypedInvokeRequestExampleBody(
            requestTypeUrl, prettyPrinted: false) ?? "{}";
        return $"""
curl -X POST \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{typedBody}'
""";
    }

    private static string BuildFetchExample(string invokePath, bool supportsSse, string? requestTypeUrl)
    {
        if (supportsSse)
        {
            return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Accept": "text/event-stream",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    prompt: "{{DefaultSmokePrompt}}",
  }),
});

// Consume response.body as an SSE stream.
""";
        }

        var normalizedRequestTypeUrl = ServiceEndpointContractMath.NullIfEmpty(requestTypeUrl) ?? "<type-url>";
        var payloadBase64 = ServiceEndpointContractMath.BuildBase64PayloadPlaceholder(normalizedRequestTypeUrl);
        return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    payloadTypeUrl: "{{normalizedRequestTypeUrl}}",
    payloadBase64: "{{payloadBase64}}",
  }),
});
""";
    }

}
