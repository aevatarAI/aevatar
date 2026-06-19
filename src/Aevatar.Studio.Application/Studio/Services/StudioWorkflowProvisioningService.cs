using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
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
/// The caller credential is a NyxID <b>subject reference</b> (not a raw token):
/// the scheduled dispatch exchanges it for a short-lived token on every fire, so a
/// recurring monitor schedule keeps working past the caller's session-token
/// expiry. <paramref name="scopeId"/> and the caller credential are always input
/// parameters — the service holds no HttpContext and no infrastructure
/// dependency, only application ports.
/// </summary>
public sealed class StudioWorkflowProvisioningService : IStudioWorkflowProvisioningService
{
    // Workflow members publish a single "chat" endpoint; a scheduled dispatch to
    // it produces a workflow run (mirrors the workflow-schedule mapper).
    private const string WorkflowInvokeEndpointId = "chat";

    private const string ObservatoryPath = "/workflow/observatory";

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly TimeProvider _timeProvider;

    public StudioWorkflowProvisioningService(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        TimeProvider? timeProvider = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
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
        var auth = BuildSenderNyxIdAuth(callerCredential);

        // 1. Create the member (kind = workflow). The actor stamps the rename-safe
        //    published service id at creation, so we read it straight back — no
        //    poll, no recompute of the convention.
        var member = await _memberService.CreateAsync(
            normalizedScopeId,
            new CreateStudioMemberRequest(
                DisplayName: displayName,
                ImplementationKind: MemberImplementationKindNames.Workflow),
            ct);

        var memberId = member.MemberId;
        var publishedServiceId = NormalizeRequired(
            member.PublishedServiceId, nameof(member.PublishedServiceId));

        // 2. Bind the inline workflow YAML. WorkflowId is a stable identifier the
        //    bind contract requires; we mint one so the caller only supplies YAML.
        //    The bind is asynchronous — we do NOT poll it to completion.
        var bindReceipt = await _memberService.BindAsync(
            normalizedScopeId,
            memberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    WorkflowId: GenerateWorkflowId(),
                    WorkflowYamls: [workflowYaml])),
            ct);

        // 3. Create the scheduled-dispatch that produces the run. The Workflow kind
        //    is what flips on caller-token projection; the dispatch re-mints the
        //    token from the subject ref on every fire. A schedule is created when
        //    there is something to fire — a recurring monitor (caller Cron) or a
        //    one-shot demo (RunImmediately). RunImmediately=false with no Cron is an
        //    honest "bind only": no schedule, no run.
        string? scheduleId = null;
        if (ShouldSchedule(request))
        {
            var schedule = await _scheduleService.CreateAsync(
                BuildScheduleConfiguration(
                    normalizedScopeId,
                    publishedServiceId,
                    request.Prompt ?? string.Empty,
                    auth,
                    ResolveCron(request, out var timezone),
                    timezone),
                ct);
            scheduleId = NormalizeOptional(schedule.ScheduleId);
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
            StudioUrl = BuildStudioUrl(normalizedScopeId, member.TeamId, memberId),
        };
    }

    /// <summary>
    /// Builds the scheduled-dispatch configuration: a Workflow-kind service
    /// invocation targeting the bound member's <c>chat</c> endpoint with the
    /// caller's prompt, carrying the caller subject ref so the dispatch re-mints a
    /// token to project onto the run.
    /// </summary>
    private static ScheduledDispatchConfiguration BuildScheduleConfiguration(
        string scopeId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        string cronExpression,
        string timezone) =>
        new(
            ScheduleId: string.Empty, // minted by the schedule service
            DisplayName: $"provision-{publishedServiceId}",
            Target: new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    Identity: new ServiceIdentity
                    {
                        TenantId = scopeId,
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

    private static ScheduledServiceInvocationAuth BuildSenderNyxIdAuth(
        ProvisionWorkflowCallerCredential credential) =>
        new(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(
                Platform: NormalizeRequired(credential.Platform, nameof(credential.Platform)),
                Tenant: NormalizeOptional(credential.Tenant) ?? string.Empty,
                ExternalUserId: NormalizeRequired(credential.ExternalUserId, nameof(credential.ExternalUserId))),
            Scope: NormalizeRequired(credential.Scope, nameof(credential.Scope))));

    private static string? BuildStudioUrl(string scopeId, string? teamId, string memberId)
    {
        var normalizedTeamId = NormalizeOptional(teamId);
        if (normalizedTeamId == null)
            return null;

        return $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(normalizedTeamId)}/members/{Uri.EscapeDataString(memberId)}/workflow";
    }

    private static string GenerateWorkflowId() => $"workflow-{Guid.NewGuid():N}";

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
