namespace Aevatar.Studio.Application.Studio.Contracts;

/// <summary>
/// Stable wire status values returned in
/// <see cref="ProvisionWorkflowResponse.BindingStatus"/>. The provision flow is
/// non-blocking: it composes the existing member create + bind services and
/// observes the binding run read model once before deciding whether a schedule
/// may fire. The status therefore describes the binding usability observed at
/// response time:
/// <list type="bullet">
///   <item><c>pending</c> — the bind was accepted but has not reached a usable
///   terminal state; any created schedule is disabled.</item>
///   <item><c>bound</c> — the bind read model reports success and the schedule,
///   when requested, may be enabled.</item>
///   <item><c>failed</c> / <c>rejected</c> — the bind read model reports a
///   terminal unusable result; provision schedules for this ownership key are
///   disabled.</item>
/// </list>
/// A validation failure (missing YAML / caller credential) is surfaced as an
/// exception, not a status value, so the endpoint maps it to a 4xx.
/// </summary>
public static class ProvisionWorkflowBindingStatusNames
{
    /// <summary>
    /// Legacy value retained for wire compatibility with older callers. New
    /// provisioning responses use <see cref="Pending"/> for non-terminal binds.
    /// </summary>
    public const string Accepted = "accepted";

    /// <summary>The bind was accepted but is not yet usable.</summary>
    public const string Pending = "pending";

    /// <summary>The binding run succeeded and the member is usable.</summary>
    public const string Bound = "bound";

    /// <summary>The binding run failed.</summary>
    public const string Failed = "failed";

    /// <summary>The binding run was rejected.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// Caller NyxID subject identity used to re-mint a short-lived access token for
/// every scheduled-dispatch fire. This is NOT a raw bearer token: the scheduled
/// dispatch <see cref="Aevatar.GAgentService.Abstractions.Schedules.ScheduledServiceInvocationAuth"/>
/// carries a subject reference (the caller's NyxID binding) plus a capability
/// <see cref="Scope"/>, and exchanges it for a fresh token at fire time
/// (durable across token expiry — required for a recurring monitor). The endpoint
/// resolves this from the request and threads it into the service as an input
/// parameter; the service never reads an ambient identity.
/// </summary>
public sealed record ProvisionWorkflowCallerCredential(
    string Platform,
    string ExternalUserId,
    string Scope,
    string? Tenant = null)
{
    /// <summary>
    /// Canonical capability scope for a caller reaching aevatar through the nyxid
    /// proxy downstream. Mirrors the workflow-schedule path's <c>proxy</c> scope.
    /// </summary>
    public const string DefaultScope = "proxy";
}

/// <summary>
/// Single-call workflow provisioning request. The caller supplies the workflow
/// body inline (YAML) plus a prompt; the service composes member create + bind +
/// scheduled-dispatch so a Claude Code session reaches a runnable, scope-owned
/// workflow in one proxied call. No serviceId / memberId / workflowId is accepted
/// — those are minted internally and returned.
///
/// The run is produced asynchronously by a scheduled-dispatch. By default a
/// near-future one-shot fire is created so the caller sees a single demo run;
/// supplying <see cref="Cron"/> turns it into a recurring monitor schedule.
///
/// <see cref="Caller"/> is the caller's NyxID subject reference, re-minted into a
/// short-lived token for every fire (mirrors the workflow-schedule path, which
/// also requires the caller to supply the subject ref — a forwarded bearer token
/// cannot be converted into the subject reference the dispatch needs). It is a
/// body field rather than an ambient claim because the binding's platform key is
/// environment-specific and C1 stays config-free.
/// </summary>
public sealed record ProvisionWorkflowRequest(
    string DisplayName,
    string WorkflowYaml,
    string? Prompt = null,
    bool RunImmediately = true,
    string? Cron = null,
    string? Timezone = null,
    ProvisionWorkflowCallerCredential? Caller = null)
{
    /// <summary>
    /// Delay ahead of "now" for the synthesized one-shot fire when no recurring
    /// <see cref="Cron"/> is supplied. Short enough to feel immediate after a
    /// successful bind, while pending binds keep the synthesized schedule disabled.
    /// </summary>
    public const int DefaultOneShotDelaySeconds = 30;
}

/// <summary>
/// Binding failure observed while provisioning.
/// </summary>
public sealed record ProvisionWorkflowBindingFailureResponse(
    string Code,
    string Message,
    DateTimeOffset FailedAt);

/// <summary>
/// Result of a single-call provision. The bind and the run are both asynchronous,
/// so no run id is returned at provision time; the run appears in the Observatory
/// (<see cref="ObservatoryUrl"/>) only after the binding read model reports a
/// usable bind and the schedule is enabled. <see cref="BindingRunId"/> and
/// <see cref="BindingRunStatus"/> let the caller follow the authoritative bind
/// read model. <see cref="StudioUrl"/> is the editable Studio member page and is
/// null until the member is assigned to a team (a freshly provisioned member has
/// no team).
/// </summary>
public sealed record ProvisionWorkflowResponse(
    string MemberId,
    string ScopeId,
    string BindingStatus,
    string ObservatoryUrl)
{
    public string? BindingRunId { get; init; }

    public string? BindingRunStatus { get; init; }

    public ProvisionWorkflowBindingFailureResponse? BindingFailure { get; init; }

    public string? ScheduleId { get; init; }

    public string? StudioUrl { get; init; }
}
