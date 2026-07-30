using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Mainnet.Host.Api.Skills;

// Invoke request body read manually via ReadFromJsonAsync.
internal sealed record SkillInvokeRequest(string? Prompt = null);

// Returned to the page after a one-shot invoke. ObservatoryUrl deep-links to the created run's detail.
public sealed record SkillRunReceipt(
    string RunId,
    string WorkflowName,
    string RunKind,
    string ObservatoryUrl);

internal sealed record SkillRunOutcome(
    bool Succeeded,
    SkillRunReceipt? Receipt = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static SkillRunOutcome Ok(SkillRunReceipt receipt) => new(true, receipt);

    public static SkillRunOutcome Failed(string code, string message) => new(false, null, code, message);
}

// Schedule request body for POST /api/workflow/skills/{guid}/schedule (read via ReadFromJsonAsync).
internal sealed record SkillScheduleHttpRequest(
    string? Prompt = null,
    string? CronExpression = null,
    string? Timezone = null,
    string? DisplayName = null,
    string? TeamId = null);

// Returned after a schedule is provisioned; ObservatoryUrl shows the schedule's recurring runs.
public sealed record SkillScheduleReceipt(
    string ScheduleId,
    string MemberId,
    string TeamId,
    string Status,
    string ObservatoryUrl,
    string StudioUrl);

internal sealed record SkillScheduleOutcome(
    bool Succeeded,
    SkillScheduleReceipt? Receipt = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static SkillScheduleOutcome Ok(SkillScheduleReceipt receipt) => new(true, receipt);

    public static SkillScheduleOutcome Failed(string code, string message) => new(false, null, code, message);
}

// Invokes a visible ornn skill once as an observable workflow run, or provisions a recurring schedule for it.
// Caller credentials are INPUTS resolved from the caller at the endpoint, not read from HttpContext here.
internal interface IUserSkillRunService
{
    Task<SkillRunOutcome> InvokeOnceAsync(
        string skillGuid,
        WorkflowCallerCredential callerCredential,
        string scopeId,
        string prompt,
        CancellationToken ct = default);

    Task<SkillScheduleOutcome> ScheduleAsync(
        string skillGuid,
        WorkflowCallerCredential callerCredential,
        string scopeId,
        string prompt,
        string cronExpression,
        string timezone,
        string displayName,
        string teamId,
        CancellationToken ct = default);
}
