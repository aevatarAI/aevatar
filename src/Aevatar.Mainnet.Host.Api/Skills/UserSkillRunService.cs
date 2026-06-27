using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Application.Abstractions.Runs;

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
        string accessToken,
        string scopeId,
        string prompt,
        CancellationToken ct = default)
    {
        var skill = await _remoteSkillFetcher.FetchSkillAsync(accessToken, skillGuid, ct);
        if (skill == null)
            return SkillRunOutcome.Failed("skill_not_found", $"Skill '{skillGuid}' was not found or is not accessible.");

        var (runKind, yamls) = ResolveWorkflowYamls(skill);

        var request = new WorkflowChatRunRequest(
            Prompt: prompt ?? string.Empty,
            Source: WorkflowChatSource.InlineYamlBundle(yamls),
            ScopeId: scopeId,
            CallerCredential: new WorkflowCallerCredential(accessToken));

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
        string accessToken,
        string scopeId,
        string? ownerSubjectExternalUserId,
        string prompt,
        string cronExpression,
        string timezone,
        string displayName,
        CancellationToken ct = default)
    {
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
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? skill.Name : displayName,
            WorkflowYaml: yamls[0])
        {
            Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
            ScheduleCron = cronExpression,
            ScheduleTimezone = string.IsNullOrWhiteSpace(timezone) ? null : timezone,
            RunImmediately = false,
            CallerSubjectExternalUserId = string.IsNullOrWhiteSpace(ownerSubjectExternalUserId) ? null : ownerSubjectExternalUserId,
            CallerBearerToken = accessToken,
        };

        try
        {
            var result = await _scheduleProvisioningPort.ProvisionAsync(request, ct);
            return SkillScheduleOutcome.Ok(new SkillScheduleReceipt(
                ScheduleId: result.ScheduleId ?? string.Empty,
                MemberId: result.MemberId,
                Status: result.BindingStatus,
                ObservatoryUrl: result.ObservatoryUrl));
        }
        catch (InvalidOperationException ex)
        {
            return SkillScheduleOutcome.Failed("schedule_failed", ex.Message);
        }
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
