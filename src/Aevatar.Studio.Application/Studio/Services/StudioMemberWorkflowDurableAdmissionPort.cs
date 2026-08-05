using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowDurableAdmissionPort :
    IStudioMemberWorkflowDurableAdmissionPort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ProvisionalRevisionDigestVersion =
        "studio-member-durable-provisional-revision.v1";
    private const string RevisionDigestVersion = "studio-member-durable-revision.v2";
    private const string RevisionSeedWorkflowId = "workflow-revision-seed";
    private const string RevisionSeedRevisionId = "revision-seed";

    private readonly IStudioMemberService _memberService;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogReader;
    private readonly IWorkflowExplicitRequestPreviewService _previewService;
    private readonly IWorkflowExternalCapabilityAdmissionService _admissionService;
    private readonly IStudioMemberWorkflowBindingPort _bindingPort;

    public StudioMemberWorkflowDurableAdmissionPort(
        IStudioMemberService memberService,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        IWorkflowExplicitRequestPreviewService previewService,
        IWorkflowExternalCapabilityAdmissionService admissionService,
        IStudioMemberWorkflowBindingPort bindingPort)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _revisionCatalogReader = revisionCatalogReader
            ?? throw new ArgumentNullException(nameof(revisionCatalogReader));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _bindingPort = bindingPort ?? throw new ArgumentNullException(nameof(bindingPort));
    }

    public async Task<StudioMemberWorkflowDurableAdmissionResult> AdmitAsync(
        StudioMemberWorkflowDurableAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var memberId = NormalizeRequired(request.MemberId, nameof(request.MemberId));
        var admissionContext = request.CapabilityAdmission
            ?? throw new InvalidOperationException("durable_admission_context_required");
        if (admissionContext.ExecutionMode != ExternalCapabilityExecutionMode.Durable)
            throw new InvalidOperationException("durable_admission_execution_mode_required");

        var member = await _memberService.GetAsync(scopeId, memberId, ct);
        var resolved = ResolveMemberIdentity(member, scopeId, memberId);
        var servingContract = await ResolveServingContractAsync(
            scopeId,
            memberId,
            resolved.PublishedServiceId,
            ct);
        var servingRevisionId = NormalizeRequired(
            servingContract.RevisionId,
            nameof(servingContract.RevisionId));

        var identity = BuildServiceIdentity(scopeId, resolved.PublishedServiceId);
        var catalog = await _revisionCatalogReader.GetAsync(identity, ct);
        if (!catalog.TryGetPreparedArtifact(servingRevisionId, out var artifact))
            throw new InvalidOperationException("serving_revision_artifact_unavailable");

        var workflowPlan = ValidateServingArtifact(
            artifact,
            identity,
            resolved.WorkflowId,
            servingRevisionId);
        var servingAdmissionPlan = ValidateServingAdmissionPlan(
            workflowPlan.CapabilityAdmissionPlan,
            resolved.WorkflowId,
            servingRevisionId);
        if (servingAdmissionPlan.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
        {
            return new StudioMemberWorkflowDurableAdmissionResult(
                StudioMemberWorkflowDurableAdmissionStatus.AlreadyDurable,
                scopeId,
                resolved.TeamId,
                memberId,
                resolved.WorkflowId,
                resolved.PublishedServiceId,
                servingRevisionId,
                servingRevisionId,
                string.Empty,
                "ready");
        }

        var provisionalRevisionId = BuildRevisionId(
            "rev-durable-provisional-",
            ProvisionalRevisionDigestVersion,
            scopeId,
            memberId,
            resolved.PublishedServiceId,
            resolved.WorkflowId,
            servingRevisionId,
            artifact.ArtifactHash,
            servingAdmissionPlan.AdmissionDigest);
        var inlineWorkflowYamls = workflowPlan.InlineWorkflowYamls.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value,
            StringComparer.Ordinal);
        var preview = await _previewService.PreviewAsync(
            new WorkflowExplicitRequestPreviewRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    scopeId,
                    admissionContext.CallerId,
                    admissionContext.NyxIdCallerCredential,
                    admissionContext.NyxIdOrganizationBearerToken),
                workflowPlan.WorkflowYaml,
                inlineWorkflowYamls,
                ExternalCapabilityExecutionMode.Durable,
                resolved.WorkflowId,
                provisionalRevisionId),
            ct);
        var confirmations = BuildConfirmations(preview, resolved.WorkflowId, provisionalRevisionId);
        var provisionalDurablePlan = await _admissionService.AdmitAsync(
            new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    scopeId,
                    admissionContext.CallerId,
                    admissionContext.NyxIdCallerCredential,
                    admissionContext.NyxIdOrganizationBearerToken),
                workflowPlan.WorkflowYaml,
                inlineWorkflowYamls,
                "studio_member_workflow_durable_admission",
                ExternalCapabilityExecutionMode.Durable,
                confirmations,
                resolved.WorkflowId,
                provisionalRevisionId),
            ct);
        var revisionSeedPlan = WorkflowCapabilityAdmissionPlanIntegrity
            .RebindExplicitRequestBindingIdentity(
                provisionalDurablePlan,
                workflowPlan.WorkflowYaml,
                inlineWorkflowYamls,
                RevisionSeedWorkflowId,
                RevisionSeedRevisionId);
        var targetRevisionId = BuildRevisionId(
            "rev-durable-",
            RevisionDigestVersion,
            scopeId,
            memberId,
            resolved.PublishedServiceId,
            resolved.WorkflowId,
            servingRevisionId,
            artifact.ArtifactHash,
            revisionSeedPlan.AdmissionDigest);
        var durablePlan = WorkflowCapabilityAdmissionPlanIntegrity
            .RebindExplicitRequestBindingIdentity(
                provisionalDurablePlan,
                workflowPlan.WorkflowYaml,
                inlineWorkflowYamls,
                resolved.WorkflowId,
                targetRevisionId);
        var existingRevision = catalog?.Revisions.FirstOrDefault(revision =>
            string.Equals(revision.RevisionId, targetRevisionId, StringComparison.Ordinal));
        if (existingRevision is not null)
        {
            ValidateExistingTargetRevision(
                existingRevision,
                identity,
                resolved.WorkflowId,
                targetRevisionId,
                workflowPlan.WorkflowYaml,
                inlineWorkflowYamls,
                durablePlan);
            var existingObserved = await _memberService.GetEndpointContractAsync(
                scopeId,
                memberId,
                WorkflowInvokeEndpointId,
                ct);
            var existingReady = IsExactReadyTarget(
                existingObserved,
                scopeId,
                memberId,
                resolved.PublishedServiceId,
                targetRevisionId);
            return new StudioMemberWorkflowDurableAdmissionResult(
                existingReady
                    ? StudioMemberWorkflowDurableAdmissionStatus.RevisionReady
                    : StudioMemberWorkflowDurableAdmissionStatus.RevisionAccepted,
                scopeId,
                resolved.TeamId,
                memberId,
                resolved.WorkflowId,
                resolved.PublishedServiceId,
                servingRevisionId,
                targetRevisionId,
                string.Empty,
                existingRevision.Status);
        }
        var bindingContext = new WorkflowCapabilityAdmissionContext(
            admissionContext.CallerId,
            admissionContext.NyxIdCallerCredential,
            admissionContext.NyxIdOrganizationBearerToken,
            ExternalCapabilityExecutionMode.Durable,
            existingPlan: durablePlan);
        var binding = await _bindingPort.BindAsync(
            new StudioMemberWorkflowBindingRequest(
                scopeId,
                memberId,
                workflowPlan.WorkflowYaml)
            {
                WorkflowId = resolved.WorkflowId,
                RevisionId = targetRevisionId,
                CapabilityAdmission = bindingContext,
            },
            ct);
        ValidateBindingReceipt(
            binding,
            scopeId,
            memberId,
            resolved.WorkflowId,
            targetRevisionId);

        var observed = await _memberService.GetEndpointContractAsync(
            scopeId,
            memberId,
            WorkflowInvokeEndpointId,
            ct);
        var ready = IsExactReadyTarget(
            observed,
            scopeId,
            memberId,
            resolved.PublishedServiceId,
            targetRevisionId);
        return new StudioMemberWorkflowDurableAdmissionResult(
            ready
                ? StudioMemberWorkflowDurableAdmissionStatus.RevisionReady
                : StudioMemberWorkflowDurableAdmissionStatus.RevisionAccepted,
            scopeId,
            resolved.TeamId,
            memberId,
            resolved.WorkflowId,
            resolved.PublishedServiceId,
            servingRevisionId,
            targetRevisionId,
            binding.Operation,
            binding.Status);
    }

    private async Task<StudioMemberEndpointContractResponse> ResolveServingContractAsync(
        string scopeId,
        string memberId,
        string publishedServiceId,
        CancellationToken ct)
    {
        var contract = await _memberService.GetEndpointContractAsync(
            scopeId,
            memberId,
            WorkflowInvokeEndpointId,
            ct);
        if (contract is null || !contract.InvocationReadiness.CanInvoke)
            throw new InvalidOperationException("serving_revision_not_ready");
        if (!MatchesContractIdentity(
                contract,
                scopeId,
                memberId,
                publishedServiceId,
                contract.RevisionId))
        {
            throw new InvalidOperationException("serving_revision_identity_mismatch");
        }

        return contract;
    }

    private static ResolvedMemberIdentity ResolveMemberIdentity(
        StudioMemberDetailResponse member,
        string scopeId,
        string memberId)
    {
        if (!string.Equals(member.Summary.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(member.Summary.MemberId, memberId, StringComparison.Ordinal) ||
            !string.Equals(
                member.Summary.ImplementationKind,
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("member_workflow_identity_mismatch");
        }

        var publishedServiceId = NormalizeRequired(
            member.Summary.PublishedServiceId,
            nameof(member.Summary.PublishedServiceId));
        var teamId = NormalizeRequired(member.Summary.TeamId, nameof(member.Summary.TeamId));
        var implementation = member.ImplementationRef
            ?? throw new InvalidOperationException("member_workflow_identity_unavailable");
        if (!string.Equals(
                implementation.ImplementationKind,
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("member_workflow_identity_mismatch");
        }

        return new ResolvedMemberIdentity(
            teamId,
            publishedServiceId,
            NormalizeRequired(implementation.WorkflowId, nameof(implementation.WorkflowId)));
    }

    private static WorkflowServiceDeploymentPlan ValidateServingArtifact(
        PreparedServiceRevisionArtifact artifact,
        ServiceIdentity expectedIdentity,
        string workflowId,
        string revisionId)
    {
        if (artifact.Identity is null ||
            !ServiceIdentityEquals(artifact.Identity, expectedIdentity) ||
            artifact.ImplementationKind != ServiceImplementationKind.Workflow ||
            !string.Equals(artifact.RevisionId, revisionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(artifact.ArtifactHash))
        {
            throw new InvalidOperationException("serving_revision_artifact_identity_mismatch");
        }

        var workflowPlan = artifact.DeploymentPlan?.WorkflowPlan
            ?? throw new InvalidOperationException("serving_revision_workflow_plan_unavailable");
        if (string.IsNullOrWhiteSpace(workflowPlan.WorkflowYaml) ||
            !string.Equals(workflowPlan.WorkflowId, workflowId, StringComparison.Ordinal) ||
            !string.Equals(workflowPlan.RevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("serving_revision_workflow_identity_mismatch");
        }

        return workflowPlan.Clone();
    }

    private static WorkflowCapabilityAdmissionPlan ValidateServingAdmissionPlan(
        WorkflowCapabilityAdmissionPlan? plan,
        string workflowId,
        string revisionId)
    {
        if (plan is null ||
            !string.Equals(
                plan.SchemaVersion,
                WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                plan.AdmissionDigest,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan),
                StringComparison.Ordinal) ||
            plan.ExecutionMode is not (ExternalCapabilityExecutionMode.Interactive or
                ExternalCapabilityExecutionMode.Durable))
        {
            throw new InvalidOperationException("serving_revision_admission_plan_invalid");
        }

        string? previousCallSiteId = null;
        var explicitRequestCount = 0;
        foreach (var admission in plan.InvocationAdmissions)
        {
            WorkflowCapabilityAdmissionPlanIntegrity.ValidateInvocationAdmissionIntrinsicIntegrity(admission);
            if (previousCallSiteId is not null &&
                string.CompareOrdinal(previousCallSiteId, admission.CallSiteId) >= 0)
            {
                throw new InvalidOperationException("serving_revision_admission_plan_invalid");
            }
            previousCallSiteId = admission.CallSiteId;
            if (plan.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
                !AllowsDurableExecution(admission))
            {
                throw new InvalidOperationException("serving_revision_admission_plan_invalid");
            }
            if (admission.Capability?.CapabilityCase !=
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
            {
                continue;
            }

            explicitRequestCount++;
            var grant = admission.NyxIdExplicitRequestGrant;
            if (grant is null ||
                !string.Equals(grant.WorkflowId, workflowId, StringComparison.Ordinal) ||
                !string.Equals(grant.RevisionId, revisionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("serving_revision_admission_plan_invalid");
            }
        }
        if (explicitRequestCount == 0)
            throw new InvalidOperationException("serving_revision_explicit_request_unavailable");

        return plan.Clone();
    }

    private static bool AllowsDurableExecution(WorkflowCapabilityInvocationAdmission admission)
    {
        var policy = admission.Capability?.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                admission.Capability.NyxIdUserService.ExecutionPolicy,
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest =>
                admission.Capability.NyxIdUserRequest.ExecutionPolicy,
            _ => null,
        };
        if (policy is not null &&
            !policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable))
        {
            return false;
        }

        return admission.NyxIdExplicitRequestGrant is null ||
               admission.NyxIdExplicitRequestGrant.AllowedExecutionModes.Contains(
                   ExternalCapabilityExecutionMode.Durable);
    }

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation> BuildConfirmations(
        WorkflowExplicitRequestPreviewResult preview,
        string workflowId,
        string revisionId)
    {
        if (!string.Equals(preview.WorkflowId, workflowId, StringComparison.Ordinal) ||
            !string.Equals(preview.RevisionId, revisionId, StringComparison.Ordinal) ||
            preview.Items.Count == 0)
        {
            throw new InvalidOperationException("durable_explicit_request_preview_invalid");
        }

        var confirmations = new List<NyxIdExplicitRequestConfirmation>(preview.Items.Count);
        string? previousCallSiteId = null;
        foreach (var item in preview.Items.OrderBy(static item => item.CallSiteId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(item.CallSiteId) ||
                string.IsNullOrWhiteSpace(item.RequestContractDigest) ||
                item.EffectiveRisk is not (NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or
                    NyxIdOperationRisk.Destructive) ||
                !item.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) ||
                string.Equals(previousCallSiteId, item.CallSiteId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("durable_explicit_request_preview_invalid");
            }
            previousCallSiteId = item.CallSiteId;
            confirmations.Add(new NyxIdExplicitRequestConfirmation
            {
                CallSiteId = item.CallSiteId,
                RequestContractDigest = item.RequestContractDigest,
                AttestedRisk = item.EffectiveRisk,
                WorkflowId = workflowId,
                RevisionId = revisionId,
            });
        }

        return confirmations;
    }

    private static void ValidateBindingReceipt(
        StudioMemberWorkflowBindingResult binding,
        string scopeId,
        string memberId,
        string workflowId,
        string revisionId)
    {
        if (!binding.Success ||
            !string.Equals(binding.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(binding.MemberId, memberId, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkflowId, workflowId, StringComparison.Ordinal) ||
            !string.Equals(binding.RevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("durable_revision_binding_receipt_invalid");
        }
    }

    private static bool IsExactReadyTarget(
        StudioMemberEndpointContractResponse? contract,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string revisionId) =>
        contract is not null &&
        contract.InvocationReadiness.CanInvoke &&
        MatchesContractIdentity(contract, scopeId, memberId, publishedServiceId, revisionId);

    private static void ValidateExistingTargetRevision(
        ServiceRevisionSnapshot revision,
        ServiceIdentity identity,
        string workflowId,
        string revisionId,
        string workflowYaml,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        WorkflowCapabilityAdmissionPlan expectedPlan)
    {
        if (revision.Status is not (nameof(ServiceRevisionStatus.Prepared) or
                nameof(ServiceRevisionStatus.Published)) ||
            revision.PreparedArtifact is null ||
            !string.Equals(
                revision.ArtifactHash,
                revision.PreparedArtifact.ArtifactHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("durable_revision_existing_artifact_unavailable");
        }

        var workflowPlan = ValidateServingArtifact(
            revision.PreparedArtifact,
            identity,
            workflowId,
            revisionId);
        var actualPlan = ValidateServingAdmissionPlan(
            workflowPlan.CapabilityAdmissionPlan,
            workflowId,
            revisionId);
        if (!string.Equals(workflowPlan.WorkflowYaml, workflowYaml, StringComparison.Ordinal) ||
            workflowPlan.InlineWorkflowYamls.Count != inlineWorkflowYamls.Count ||
            inlineWorkflowYamls.Any(entry =>
                !workflowPlan.InlineWorkflowYamls.TryGetValue(entry.Key, out var yaml) ||
                !string.Equals(yaml, entry.Value, StringComparison.Ordinal)) ||
            !actualPlan.Equals(expectedPlan))
        {
            throw new InvalidOperationException("durable_revision_existing_artifact_mismatch");
        }
    }

    private static bool MatchesContractIdentity(
        StudioMemberEndpointContractResponse contract,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string revisionId) =>
        string.Equals(contract.ScopeId, scopeId, StringComparison.Ordinal) &&
        string.Equals(contract.MemberId, memberId, StringComparison.Ordinal) &&
        string.Equals(contract.PublishedServiceId, publishedServiceId, StringComparison.Ordinal) &&
        string.Equals(contract.EndpointId, WorkflowInvokeEndpointId, StringComparison.Ordinal) &&
        string.Equals(contract.RevisionId, revisionId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(contract.InvocationReadiness.RevisionId) ||
         string.Equals(
             contract.InvocationReadiness.RevisionId,
             revisionId,
             StringComparison.Ordinal));

    private static ServiceIdentity BuildServiceIdentity(string scopeId, string publishedServiceId) =>
        new()
        {
            TenantId = scopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = publishedServiceId,
        };

    private static bool ServiceIdentityEquals(ServiceIdentity left, ServiceIdentity right) =>
        string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
        string.Equals(left.AppId, right.AppId, StringComparison.Ordinal) &&
        string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal) &&
        string.Equals(left.ServiceId, right.ServiceId, StringComparison.Ordinal);

    private static string BuildRevisionId(
        string prefix,
        string digestVersion,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string workflowId,
        string servingRevisionId,
        string artifactHash,
        string admissionDigest)
    {
        var canonical = string.Join(
            "\n",
            digestVersion,
            scopeId,
            memberId,
            publishedServiceId,
            workflowId,
            servingRevisionId,
            artifactHash,
            admissionDigest);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}{Convert.ToHexStringLower(digest.AsSpan(0, 16))}";
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException($"{fieldName}_required")
            : normalized;
    }

    private sealed record ResolvedMemberIdentity(
        string TeamId,
        string PublishedServiceId,
        string WorkflowId);
}
