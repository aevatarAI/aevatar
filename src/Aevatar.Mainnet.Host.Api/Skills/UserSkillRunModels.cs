using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.AI.ToolProviders.Skills;

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
    string? TeamId = null,
    string? WorkflowConfirmationToken = null);

// Returned after actor-owned schedule provisioning is accepted. ScheduleId remains null until
// the schedule becomes visible; clients poll the member read model through the response Location.
public sealed record SkillScheduleReceipt(
    string MemberId,
    string ScopeId,
    string TeamId,
    string BindingStatus,
    string ObservatoryUrl,
    string StudioUrl)
{
    public string? ScheduleId { get; init; }

    public string? BindingRunId { get; init; }

    public string? ScheduleProvisioningId { get; init; }

    public string? ScheduleProvisioningStatus { get; init; }
}

public sealed record SkillScheduleConfirmationReceipt(
    string Status,
    string? ConfirmationToken,
    IReadOnlyList<SkillWorkflowMountPreview> Workflows,
    string? FailureCode = null,
    string? Message = null);

internal sealed record SkillScheduleOutcome(
    bool Succeeded,
    SkillScheduleReceipt? Receipt = null,
    SkillScheduleConfirmationReceipt? Confirmation = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? RequiredUserServiceIds = null)
{
    public static SkillScheduleOutcome Ok(SkillScheduleReceipt receipt) => new(true, receipt);

    public static SkillScheduleOutcome ConfirmationRequired(SkillScheduleConfirmationReceipt confirmation) =>
        new(false, Confirmation: confirmation);

    public static SkillScheduleOutcome Failed(
        string code,
        string message,
        IReadOnlyList<string>? requiredUserServiceIds = null) =>
        new(
            false,
            ErrorCode: code,
            ErrorMessage: message,
            RequiredUserServiceIds: requiredUserServiceIds);
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
        string workflowConfirmationToken,
        CancellationToken ct = default);
}
