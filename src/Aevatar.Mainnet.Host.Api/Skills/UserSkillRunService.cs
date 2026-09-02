using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowCallerCredentialTokens = Aevatar.Workflow.Abstractions.WorkflowCallerCredentialTokens;
using ExternalCapabilityExecutionMode = Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode;

namespace Aevatar.Mainnet.Host.Api.Skills;

// Invokes an ornn skill once as an observable workflow run via the workflow-native chat-run command path:
// resolve the skill -> obtain its workflow YAML (carried, or a synthesized single llm_call) -> run it INLINE
// (no mount needed for a one-shot) -> return the run actor id for the observatory deep-link. Scheduling (a
// persisted target) is a separate path.
internal sealed class UserSkillRunService : IUserSkillRunService
{
    private const string ObservatoryRunPathPrefix = "/workflow/observatory?run=";

    private readonly IRemoteSkillFetcher _remoteSkillFetcher;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _chatRunDispatch;
    private readonly IWorkflowScheduleProvisioningPort _scheduleProvisioningPort;

    public UserSkillRunService(
        IRemoteSkillFetcher remoteSkillFetcher,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> chatRunDispatch,
        IWorkflowScheduleProvisioningPort scheduleProvisioningPort)
    {
        _remoteSkillFetcher = remoteSkillFetcher ?? throw new ArgumentNullException(nameof(remoteSkillFetcher));
        _chatRunDispatch = chatRunDispatch ?? throw new ArgumentNullException(nameof(chatRunDispatch));
        _scheduleProvisioningPort = scheduleProvisioningPort ?? throw new ArgumentNullException(nameof(scheduleProvisioningPort));
    }

    public async Task<SkillRunOutcome> InvokeOnceAsync(
        string skillGuid,
        WorkflowCallerCredential callerCredential,
        string scopeId,
        string prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerCredential);
        var parsedToken = WorkflowCallerCredentialTokens.ParseOptional(callerCredential.BearerToken);
        if (!parsedToken.IsValid)
            return SkillRunOutcome.Failed("invalid_caller_credential", "Caller credential is invalid.");

        var accessToken = parsedToken.NormalizedBearerToken!;
        var skill = await _remoteSkillFetcher.FetchSkillAsync(accessToken, skillGuid, ct);
        if (skill == null)
            return SkillRunOutcome.Failed("skill_not_found", $"Skill '{skillGuid}' was not found or is not accessible.");

        var (runKind, yamls) = ResolveWorkflowYamls(skill);

        var request = new WorkflowChatRunRequest(
            Prompt: prompt ?? string.Empty,
            Source: WorkflowChatSource.InlineYamlBundle(yamls),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            ScopeId: scopeId,
            CallerCredential: callerCredential);

        var dispatch = await _chatRunDispatch.DispatchAsync(request, ct);
        if (!dispatch.Succeeded || dispatch.Receipt == null)
            return SkillRunOutcome.Failed(dispatch.Error.ToString(), "Failed to start the skill workflow run.");

