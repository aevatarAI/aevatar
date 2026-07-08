using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// One-call workflow provisioning facade (C1). Composes the existing member-first
/// services — it reinvents nothing: create a member via
/// <see cref="IStudioMemberService.CreateAsync"/>, bind the inline workflow YAML
/// via <see cref="IStudioMemberService.BindAsync"/>, then create a
/// <b>scheduled-dispatch</b> (via <see cref="IScheduledDispatchApplicationService"/>)
/// that produces the run under the caller scope.
///
/// The flow is deliberately NON-BLOCKING. Binding a workflow member is an
/// asynchronous pipeline that can take minutes, so a synchronous handler that
/// polled the bind to completion would exhaust the gateway timeout and never
/// invoke. Instead the run is produced by a scheduled-dispatch:
/// <list type="bullet">
///   <item>it fires after the bind publishes the deterministic
///   <c>member-{memberId}</c> service (an early fire simply retries on the
///   schedule's recurrence);</item>
///   <item>because the schedule kind is <see cref="ScheduledDispatchScheduleKind.Workflow"/>,
///   the dispatch projects a freshly re-minted caller NyxID token onto the run's
///   <c>ChatRequestEvent</c> (<c>LlmControl.SenderNyxIdAccessToken</c>), so the
///   run's LLM calls authenticate — the one thing a direct
///   <c>IServiceInvocationPort.InvokeAsync</c> could not provide.</item>
/// </list>
///
/// The schedule carries EXACTLY ONE credential source, chosen by
/// <see cref="BuildScheduleAuth"/> to stay valid for the schedule's whole
/// lifetime: a re-mintable NyxID subject reference. The dispatch exchanges it for
/// a fresh token on every fire, past session-token expiry. The scope id and the
/// caller credential are always input parameters — the service holds no
/// HttpContext and no infrastructure dependency, only application ports.
///
/// Two invariants keep the non-blocking flow from leaking resources:
/// <list type="bullet">
///   <item><b>Validate before provisioning.</b> The workflow YAML is parsed
///   synchronously with the same <see cref="IWorkflowDefinitionParser"/> the bind
///   pipeline uses. Invalid YAML throws with the parser's error message and
///   provisions NOTHING — no member, no schedule — so an authoring agent can
///   repair the YAML and retry without leaving garbage behind.</item>
///   <item><b>Retries converge.</b> One (scope, display name) pair owns exactly
///   one member, one workflow id, and one schedule: the member id is derived
///   deterministically from that pair (an existing member is reused, never
///   re-created), and the schedule uses a deterministic id via
///   <see cref="IScheduledDispatchApplicationService.EnsureAsync"/> (idempotent
///   upsert). Re-provisioning the same display name re-binds and re-schedules the
///   same resources instead of accumulating a new member + enabled schedule per
///   attempt. That reuse is also the documented ownership rule: a display name
///   identifies ONE automation, and re-provisioning it replaces that member's
///   workflow. When the pair's schedule was explicitly deleted, the schedule id
///   advances to the next generation instead of resurrecting the tombstone (see
///   <see cref="EnsureProvisionScheduleAsync"/>).</item>
/// </list>
/// </summary>
public sealed class StudioWorkflowProvisioningService : IStudioWorkflowProvisioningService
{
    // Workflow members publish a single "chat" endpoint; a scheduled dispatch to
    // it produces a workflow run (mirrors the workflow-schedule mapper).
    private const string WorkflowInvokeEndpointId = "chat";

    private const string ObservatoryPath = "/workflow/observatory";

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly TimeProvider _timeProvider;

