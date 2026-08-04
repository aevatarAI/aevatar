using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowDraftProvisioningService :
    IStudioMemberWorkflowDraftProvisioningPort
{
    private readonly IStudioMemberProvisioningPort _memberProvisioningPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly AppScopedWorkflowService _workflowDraftService;

    public StudioMemberWorkflowDraftProvisioningService(
        IStudioMemberProvisioningPort memberProvisioningPort,
        IStudioMemberQueryPort memberQueryPort,
        IWorkflowDefinitionParser workflowDefinitionParser,
        AppScopedWorkflowService workflowDraftService)
    {
        _memberProvisioningPort = memberProvisioningPort
            ?? throw new ArgumentNullException(nameof(memberProvisioningPort));
        _memberQueryPort = memberQueryPort
            ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _workflowDefinitionParser = workflowDefinitionParser
            ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _workflowDraftService = workflowDraftService
            ?? throw new ArgumentNullException(nameof(workflowDraftService));
    }

    public async Task<StudioMemberWorkflowDraftProvisioningResult> SaveAsync(
        StudioMemberWorkflowDraftProvisioningRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var teamId = NormalizeRequired(request.TeamId, nameof(request.TeamId));
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        var workflowYaml = NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));

        var parsed = await _workflowDefinitionParser.ParseWorkflowYamlAsync(workflowYaml, ct);
        if (!parsed.Succeeded)
            throw new InvalidOperationException(parsed.Error);

        var provisionKey = StudioWorkflowProvisioningService.BuildProvisionKey(
            scopeId,
            teamId,
            displayName);
        var suppliedMemberId = NormalizeOptional(request.MemberId);
        var memberId = suppliedMemberId ?? $"wf-{provisionKey}";
        var workflowId = NormalizeOptional(request.WorkflowId) ?? $"workflow-{provisionKey}";

        var existingMember = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
        if (existingMember is null && suppliedMemberId is not null)
        {
            throw new StudioMemberWorkflowDraftProvisioningException(
                StudioMemberWorkflowDraftErrorCodes.MemberNotFound,
                $"Studio member '{memberId}' was not found in the current scope.",
                memberId);
        }

        if (existingMember is not null)
        {
            ValidateMember(existingMember.Summary, teamId);
        }
        else
        {
            var created = await _memberProvisioningPort.CreateAsync(
                new StudioMemberProvisioningRequest(
                    scopeId,
                    displayName,
                    MemberImplementationKindNames.Workflow)
                {
                    MemberId = memberId,
                    TeamId = teamId,
                },
                ct);
            memberId = NormalizeRequired(created.MemberId, nameof(created.MemberId));
        }

        var accepted = await SaveDraftAsync(
            scopeId,
            workflowId,
            parsed.WorkflowName,
            workflowYaml,
            memberId,
            ct);
        return BuildResult(scopeId, teamId, memberId, workflowId, parsed, accepted);
    }

    private async Task<WorkflowDraftCreateAcceptedResponse> SaveDraftAsync(
        string scopeId,
        string workflowId,
        string workflowName,
        string workflowYaml,
        string memberId,
        CancellationToken ct)
    {
        try
        {
            return await _workflowDraftService.SaveDraftAsync(
                scopeId,
                workflowId,
                new SaveWorkflowDraftRequest(
                    AppScopedWorkflowService.BuildScopeDirectoryId(scopeId),
                    workflowName,
                    FileName: $"{workflowId}.yaml",
                    workflowYaml),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new StudioMemberWorkflowDraftProvisioningException(
                StudioMemberWorkflowDraftErrorCodes.DraftSaveFailed,
                "The workflow member exists, but its draft save command was not accepted. Retry with the returned member_id.",
                memberId);
        }
    }

    private static StudioMemberWorkflowDraftProvisioningResult BuildResult(
        string scopeId,
        string teamId,
        string memberId,
        string workflowId,
        WorkflowYamlParseResult parsed,
        WorkflowDraftCreateAcceptedResponse accepted)
    {
        var unresolved = parsed.AuthorizationDependencies?.ExternalInvocations.Any(static invocation =>
            WorkflowAuthorizationDependencyEvaluator.RequiresExternalCapabilityAdmission(invocation.ToolName) &&
            invocation.Selector.SelectorCase ==
            Aevatar.Workflow.Abstractions.ExternalWorkflowCapabilitySelector.SelectorOneofCase.None) == true;
        var blocker = unresolved
            ? new StudioMemberWorkflowDraftBlocker(
                StudioMemberWorkflowDraftBlockerCodes.NyxIdOperationSelectionRequired,
                "Select an exact NyxID operation before binding this draft.")
            : new StudioMemberWorkflowDraftBlocker(
                StudioMemberWorkflowDraftBlockerCodes.WorkflowBindRequired,
                "Bind this draft before scheduling or running it.");

        return new StudioMemberWorkflowDraftProvisioningResult(
            StudioMemberWorkflowDraftStatusNames.SaveAccepted,
            Runnable: false,
            StudioMemberWorkflowDraftStatusNames.NotBound,
            scopeId,
            teamId,
            memberId,
            workflowId,
            $"{StudioWorkflowProvisioningService.BuildStudioUrl(scopeId, teamId, memberId)}?workflowId={Uri.EscapeDataString(workflowId)}",
            accepted.CommandId,
            accepted.AckStage,
            accepted.ActorId,
            accepted.WorkspaceId,
            accepted.ExpectedVersion,
            accepted.AckedAtUtc,
            new StudioMemberWorkflowDraftReadiness(
                accepted.Readiness.Readable,
                accepted.Readiness.Stage,
                accepted.Readiness.Message),
            [blocker]);
    }

    private static void ValidateMember(
        StudioMemberSummaryResponse member,
        string teamId)
    {
        if (!string.Equals(member.TeamId?.Trim(), teamId, StringComparison.Ordinal))
        {
            throw new StudioMemberWorkflowDraftProvisioningException(
                StudioMemberWorkflowDraftErrorCodes.MemberTeamMismatch,
                $"Studio member '{member.MemberId}' does not belong to Team '{teamId}'.",
                member.MemberId);
        }

        if (!string.Equals(
                member.ImplementationKind?.Trim(),
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal))
        {
            throw new StudioMemberWorkflowDraftProvisioningException(
                StudioMemberWorkflowDraftErrorCodes.MemberKindMismatch,
                $"Studio member '{member.MemberId}' is not a workflow member.",
                member.MemberId);
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