        var receipt = dispatch.Receipt;
        return SkillRunOutcome.Ok(new SkillRunReceipt(
            RunId: receipt.ActorId,
            WorkflowName: receipt.WorkflowName,
            RunKind: runKind,
            ObservatoryUrl: ObservatoryRunPathPrefix + Uri.EscapeDataString(receipt.ActorId)));
    }

    public async Task<SkillScheduleOutcome> ScheduleAsync(
        string skillGuid,
        WorkflowCallerCredential callerCredential,
        string scopeId,
        string prompt,
        string cronExpression,
        string timezone,
        string displayName,
        string teamId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerCredential);
        var parsedToken = WorkflowCallerCredentialTokens.ParseOptional(callerCredential.BearerToken);
        if (!parsedToken.IsValid)
            return SkillScheduleOutcome.Failed("invalid_caller_credential", "Caller credential is invalid.");

        var authenticatedOwner = BuildAuthenticatedOwner(callerCredential.NyxIdAuthority);
        if (authenticatedOwner is null)
            return SkillScheduleOutcome.Failed(
                "authenticated_authorization_owner_required",
                "Caller NyxID authority with verified binding is required to schedule the skill workflow.");

        var accessToken = parsedToken.NormalizedBearerToken!;
        var skill = await _remoteSkillFetcher.FetchSkillAsync(accessToken, skillGuid, ct);
        if (skill == null)
            return SkillScheduleOutcome.Failed("skill_not_found", $"Skill '{skillGuid}' was not found or is not accessible.");

        // The provisioning port persists a member + inline-bound workflow YAML + a Workflow-kind scheduled
        // dispatch, so recurring runs land in the observatory (the same path the LLM provisioning tool uses).
        // It takes a single entry YAML: a direct skill's synthesized workflow is one document; a carried skill
        // uses its entry workflow. Cron is Cronos CronFormat.Standard (5-field, no seconds).
        var (_, yamls) = ResolveWorkflowYamls(skill);
        var request = new WorkflowScheduleProvisioningRequest(
            ScopeId: scopeId,
            TeamId: teamId,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? skill.Name : displayName,
            WorkflowYaml: yamls[0])
        {
            Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
            ScheduleCron = cronExpression,
            ScheduleTimezone = string.IsNullOrWhiteSpace(timezone) ? null : timezone,
            RunImmediately = false,
            CallerSubjectPlatform = authenticatedOwner.SubjectPlatform,
            CallerSubjectTenant = authenticatedOwner.SubjectTenant,
            CallerSubjectExternalUserId = authenticatedOwner.SubjectExternalUserId,
            AuthenticatedOwner = authenticatedOwner,
            ProvisioningBearerToken = accessToken,
        };

        try
        {
            var result = await _scheduleProvisioningPort.ProvisionAsync(request, ct);
            return SkillScheduleOutcome.Ok(new SkillScheduleReceipt(
                ScheduleId: result.ScheduleId ?? string.Empty,
                MemberId: result.MemberId,
                TeamId: result.TeamId,
                Status: result.BindingStatus,
                ObservatoryUrl: result.ObservatoryUrl,
                StudioUrl: result.StudioUrl));
        }
        catch (StudioMemberAutomationProjectionPendingException ex)
        {
            return SkillScheduleOutcome.Failed(
                "schedule_authorization_projection_pending",
                $"The refreshed authorization catalog is still being projected. Retry this request. Required state version: {ex.RequiredStateVersion}.");
        }
        catch (StudioMemberAutomationCatalogRefreshUnavailableException ex)
        {
            return SkillScheduleOutcome.Failed(
                "schedule_authorization_refresh_unavailable",
                ex.Message);
        }
        catch (StudioMemberAutomationCatalogRefreshSupersededException ex)
        {
            return SkillScheduleOutcome.Failed(
                "schedule_authorization_refresh_superseded",
                ex.Message);
        }
        catch (StudioMemberAutomationPlanConflictException ex)
        {
            return SkillScheduleOutcome.Failed(
                ToPlanConflictCode(ex.Code),
                ToPlanConflictMessage(ex.Code));
        }
        catch (InvalidOperationException ex)
        {
            return SkillScheduleOutcome.Failed("schedule_failed", ex.Message);
        }
    }

    private static string ToPlanConflictCode(string code) => code switch
    {
        "authorization_plan_changed" => "schedule_authorization_plan_changed",
        "reauthorization_required" => "schedule_reauthorization_required",
        _ => "schedule_authorization_conflict",
    };

    private static string ToPlanConflictMessage(string code) => code switch
    {
        "authorization_plan_changed" => "The authorization plan changed before the schedule write. Retry this request.",
        "reauthorization_required" => "Reconnect NyxID to authorize this workflow schedule.",
        _ => "The workflow schedule authorization plan conflicted with the current state.",
    };

    private static AuthenticatedAuthorizationOwnerContext? BuildAuthenticatedOwner(
        WorkflowCallerNyxIdAuthority? authority)
    {
        if (authority == null ||
            string.IsNullOrWhiteSpace(authority.Platform) ||
            string.IsNullOrWhiteSpace(authority.ExternalUserId) ||
            string.IsNullOrWhiteSpace(authority.BindingId))
        {
            return null;
        }

        var tenant = string.IsNullOrWhiteSpace(authority.Tenant) ? string.Empty : authority.Tenant.Trim();
        var externalUserId = authority.ExternalUserId.Trim();
        return new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = externalUserId,
            },
            authority.Platform.Trim(),
            tenant,
            externalUserId,
            authority.BindingId.Trim());
    }

    // Skills carrying workflow YAML run as-is; skills without one run a synthesized single llm_call workflow
    // that injects the skill's full instructions. Either way the result is one observable workflow run.
    private static (string RunKind, IReadOnlyList<string> Yamls) ResolveWorkflowYamls(SkillDefinition skill)
    {
        if (skill.Workflows.Count > 0)
        {
            var yamls = skill.Workflows.SelectMany(static workflow => workflow.WorkflowYamls).ToList();
            if (yamls.Count > 0)
                return ("workflow", yamls);
        }

        return ("direct", [SkillDirectWorkflowYamlSynthesizer.Synthesize(skill)]);
    }
}
