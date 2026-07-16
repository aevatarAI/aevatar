using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowBindingPort : IStudioMemberWorkflowBindingPort
{
    private readonly IStudioMemberService _memberService;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IScopeWorkflowSaveAndBindPort _saveAndBindPort;
    private readonly IStudioMemberCommandPort _memberCommandPort;

    public StudioMemberWorkflowBindingPort(
        IStudioMemberService memberService,
        IWorkflowDefinitionParser workflowDefinitionParser,
        IScopeWorkflowSaveAndBindPort saveAndBindPort,
        IStudioMemberCommandPort memberCommandPort)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _workflowDefinitionParser = workflowDefinitionParser
            ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _saveAndBindPort = saveAndBindPort ?? throw new ArgumentNullException(nameof(saveAndBindPort));
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
    }

    public async Task<StudioMemberWorkflowBindingResult> BindAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parseResult = await _workflowDefinitionParser.ParseWorkflowYamlAsync(request.WorkflowYaml, ct);
        if (!parseResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"workflow_yaml is not a valid workflow definition: {parseResult.Error}");
        }

        try
        {
            var member = await _memberService.GetAsync(request.ScopeId, request.MemberId, ct);
            return IsPublished(member)
                ? await SaveAndBindPublishedMemberAsync(request, member, ct)
                : await BindUnpublishedMemberAsync(request, ct);
        }
        catch (StudioMemberNotFoundException)
        {
            return await BindUnpublishedMemberAsync(request, ct);
        }
    }

    private async Task<StudioMemberWorkflowBindingResult> BindUnpublishedMemberAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct)
    {
        var workflowId = ResolveWorkflowId(request);
        var receipt = await _memberService.BindAsync(
            request.ScopeId,
            request.MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    workflowId,
                    [request.WorkflowYaml])),
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
                ExposureDesired: true),
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

    private static string BuildWorkflowKey(string scopeId, string memberId)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{memberId}");
        var hash = SHA256.HashData(identity);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
