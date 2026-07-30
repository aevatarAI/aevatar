using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions;

namespace Aevatar.Studio.Application.Provisioning;

public static class WorkflowScheduleProvisioningStageNames
{
    public const string BindAccepted = "bind_accepted";

    public const string ScheduleAccepted = "schedule_accepted";

    public const string ScheduleBlocked = "schedule_blocked";
}

public static class WorkflowScheduleProvisioningScheduleStatusNames
{
    public const string NotRequested = "not_requested";

    public const string Accepted = "accepted";

    public const string Blocked = "blocked";
}

public sealed record WorkflowScheduleProvisioningStageFailure(
    string Stage,
    string Code,
    string Message);

/// <summary>
/// Request to provision a runnable, Observatory-delivered workflow schedule.
///
/// The caller supplies the target <see cref="TeamId"/>, workflow body inline
/// (YAML), and scheduling intent; the adapter composes Team-owned member-create
/// + bind + a <c>ScheduleKind=Workflow</c> scheduled-dispatch so the runs land in
/// the Observatory. No serviceId / memberId / workflowId is accepted — those are
/// minted internally and returned.
///
/// <see cref="ScopeId"/> and <see cref="CallerSubjectExternalUserId"/> /
/// <see cref="CallerSubjectPlatform"/> identify the owning scope and the caller's
/// NyxID subject reference (re-minted into a short-lived token on every fire). On
/// the channel-free studio path the scope IS the caller's NyxID subject, so the
/// subject platform defaults to <c>nyxid</c> and the external user id defaults to
/// the scope owner subject. Schedule auth never accepts, persists, or replays a
/// caller bearer token — the subject reference is the only credential source.
/// </summary>
public sealed record WorkflowScheduleProvisioningRequest(
    string ScopeId,
    string TeamId,
    string DisplayName,
    string WorkflowYaml)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }

    /// <summary>Optional user prompt the scheduled run starts from.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Standard 5-field cron expression (minute hour day-of-month month
    /// day-of-week) in <see cref="ScheduleTimezone"/>. When null/blank a single
    /// near-future demo fire is synthesized (subject to <see cref="RunImmediately"/>).
    /// </summary>
    public string? ScheduleCron { get; init; }

    /// <summary>IANA timezone for <see cref="ScheduleCron"/> evaluation.</summary>
    public string? ScheduleTimezone { get; init; }

    /// <summary>
    /// When true (default) a demo run fires shortly after the bind even without a
    /// recurring cron. False with no cron is an honest "bind only" — no schedule,
    /// no run.
    /// </summary>
    public bool RunImmediately { get; init; } = true;

    /// <summary>
    /// Caller NyxID subject platform (re-mint subject reference). Defaults to
    /// <c>nyxid</c> for the channel-free studio path where the scope is the
    /// caller's NyxID subject.
    /// </summary>
    public string CallerSubjectPlatform { get; init; } = "nyxid";

    /// <summary>
    /// Caller NyxID subject external user id. On the studio path this is the scope
    /// owner subject (== <see cref="ScopeId"/>); the adapter substitutes
    /// <see cref="ScopeId"/> when this is null/blank.
    /// </summary>
    public string? CallerSubjectExternalUserId { get; init; }

    /// <summary>Optional caller NyxID subject tenant.</summary>
    public string? CallerSubjectTenant { get; init; }
}

/// <summary>
/// Result of provisioning a workflow schedule. The bind and any run are
/// asynchronous, so no run id is returned; runs appear in the Observatory
/// (<see cref="ObservatoryUrl"/>) only when <see cref="ScheduleStatus"/> is
/// <see cref="WorkflowScheduleProvisioningScheduleStatusNames.Accepted"/> and
/// the accepted <see cref="ScheduleId"/> fires. Contains NO channel / Lark / bot
/// fields.
/// </summary>
public sealed record WorkflowScheduleProvisioningResult(
    string MemberId,
    string WorkflowId,
    string ScopeId,
    string TeamId,
    string BindingStatus,
    string ObservatoryUrl,
    string StudioUrl)
{
    public string? ScheduleId { get; init; }

    public string? BindingRunId { get; init; }

    public string ProvisioningStage { get; init; } = WorkflowScheduleProvisioningStageNames.ScheduleAccepted;

    public string ScheduleStatus { get; init; } = WorkflowScheduleProvisioningScheduleStatusNames.Accepted;

    public WorkflowScheduleProvisioningStageFailure? StageFailure { get; init; }
}
