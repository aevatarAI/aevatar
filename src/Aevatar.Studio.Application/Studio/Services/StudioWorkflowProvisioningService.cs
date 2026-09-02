using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// One-call workflow provisioning facade (C1). Composes the existing member-first
/// services — it reinvents nothing: create a member via
/// <see cref="IStudioMemberService.CreateAsync"/>, bind the inline workflow YAML
/// via <see cref="IStudioMemberWorkflowBindingPort"/>, then create a
/// Team-owned workflow schedule via <see cref="IStudioMemberWorkflowSchedulePort"/>
/// that produces the run under the caller scope.
///
/// The flow is deliberately NON-BLOCKING. Binding a workflow member is an
/// asynchronous pipeline that can take minutes, so a synchronous handler that
/// polled the bind to completion would exhaust the gateway timeout and never
/// invoke. Instead the run is produced by a scheduled-dispatch:
/// <list type="bullet">
///   <item>it schedules the requested run without polling the bind; binding-terminal
///   readiness and reconciliation when a one-shot fires before the deterministic
///   <c>member-{memberId}</c> service is callable remain tracked by issue #2679;</item>
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
///   <item><b>Admit before provisioning.</b> The unified external-capability
///   admission service parses the workflow and evaluates readiness before any
///   mutation. Invalid or non-ready workflows provision NOTHING — no member, no
///   schedule — so an authoring agent can repair the YAML and retry without
///   leaving garbage behind.</item>
///   <item><b>Retries converge.</b> One (scope, display name) pair owns exactly
///   one member, one workflow id, and one schedule inside one target Team: the
///   member id is derived deterministically from that ownership tuple (an
///   existing member is reused, never
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
    private const string ObservatoryPath = "/workflow/observatory";
    private const string CredentialProvisioningKind = "dedicated_scheduled_invocation_agent_key";

    private readonly IStudioMemberService _memberService;
    private readonly IStudioMemberWorkflowBindingPort _bindingPort;
    private readonly IStudioMemberWorkflowSchedulePort _schedulePort;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly TimeProvider _timeProvider;

    public StudioWorkflowProvisioningService(
        IStudioMemberService memberService,
        IStudioMemberWorkflowBindingPort bindingPort,
        IStudioMemberWorkflowSchedulePort schedulePort,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        TimeProvider? timeProvider = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _bindingPort = bindingPort ?? throw new ArgumentNullException(nameof(bindingPort));
        _schedulePort = schedulePort ?? throw new ArgumentNullException(nameof(schedulePort));
        _capabilityAdmissionService = capabilityAdmissionService
            ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
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
        var teamId = NormalizeRequired(request.TeamId, "teamId");
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        var workflowYaml = NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var provisionKey = BuildProvisionKey(normalizedScopeId, teamId, displayName);
        var workflowId = $"workflow-{provisionKey}";
        var revisionId = $"revision-{provisionKey}";

        var suppliedAdmission = request.CapabilityAdmission;
        var callerId = suppliedAdmission?.CallerId ?? string.Empty;
        var nyxIdCallerCredentialSelection = suppliedAdmission?.NyxIdCallerCredential;
        var organizationBearerToken = suppliedAdmission?.NyxIdOrganizationBearerToken;
        var existingPlan = suppliedAdmission?.ExistingPlan?.Clone();
        var explicitRequestConfirmations = suppliedAdmission?.ExplicitRequestConfirmations ?? [];
        var executionMode = ShouldSchedule(request)
            ? ExternalCapabilityExecutionMode.Durable
            : ExternalCapabilityExecutionMode.Interactive;
        var capabilityAdmissionPlan = existingPlan is not null
            ? await _capabilityAdmissionService.RevalidatePersistedAsync(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    existingPlan,
                    workflowYaml,
                    new Dictionary<string, string>(),
                    "studio_workflow_provisioning",
                    executionMode,
                    workflowId,
                    revisionId),
                ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    normalizedScopeId,
                    callerId,
                    nyxIdCallerCredentialSelection,
                    organizationBearerToken),
                workflowYaml,
                new Dictionary<string, string>(),
                "studio_workflow_provisioning",
                executionMode,
                explicitRequestConfirmations,
                workflowId,
                revisionId),
                ct);
        var trustedAdmission = new WorkflowCapabilityAdmissionContext(
            callerId,
            executionMode: executionMode,
            existingPlan: capabilityAdmissionPlan);

        // Provision identity: one (scope, team, display name) tuple owns exactly
        // one member + workflow id + schedule, so retries converge on the same
        // Team-owned resources instead of leaving an orphan pair per attempt.
        // 1. Resolve the member: reuse the existing one for this (scope, display
        //    name), else create it. The deterministic id is the member's identity;
        //    its display name is a mutable label — re-creating after a rename
        //    would hard-conflict at the actor, so an already-provisioned member is
        //    read from the readmodel and never re-created. The actor stamps the
        //    rename-safe published service id at creation, so both paths read it
        //    straight back — no poll, no recompute of the convention.
        var (memberId, publishedServiceId) = await ResolveProvisionedMemberAsync(
            normalizedScopeId, teamId, displayName, $"wf-{provisionKey}", ct);

        // 2. Bind the inline workflow YAML. WorkflowId is a stable identifier the
        //    bind contract requires; deriving it from the provision key keeps one
        //    logical workflow identity across re-binds of the same member.
        //    The bind is asynchronous — we do NOT poll it to completion.
        var bindReceipt = await _bindingPort.BindAsync(
            new StudioMemberWorkflowBindingRequest(
                normalizedScopeId,
                memberId,
                workflowYaml)
            {
                WorkflowId = workflowId,
                RevisionId = revisionId,
                CapabilityAdmission = trustedAdmission,
            },
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
            var timing = ResolveScheduleTiming(request);
            scheduleId = await EnsureProvisionScheduleAsync(
                normalizedScopeId,
                teamId,
                memberId,
                publishedServiceId,
                bindReceipt,
                workflowId,
                revisionId,
                displayName,
                workflowYaml,
                request.Prompt ?? string.Empty,
                callerCredential,
                request,
                capabilityAdmissionPlan,
                timing,
                ct);
        }

        return new ProvisionWorkflowResponse(
            MemberId: memberId,
            ScopeId: normalizedScopeId,
            TeamId: teamId,
            BindingStatus: ProvisionWorkflowBindingStatusNames.Accepted,
            ObservatoryUrl: ObservatoryPath)
        {
            BindingRunId = NormalizeOptional(bindReceipt.BindingRunId),
            ScheduleId = scheduleId,
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
    private async Task<(string MemberId, string PublishedServiceId)> ResolveProvisionedMemberAsync(
        string scopeId,
        string teamId,
        string displayName,
        string memberId,
        CancellationToken ct)
    {
        try
        {
            var existing = await _memberService.GetAsync(scopeId, memberId, ct);
            var existingTeamId = NormalizeOptional(existing.Summary.TeamId);
            if (!string.Equals(existingTeamId, teamId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Provisioned member '{memberId}' is not assigned to team '{teamId}'.");
            }

            return (
                existing.Summary.MemberId,
                NormalizeRequired(existing.Summary.PublishedServiceId, nameof(existing.Summary.PublishedServiceId)));
        }
        catch (StudioMemberNotFoundException)
        {
            var created = await _memberService.CreateAsync(
                scopeId,
                new CreateStudioMemberRequest(
                    DisplayName: displayName,
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    MemberId: memberId,
                    TeamId: teamId),
                ct);
            return (
                created.MemberId,
                NormalizeRequired(created.PublishedServiceId, nameof(created.PublishedServiceId)));
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
        string teamId,
        string memberId,
        string publishedServiceId,
        StudioMemberWorkflowBindingResult bindReceipt,
        string workflowId,
        string revisionId,
        string displayName,
        string workflowYaml,
        string prompt,
        ProvisionWorkflowCallerCredential callerCredential,
        ProvisionWorkflowRequest request,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan,
        ProvisionScheduleTiming timing,
        CancellationToken ct)
    {
        var authenticatedOwner = request.AuthenticatedOwner
            ?? BuildLegacyUnauthenticatedOwner(callerCredential);
        var baseScheduleRequest = new StudioMemberWorkflowScheduleRequest(
            scopeId,
            memberId,
            timing.CronExpression,
            timing.Timezone,
            authenticatedOwner)
        {
            TeamId = teamId,
            CredentialProvisioningKind = CredentialProvisioningKind,
            Prompt = prompt,
            DisplayName = $"provision-{displayName}",
            ProvisioningBearerToken = request.ProvisioningBearerToken,
            Enabled = true,
            ScheduleMode = timing.ScheduleMode,
            OneShotFireAt = timing.OneShotFireAt,
            AcceptedBinding = new StudioMemberWorkflowAcceptedBindingContext(
                teamId,
                publishedServiceId,
                NormalizeOptional(bindReceipt.WorkflowId) ?? workflowId,
                NormalizeOptional(bindReceipt.RevisionId) ?? revisionId)
            {
                WorkflowEvidence = BuildTrustedWorkflowEvidence(
                    workflowYaml,
                    capabilityAdmissionPlan),
            },
        };

        var preflight = await _schedulePort.PreflightForWriteAsync(baseScheduleRequest, ct);
        if (!preflight.Success)
            throw new InvalidOperationException(preflight.Detail);
        var permissionDigest = NormalizeRequired(preflight.Plan?.PermissionDigest, "permissionDigest");
        var policyVersion = NormalizeRequired(preflight.Plan?.CredentialPolicy?.PolicyVersion, "policyVersion");

        const int maxGenerations = 50;
        for (var generation = 1; generation <= maxGenerations; generation++)
        {
            var scheduleId = generation == 1
                ? $"provision-{publishedServiceId}"
                : $"provision-{publishedServiceId}.{generation}";
            var operationIdentity = BuildProvisionScheduleOperationIdentity(
                request,
                scheduleId,
                permissionDigest,
                timing);
            try
            {
                var schedule = await _schedulePort.CreateAsync(
                    baseScheduleRequest with
                    {
                        ScheduleId = scheduleId,
                        OperationId = operationIdentity.OperationId,
                        IdempotencyKey = operationIdentity.IdempotencyKey,
                        ConfirmedPolicyVersion = policyVersion,
                    },
                    permissionDigest,
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

    private static ScheduledInvocationWorkflowEvidence BuildTrustedWorkflowEvidence(
        string workflowYaml,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan)
    {
        var workflow = new WorkflowParser().Parse(workflowYaml);
        var authorizationDependencies = WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow);
        var admittedCapabilities = WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(capabilityAdmissionPlan);
        return new ScheduledInvocationWorkflowEvidence(
            StateVersion: 0,
            ExternalCapabilities: admittedCapabilities,
            OwnerLLMRouteRequired: authorizationDependencies.OwnerLlmRouteRequired,
            ServiceGrantRequirement: WorkflowServiceGrantRequirementClassifier.Classify(admittedCapabilities));
    }

    /// <summary>
    /// A schedule (and therefore a run) is created when there is something to
    /// fire: a recurring monitor (caller-supplied <see cref="ProvisionWorkflowRequest.Cron"/>)
    /// or a one-shot demo (<see cref="ProvisionWorkflowRequest.RunImmediately"/>).
    /// </summary>
    private static bool ShouldSchedule(ProvisionWorkflowRequest request) =>
        request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron);

    /// <summary>
    /// Resolves schedule timing as one typed value. A caller-supplied cron remains
    /// recurring with its normalized timezone; otherwise a first-class one-shot
    /// fires shortly after the bind at an exact UTC timestamp.
    /// </summary>
    private ProvisionScheduleTiming ResolveScheduleTiming(ProvisionWorkflowRequest request)
    {
        var callerCron = NormalizeOptional(request.Cron);
        if (callerCron != null)
        {
            return new ProvisionScheduleTiming(
                callerCron,
                ScheduledDispatchCalculator.NormalizeTimezone(request.Timezone),
                ScheduledDispatchScheduleMode.RecurringCron,
                null);
        }

        return new ProvisionScheduleTiming(
            string.Empty,
            ScheduledDispatchCalculator.DefaultTimezone,
            ScheduledDispatchScheduleMode.OneShotAtUtc,
            _timeProvider
                .GetUtcNow()
                .AddSeconds(ProvisionWorkflowRequest.DefaultOneShotDelaySeconds)
                .ToUniversalTime());
    }

    private readonly record struct ProvisionScheduleTiming(
        string CronExpression,
        string Timezone,
        ScheduledDispatchScheduleMode ScheduleMode,
        DateTimeOffset? OneShotFireAt);

    private static ProvisionScheduleOperationIdentity BuildProvisionScheduleOperationIdentity(
        ProvisionWorkflowRequest request,
        string scheduleId,
        string permissionDigest,
        ProvisionScheduleTiming timing)
    {
        var explicitOperationId = NormalizeOptional(request.ScheduleOperationId);
        var explicitIdempotencyKey = NormalizeOptional(request.ScheduleIdempotencyKey);
        if (explicitOperationId != null && explicitIdempotencyKey != null)
            return new ProvisionScheduleOperationIdentity(explicitOperationId, explicitIdempotencyKey);

        var identity = Encoding.UTF8.GetBytes(string.Join('\n',
            "studio-workflow-provision-schedule/v1",
            scheduleId,
            permissionDigest,
            NormalizeOptional(request.Prompt) ?? string.Empty,
            timing.CronExpression,
            timing.Timezone,
            ((int)timing.ScheduleMode).ToString(),
            timing.OneShotFireAt?.ToUniversalTime().UtcTicks.ToString() ?? string.Empty));
        var hash = Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 16));
        return new ProvisionScheduleOperationIdentity(
            $"studio-workflow-provision-create:{hash}",
            $"studio-workflow-provision-schedule:{hash}");
    }

    private readonly record struct ProvisionScheduleOperationIdentity(
        string OperationId,
        string IdempotencyKey);

    internal static string BuildStudioUrl(string scopeId, string teamId, string memberId) =>
        $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(memberId)}/workflow";

    private static AuthenticatedAuthorizationOwnerContext BuildLegacyUnauthenticatedOwner(
        ProvisionWorkflowCallerCredential callerCredential)
    {
        var externalUserId = NormalizeRequired(callerCredential.ExternalUserId, nameof(callerCredential.ExternalUserId));
        return new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = externalUserId,
            },
            NormalizeRequired(callerCredential.Platform, nameof(callerCredential.Platform)),
            NormalizeOptional(callerCredential.Tenant) ?? string.Empty,
            externalUserId,
            string.Empty);
    }

    /// <summary>
    /// Deterministic provision identity for one (scope, team, display name) tuple:
    /// 32 hex chars of SHA-256, so the derived member id (<c>wf-{key}</c>, 35
    /// chars) satisfies the member-id slug pattern and length cap while retries
    /// with the same display name land on the same member/workflow/schedule.
    /// </summary>
    internal static string BuildProvisionKey(string scopeId, string teamId, string displayName)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{teamId}\n{displayName}");
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
