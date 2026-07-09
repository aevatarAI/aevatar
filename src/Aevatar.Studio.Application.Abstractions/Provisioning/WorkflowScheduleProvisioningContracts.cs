namespace Aevatar.Studio.Application.Provisioning;

/// <summary>
/// Request to provision a runnable, Observatory-delivered workflow schedule.
///
/// The caller supplies the workflow body inline (YAML) plus scheduling intent; the
/// adapter composes member-create + bind + a <c>ScheduleKind=Workflow</c>
/// scheduled-dispatch so the runs land in the Observatory. No serviceId / memberId
/// / workflowId is accepted — those are minted internally and returned.
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
    string DisplayName,
    string WorkflowYaml)
{
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
/// Result of provisioning a workflow schedule. The bind and the run are both
/// asynchronous, so no run id is returned; the runs appear in the Observatory
/// (<see cref="ObservatoryUrl"/>) as the <see cref="ScheduleId"/> fires.
/// Contains NO channel / Lark / bot fields.
/// </summary>
public sealed record WorkflowScheduleProvisioningResult(
    string MemberId,
    string ScopeId,
    string BindingStatus,
    string ObservatoryUrl)
{
    public string? ScheduleId { get; init; }

    public string? BindingRunId { get; init; }

    public string? BindingRunStatus { get; init; }

    public WorkflowScheduleProvisioningFailure? BindingFailure { get; init; }
}

public sealed record WorkflowScheduleProvisioningFailure(
    string Code,
    string Message,
    DateTimeOffset FailedAt);
