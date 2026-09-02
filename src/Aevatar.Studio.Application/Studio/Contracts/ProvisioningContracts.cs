using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.Studio.Application.Studio.Contracts;

/// <summary>
/// Stable wire status values returned in
/// <see cref="ProvisionWorkflowResponse.BindingStatus"/>. The provision flow is
/// non-blocking: it composes the existing member create + bind services and then
/// hands the run off to a scheduled-dispatch rather than blocking the request on
/// the (multi-minute, asynchronous) bind. The status therefore describes the
/// provisioning hand-off, not the bind terminal state:
/// <list type="bullet">
///   <item><c>accepted</c> — the member was created, the inline workflow YAML bind
///   was accepted, and a scheduled-dispatch was created to produce the run once
///   the member is bound. Runs appear in the Observatory as the schedule fires;
///   the caller watches the Observatory, there is no synchronous run id.</item>
/// </list>
/// A validation failure (missing YAML / caller credential) is surfaced as an
/// exception, not a status value, so the endpoint maps it to a 4xx.
/// </summary>
public static class ProvisionWorkflowBindingStatusNames
{
    /// <summary>
    /// The member + bind + schedule were accepted; the run is produced
    /// asynchronously by the scheduled-dispatch (HTTP 202).
    /// </summary>
    public const string Accepted = "accepted";
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
/// body inline (YAML), a target Team, and a prompt; the service composes
/// Team-owned member create + bind + scheduled-dispatch so a Claude Code session
/// reaches a runnable, discoverable workflow in one proxied call. No serviceId /
/// memberId / workflowId is accepted — those are minted internally and returned.
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
    public IReadOnlyList<NyxIdExplicitRequestConfirmationInput>? ExplicitRequestConfirmations { get; init; }

    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }

    /// <summary>
    /// Target Studio Team that owns the provisioned workflow member. Required:
    /// Chat-created workflows must be discoverable through the Team member route
    /// before any member, binding, or schedule side effects are created.
    /// </summary>
    public string? TeamId { get; init; }

    [JsonIgnore]
    public AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner { get; init; }

    [JsonIgnore]
    public string? ProvisioningBearerToken { get; init; }

    [JsonIgnore]
    public string? ScheduleOperationId { get; init; }

    [JsonIgnore]
    public string? ScheduleIdempotencyKey { get; init; }

    /// <summary>
    /// Delay ahead of "now" for the synthesized one-shot fire when no recurring
    /// <see cref="Cron"/> is supplied. This near-future fire does not guarantee
    /// binding-terminal readiness; readiness and reconciliation remain tracked by
    /// issue #2679.
    /// </summary>
    public const int DefaultOneShotDelaySeconds = 30;
}

/// <summary>
/// Result of a single-call provision. The bind and the run are both asynchronous,
/// so no run id is returned at provision time; the run appears in the Observatory
/// (<see cref="ObservatoryUrl"/>) as the <see cref="ScheduleId"/> fires.
/// <see cref="BindingRunId"/> lets the caller poll the bind status if desired.
/// <see cref="StudioUrl"/> is the editable Studio member page under the owning
/// Team.
/// </summary>
public sealed record ProvisionWorkflowResponse(
    string MemberId,
    string ScopeId,
    string TeamId,
    string BindingStatus,
    string ObservatoryUrl)
{
    public string? BindingRunId { get; init; }

    public string? ScheduleId { get; init; }

    public string StudioUrl { get; init; } = string.Empty;
}