    public StudioWorkflowProvisioningService(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IWorkflowDefinitionParser workflowDefinitionParser,
        TimeProvider? timeProvider = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _workflowDefinitionParser = workflowDefinitionParser
            ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProvisionWorkflowResponse> ProvisionAsync(
        string scopeId,
        ProvisionWorkflowCallerCredential callerCredential,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerCredential);
        ArgumentNullException.ThrowIfNull(request);
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        var workflowYaml = NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));

        // 0. Validate the YAML with the same parser the bind pipeline runs, BEFORE
        //    any resource exists. Invalid YAML must provision nothing; the parser's
        //    error message goes back to the caller so it can repair and retry.
        var parseResult = await _workflowDefinitionParser.ParseWorkflowYamlAsync(workflowYaml, ct);
        if (!parseResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"workflow_yaml is not a valid workflow definition: {parseResult.Error}");
        }

        var subjectRef = BuildSenderNyxIdCredentialSource(callerCredential);

        // Provision identity: one (scope, display name) pair owns exactly one
        // member + workflow id + schedule, so retries converge on the same
        // resources instead of leaving an orphan pair per attempt.
        var provisionKey = BuildProvisionKey(normalizedScopeId, displayName);

        // 1. Resolve the member: reuse the existing one for this (scope, display
        //    name), else create it. The deterministic id is the member's identity;
        //    its display name is a mutable label — re-creating after a rename
        //    would hard-conflict at the actor, so an already-provisioned member is
        //    read from the readmodel and never re-created. The actor stamps the
        //    rename-safe published service id at creation, so both paths read it
        //    straight back — no poll, no recompute of the convention.
        var (memberId, publishedServiceId, teamId) = await ResolveProvisionedMemberAsync(
            normalizedScopeId, displayName, $"wf-{provisionKey}", ct);

        // 2. Bind the inline workflow YAML. WorkflowId is a stable identifier the
        //    bind contract requires; deriving it from the provision key keeps one
        //    logical workflow identity across re-binds of the same member.
        //    The bind is asynchronous — we do NOT poll it to completion.
        var bindReceipt = await _memberService.BindAsync(
            normalizedScopeId,
            memberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    WorkflowId: $"workflow-{provisionKey}",
                    WorkflowYamls: [workflowYaml])),
            ct);

        // 3. Create the scheduled-dispatch that produces the run. The Workflow kind
        //    is what flips on caller-token projection. A schedule is created when
        //    there is something to fire — a recurring monitor (caller Cron) or a
        //    one-shot demo (RunImmediately). RunImmediately=false with no Cron is an
        //    honest "bind only": no schedule, no run (and nothing to credential).
        //
        //    The schedule carries EXACTLY ONE credential source (the validator admits
        //    no more), a re-mintable subject reference. Raw bearer tokens are never
        //    persisted in schedule state.
        //
        //    The schedule id is deterministic (provision-{publishedServiceId}) and
        //    the write goes through EnsureAsync, so a re-provision updates the one
        //    existing schedule instead of stacking a new enabled schedule per retry.
        string? scheduleId = null;
        if (ShouldSchedule(request))
        {
            var cronExpression = ResolveCron(request, out var timezone);
            var auth = BuildScheduleAuth(subjectRef);
            scheduleId = await EnsureProvisionScheduleAsync(
                normalizedScopeId,
                publishedServiceId,
                request.Prompt ?? string.Empty,
                auth,
                cronExpression,
                timezone,
                ct);
        }

        return new ProvisionWorkflowResponse(
            MemberId: memberId,
            ScopeId: normalizedScopeId,
            BindingStatus: ProvisionWorkflowBindingStatusNames.Accepted,
            ObservatoryUrl: ObservatoryPath)
        {
            BindingRunId = NormalizeOptional(bindReceipt.BindingRunId),
            ScheduleId = scheduleId,
            // The editable Studio page is team-scoped; a freshly provisioned
            // member has no team yet, so the link is only built once one exists.
            StudioUrl = BuildStudioUrl(normalizedScopeId, teamId, memberId),
        };
    }

    /// <summary>
    /// Resolves the provisioned member for one (scope, display name) pair:
    /// reuse it when it already exists, create it otherwise. The deterministic
    /// member id is the identity; the display name is a mutable label, so an
    /// existing member is read from the readmodel and never re-created — a
    /// re-create after a rename would hard-conflict at the actor. A member
    /// created moments ago may not be materialized yet; that create falls
    /// through to the actor's idempotent no-op for identical identity fields.
    /// </summary>
    private async Task<(string MemberId, string PublishedServiceId, string? TeamId)> ResolveProvisionedMemberAsync(
        string scopeId,
        string displayName,
        string memberId,
        CancellationToken ct)
    {
        try
        {
            var existing = await _memberService.GetAsync(scopeId, memberId, ct);
            return (
                existing.Summary.MemberId,
                NormalizeRequired(existing.Summary.PublishedServiceId, nameof(existing.Summary.PublishedServiceId)),
                existing.Summary.TeamId);
        }
        catch (StudioMemberNotFoundException)
        {
            var created = await _memberService.CreateAsync(
                scopeId,
                new CreateStudioMemberRequest(
                    DisplayName: displayName,
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    MemberId: memberId),
                ct);
            return (
                created.MemberId,
                NormalizeRequired(created.PublishedServiceId, nameof(created.PublishedServiceId)),
                created.TeamId);
        }
    }

    /// <summary>
    /// Converges the provision schedule onto a deterministic id. A deleted
    /// schedule is a permanent tombstone (the platform rejects reconfiguring it
    /// as typed not-found), so an explicit user delete advances the id to the
    /// next generation (<c>provision-{serviceId}</c>, <c>provision-{serviceId}.2</c>, …):
    /// retries still converge on the first live generation, and a deleted pair
    /// can be re-provisioned without resurrecting the deleted schedule.
    /// </summary>
    private async Task<string?> EnsureProvisionScheduleAsync(
        string scopeId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        string cronExpression,
        string timezone,
        CancellationToken ct)
    {
        const int maxGenerations = 50;
        for (var generation = 1; generation <= maxGenerations; generation++)
        {
            var scheduleId = generation == 1
                ? $"provision-{publishedServiceId}"
                : $"provision-{publishedServiceId}.{generation}";
            try
            {
                var schedule = await _scheduleService.EnsureAsync(
                    BuildScheduleConfiguration(
                        scheduleId, scopeId, publishedServiceId, prompt, auth, cronExpression, timezone),
                    ct);
                return NormalizeOptional(schedule.ScheduleId);
            }
            catch (ScheduledDispatchNotFoundException)
            {
                // Tombstoned by an explicit delete — advance to the next generation.
            }
        }

        throw new InvalidOperationException(
            $"Provisioning for service '{publishedServiceId}' exhausted {maxGenerations} deleted schedule generations.");
    }

    /// <summary>
    /// Builds the scheduled-dispatch configuration: a Workflow-kind service
    /// invocation targeting the bound member's <c>chat</c> endpoint with the
    /// caller's prompt, carrying the single resolved credential source that the
    /// dispatch projects onto the run.
    /// </summary>
    private static ScheduledDispatchConfiguration BuildScheduleConfiguration(
        string scheduleId,
        string scopeId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        string cronExpression,
        string timezone) =>
        new(
            // Deterministic id: EnsureAsync converges retries onto one schedule.
            // '.'/'-' stay inside the scheduled-dispatch id charset ([A-Za-z0-9._-]).
            ScheduleId: scheduleId,
            DisplayName: $"provision-{publishedServiceId}",
            Target: new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    Identity: new ServiceIdentity
                    {
                        TenantId = scopeId,
                        AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                        Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                        ServiceId = publishedServiceId,
                    },
                    EndpointId: WorkflowInvokeEndpointId,
                    Payload: Any.Pack(new ChatRequestEvent
                    {
                        Prompt = prompt,
                        ScopeId = scopeId,
                    }),
                    Auth: auth)),
            CronExpression: cronExpression,
            Timezone: timezone,
            Enabled: true,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow);

    /// <summary>
    /// A schedule (and therefore a run) is created when there is something to
    /// fire: a recurring monitor (caller-supplied <see cref="ProvisionWorkflowRequest.Cron"/>)
    /// or a one-shot demo (<see cref="ProvisionWorkflowRequest.RunImmediately"/>).
    /// </summary>
    private static bool ShouldSchedule(ProvisionWorkflowRequest request) =>
        request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron);

    /// <summary>
    /// Resolves the cron expression. A caller-supplied recurring cron is a
    /// monitor; otherwise a one-shot cron pinned to a near-future minute is
    /// synthesized so a single demo run fires shortly after the bind. The minute
    /// granularity matches the standard 5-field cron the dispatch validator
    /// accepts; the dispatch's recurrence harmlessly re-fires if the bind has not
    /// completed by the first tick.
    /// </summary>
    private string ResolveCron(ProvisionWorkflowRequest request, out string timezone)
    {
        var callerCron = NormalizeOptional(request.Cron);
        if (callerCron != null)
        {
            timezone = ScheduledDispatchCalculator.NormalizeTimezone(request.Timezone);
            return callerCron;
        }

        // Demo fire: pin a fixed minute/hour/day/month in UTC at the next whole
        // minute at/after now+delay. Standard 5-field cron has no year field, so
        // this technically recurs on that calendar date annually — acceptable for
        // a throwaway demo schedule (it fires once in the current run window; the
        // caller deletes it, and a recurring monitor supplies its own Cron). The
        // round-up is required because a cron never fires within the current
        // partial minute.
        timezone = ScheduledDispatchCalculator.DefaultTimezone;
        var fireAt = _timeProvider
            .GetUtcNow()
            .AddSeconds(ProvisionWorkflowRequest.DefaultOneShotDelaySeconds)
            .UtcDateTime;
        var fireMinute = new DateTime(
            fireAt.Year, fireAt.Month, fireAt.Day, fireAt.Hour, fireAt.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);
        return $"{fireMinute.Minute} {fireMinute.Hour} {fireMinute.Day} {fireMinute.Month} *";
    }

    /// <summary>
    /// Selects the schedule's single authoritative credential source. A scheduled
    /// dispatch admits EXACTLY ONE source (the create-time validator rejects more,
    /// and the dispatch never combines them), so this never attaches a fallback.
    /// Raw bearer tokens are intentionally excluded from schedule state; the
    /// caller's NyxID subject reference is the durable identity and the dispatch
    /// re-mints a fresh sender token from it on every fire.
    /// </summary>
    private static ScheduledServiceInvocationAuth BuildScheduleAuth(
        ScheduledServiceInvocationNyxIdCredentialSource subjectRef) =>
        new(SenderNyxId: subjectRef);

    private static ScheduledServiceInvocationNyxIdCredentialSource BuildSenderNyxIdCredentialSource(
        ProvisionWorkflowCallerCredential credential) =>
        new(new ScheduledServiceInvocationNyxIdSubjectRef(
                Platform: NormalizeRequired(credential.Platform, nameof(credential.Platform)),
                Tenant: NormalizeOptional(credential.Tenant) ?? string.Empty,
                ExternalUserId: NormalizeRequired(credential.ExternalUserId, nameof(credential.ExternalUserId))),
            Scope: NormalizeRequired(credential.Scope, nameof(credential.Scope)));

    private static string? BuildStudioUrl(string scopeId, string? teamId, string memberId)
    {
        var normalizedTeamId = NormalizeOptional(teamId);
        if (normalizedTeamId == null)
            return null;

        return $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(normalizedTeamId)}/members/{Uri.EscapeDataString(memberId)}/workflow";
    }

    /// <summary>
    /// Deterministic provision identity for one (scope, display name) pair:
    /// 32 hex chars of SHA-256, so the derived member id (<c>wf-{key}</c>, 35
    /// chars) satisfies the member-id slug pattern and length cap while retries
    /// with the same display name land on the same member/workflow/schedule.
    /// </summary>
    private static string BuildProvisionKey(string scopeId, string displayName)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{displayName}");
        var hash = SHA256.HashData(identity);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
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
