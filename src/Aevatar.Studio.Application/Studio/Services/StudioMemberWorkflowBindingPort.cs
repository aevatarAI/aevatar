using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowBindingPort : IStudioMemberWorkflowBindingPort
{
    private readonly IStudioMemberService _memberService;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly IScopeWorkflowSaveAndBindPort _saveAndBindPort;
    private readonly IStudioMemberCommandPort _memberCommandPort;

    public StudioMemberWorkflowBindingPort(
        IStudioMemberService memberService,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        IScopeWorkflowSaveAndBindPort saveAndBindPort,
        IStudioMemberCommandPort memberCommandPort)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _capabilityAdmissionService = capabilityAdmissionService
            ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _saveAndBindPort = saveAndBindPort ?? throw new ArgumentNullException(nameof(saveAndBindPort));
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
    }

    public async Task<StudioMemberWorkflowBindingResult> BindAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowId = ResolveWorkflowId(request);
        var revisionId = NormalizeOptional(request.RevisionId);
        var suppliedAdmission = request.CapabilityAdmission;
        var callerId = suppliedAdmission?.CallerId ?? string.Empty;
        var callerCredential = suppliedAdmission?.NyxIdCallerCredential;
        var organizationBearerToken = suppliedAdmission?.NyxIdOrganizationBearerToken;
        var existingPlan = suppliedAdmission?.ExistingPlan?.Clone();
        var explicitRequestConfirmations = suppliedAdmission?.ExplicitRequestConfirmations ?? [];
        var executionMode = suppliedAdmission?.ExecutionMode
            ?? ExternalCapabilityExecutionMode.Interactive;
        var capabilityAdmissionPlan = existingPlan is not null
            ? await _capabilityAdmissionService.RevalidatePersistedAsync(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    existingPlan,
                    request.WorkflowYaml,
                    new Dictionary<string, string>(),
                    "studio_member_workflow_binding",
                    executionMode,
                    workflowId,
                    revisionId),
                ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    request.ScopeId,
                    callerId,
                    callerCredential,
                    organizationBearerToken),
                request.WorkflowYaml,
                new Dictionary<string, string>(),
                "studio_member_workflow_binding",
                executionMode,
                explicitRequestConfirmations,
                workflowId,
                revisionId),
                ct);
        var trustedAdmission = new WorkflowCapabilityAdmissionContext(
            callerId,
            executionMode: executionMode,
            existingPlan: capabilityAdmissionPlan);

        try
        {
            var member = await _memberService.GetAsync(request.ScopeId, request.MemberId, ct);
            return IsPublished(member)
                ? await SaveAndBindPublishedMemberAsync(request, member, trustedAdmission, ct)
                : await BindUnpublishedMemberAsync(request, capabilityAdmissionPlan, trustedAdmission, ct);
        }
        catch (StudioMemberNotFoundException)
        {
            return await BindUnpublishedMemberAsync(request, capabilityAdmissionPlan, trustedAdmission, ct);
        }
    }

    private async Task<StudioMemberWorkflowBindingResult> BindUnpublishedMemberAsync(
        StudioMemberWorkflowBindingRequest request,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan,
        WorkflowCapabilityAdmissionContext trustedAdmission,
        CancellationToken ct)
    {
        var workflowId = ResolveWorkflowId(request);
        var receipt = await _memberService.BindAsync(
            request.ScopeId,
            request.MemberId,
            new UpdateStudioMemberBindingRequest(
                RevisionId: NormalizeOptional(request.RevisionId),
                Workflow: new StudioMemberWorkflowBindingSpec(
                    workflowId,
                    [request.WorkflowYaml])
                {
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                })
            {
                CapabilityAdmission = trustedAdmission,
            },
            ct);

        return new StudioMemberWorkflowBindingResult(
            Success: true,
            ScopeId: receipt.ScopeId,
            MemberId: receipt.MemberId,
            Operation: StudioMemberWorkflowBindingOperationNames.Bind,
            Status: receipt.Status,
            BindingRunId: receipt.BindingRunId,
            AckStage: receipt.AckStage,
            BindingRunRole: receipt.BindingRunRole,
            WorkflowId: workflowId);
    }

    private async Task<StudioMemberWorkflowBindingResult> SaveAndBindPublishedMemberAsync(
        StudioMemberWorkflowBindingRequest request,
        StudioMemberDetailResponse member,
        WorkflowCapabilityAdmissionContext trustedAdmission,
        CancellationToken ct)
    {
        if (!string.Equals(member.Summary.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Studio member '{request.MemberId}' implementation kind '{member.Summary.ImplementationKind}' cannot be bound with a workflow.");
        }

        var publishedServiceId = member.Summary.PublishedServiceId;
        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"Studio member '{request.MemberId}' is already published but has no published service id.");
        }

        var workflowId = ResolveWorkflowId(request);
        var result = await _saveAndBindPort.SaveAndBindAsync(
            new ScopeWorkflowSaveAndBindRequest(
                request.ScopeId,
                workflowId,
                request.WorkflowYaml,
                WorkflowName: workflowId,
                DisplayName: member.Summary.DisplayName,
                InlineWorkflowYamls: null,
                AppId: "studio",
                ServiceId: publishedServiceId,
                ExposureDesired: true,
                RevisionId: NormalizeOptional(request.RevisionId))
            {
                CapabilityAdmission = trustedAdmission,
            },
            ct);

        await _memberCommandPort.RecordPublishedBindingAsync(
            request.ScopeId,
            request.MemberId,
            new StudioMemberPublishedBindingRecordRequest(
                PublishedServiceId: publishedServiceId,
                RevisionId: result.RevisionId,
                ImplementationKind: MemberImplementationKindNames.Workflow,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.Workflow,
                    WorkflowId: result.WorkflowId,
                    WorkflowRevision: result.RevisionId),
                ExpectedActorId: result.Binding.ExpectedActorId),
            ct);

        return new StudioMemberWorkflowBindingResult(
            Success: true,
            ScopeId: result.ScopeId,
            MemberId: request.MemberId,
            Operation: StudioMemberWorkflowBindingOperationNames.SaveAndBind,
            Status: result.AcceptanceStage,
            WorkflowId: result.WorkflowId,
            RevisionId: result.RevisionId);
    }

    private static bool IsPublished(StudioMemberDetailResponse member) =>
        member.LastBinding is not null || !string.IsNullOrWhiteSpace(member.Summary.LastBoundRevisionId);

    private static string ResolveWorkflowId(StudioMemberWorkflowBindingRequest request) =>
        string.IsNullOrWhiteSpace(request.WorkflowId)
            ? $"workflow-{BuildWorkflowKey(request.ScopeId, request.MemberId)}"
            : request.WorkflowId.Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    private static string BuildWorkflowKey(string scopeId, string memberId)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{memberId}");
        var hash = SHA256.HashData(identity);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
