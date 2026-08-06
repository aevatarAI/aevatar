using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// One-call workflow provisioning facade (C1). Composes the existing member-first
/// services: create a member via
/// <see cref="IStudioMemberService.CreateAsync"/>, bind the inline workflow YAML
/// via <see cref="IStudioMemberWorkflowBindingPort"/>, then submit a secret-free
/// provisioning intent through <see cref="IStudioWorkflowScheduleProvisioningCommandPort"/>.
/// The member actor waits for that exact binding revision before a background
/// executor creates or replaces the Team-owned workflow schedule.
///
/// The flow is deliberately NON-BLOCKING. Binding a workflow member is an
/// asynchronous pipeline that can take minutes, so a synchronous handler that
/// polled the bind to completion would exhaust the gateway timeout and never
/// invoke. Instead the actor-owned continuation produces a scheduled-dispatch:
/// <list type="bullet">
///   <item>it observes the committed binding revision before scheduling, so a
///   one-shot timestamp is resolved only after the target service is ready;</item>
///   <item>projection lag returns a retryable typed result and is reconciled by
///   durable self-timeouts without re-admitting a new workflow revision;</item>
///   <item>because the schedule kind is <see cref="ScheduledDispatchScheduleKind.Workflow"/>,
///   the dispatch projects a freshly re-minted caller NyxID token onto the run's
///   <c>ChatRequestEvent</c> (<c>LlmControl.SenderNyxIdAccessToken</c>), so the
///   run's LLM calls authenticate — the one thing a direct
///   <c>IServiceInvocationPort.InvokeAsync</c> could not provide.</item>
/// </list>
///
/// The durable intent carries a verified NyxID binding reference, never a raw
/// bearer token. The background schedule port uses that reference to issue a
/// short-lived provisioning token and materialize the schedule's dedicated
/// credential. The scope id and caller identity are always input parameters;
/// this service holds no HttpContext and depends only on application ports.
///
/// Two invariants keep the non-blocking flow from leaking resources:
/// <list type="bullet">
///   <item><b>Admit before provisioning.</b> The unified external-capability
///   admission service parses the workflow and evaluates readiness before any
///   mutation. Invalid or non-ready workflows provision NOTHING — no member, no
///   schedule provisioning intent — so an authoring agent can repair the YAML and retry without
///   leaving garbage behind.</item>
///   <item><b>Retries converge.</b> One (scope, display name) pair owns exactly
///   one member, one workflow id, and one schedule inside one target Team: the
///   member id is derived deterministically from that ownership tuple (an
///   existing member is reused, never re-created), and the schedule uses a
///   deterministic id. Re-provisioning the same display name replaces the live
///   automation through the credential replacement protocol instead of trying to
///   create a second active credential. That reuse is also the documented
///   ownership rule: a display name identifies ONE automation, and
///   re-provisioning it replaces that member's workflow. When the pair's schedule
///   was explicitly deleted, the schedule id advances to the next generation
///   instead of resurrecting the tombstone (see
///   the actor-owned provisioning continuation).</item>
/// </list>
/// </summary>
public sealed class StudioWorkflowProvisioningService : IStudioWorkflowProvisioningService
{
    private const string ObservatoryPath = "/admin#/observatory";

    private readonly IStudioMemberService _memberService;
    private readonly IStudioMemberWorkflowBindingPort _bindingPort;
    private readonly IStudioWorkflowScheduleProvisioningCommandPort _scheduleProvisioningCommandPort;
    private readonly StudioWorkflowProvisioningAdmissionService _provisioningAdmissionService;

    public StudioWorkflowProvisioningService(
        IStudioMemberService memberService,
        IStudioMemberWorkflowBindingPort bindingPort,
        IStudioWorkflowScheduleProvisioningCommandPort scheduleProvisioningCommandPort,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort = null,
        INyxIdAuthorizationCatalogVisibilityPort? catalogVisibilityPort = null,
        ILogger<StudioWorkflowProvisioningService>? logger = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _bindingPort = bindingPort ?? throw new ArgumentNullException(nameof(bindingPort));
        _scheduleProvisioningCommandPort = scheduleProvisioningCommandPort
            ?? throw new ArgumentNullException(nameof(scheduleProvisioningCommandPort));
        _provisioningAdmissionService = new StudioWorkflowProvisioningAdmissionService(
            capabilityAdmissionService,
            catalogRefreshPort,
            catalogVisibilityPort,
            logger);
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
        _ = NormalizeRequired(callerCredential.Platform, nameof(callerCredential.Platform));
        _ = NormalizeRequired(callerCredential.ExternalUserId, nameof(callerCredential.ExternalUserId));
        _ = NormalizeRequired(callerCredential.Scope, nameof(callerCredential.Scope));
        var teamId = NormalizeRequired(request.TeamId, "teamId");
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        var workflowYaml = NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var provisionKey = BuildProvisionKey(normalizedScopeId, teamId, displayName);
        var workflowId = $"workflow-{provisionKey}";
        var executionMode = ShouldSchedule(request)
            ? ExternalCapabilityExecutionMode.Durable
            : ExternalCapabilityExecutionMode.Interactive;

        var suppliedAdmission = request.CapabilityAdmission;
        var callerId = suppliedAdmission?.CallerId ?? string.Empty;
        var nyxIdCallerCredentialSelection = suppliedAdmission?.NyxIdCallerCredential;
        var organizationBearerToken = suppliedAdmission?.NyxIdOrganizationBearerToken;
        var existingPlan = suppliedAdmission?.ExistingPlan?.Clone();
        var suppliedExplicitRequestConfirmations = suppliedAdmission?.ExplicitRequestConfirmations;
        var (revisionId, capabilityAdmissionPlan) = await ResolveProvisionRevisionAdmissionAsync(
            normalizedScopeId,
            provisionKey,
            workflowId,
            workflowYaml,
            executionMode,
            callerId,
            nyxIdCallerCredentialSelection,
            organizationBearerToken,
            existingPlan,
            suppliedExplicitRequestConfirmations,
            request,
            ct);
        ValidatePersistedAdmissionCapabilities(capabilityAdmissionPlan);
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
        if (!bindReceipt.Success ||
            !string.Equals(bindReceipt.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(bindReceipt.MemberId, memberId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("workflow_binding_receipt_invalid");
        }
        var acceptedWorkflowId = NormalizeOptional(bindReceipt.WorkflowId) ?? workflowId;
        var acceptedRevisionId = NormalizeOptional(bindReceipt.RevisionId) ?? revisionId;

        // 3. Durably accept actor-owned schedule provisioning. The actor waits for
        //    the exact binding revision and only then creates the workflow-kind
        //    scheduled dispatch. Scheduling is requested for a recurring monitor
        //    (caller Cron) or a one-shot demo (RunImmediately). RunImmediately=false
        //    with no Cron is an honest "bind only": no schedule and no run.
        //
        //    The intent persists only a re-mintable subject and verified binding
        //    reference. Raw bearer tokens never enter member or schedule state.
        //
        //    The executor later uses deterministic id provision-{publishedServiceId};
        //    it replaces a live schedule through the credential protocol and advances
        //    to a new generation after an explicit delete tombstone.
        string? scheduleId = null;
        string? scheduleProvisioningId = null;
        string? scheduleProvisioningStatus = null;
        if (ShouldSchedule(request))
        {
            var authenticatedOwner = request.AuthenticatedOwner
                ?? throw new UnauthorizedAccessException("authenticated_authorization_owner_required");
            if (string.IsNullOrWhiteSpace(authenticatedOwner.VerifiedBindingId))
                throw new UnauthorizedAccessException("authenticated_authorization_owner_binding_missing");

            var intent = BuildScheduleProvisioningIntent(
                normalizedScopeId,
                teamId,
                memberId,
                publishedServiceId,
                acceptedWorkflowId,
                acceptedRevisionId,
                displayName,
                request.Prompt ?? string.Empty,
                authenticatedOwner,
                bindReceipt.BindingRunId,
                request);
            var scheduleAcceptance = await _scheduleProvisioningCommandPort.AcceptAsync(intent, ct);
            if (!scheduleAcceptance.Accepted ||
                !string.Equals(scheduleAcceptance.ProvisioningId, intent.ProvisioningId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("workflow_schedule_provisioning_receipt_invalid");
            }

            scheduleId = NormalizeOptional(scheduleAcceptance.ScheduleId);
            scheduleProvisioningId = intent.ProvisioningId;
            scheduleProvisioningStatus = scheduleAcceptance.Status;
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
            ScheduleProvisioningId = scheduleProvisioningId,
            ScheduleProvisioningStatus = scheduleProvisioningStatus,
            StudioUrl = BuildStudioUrl(normalizedScopeId, teamId, memberId),
        };
    }

    private async Task<ProvisionRevisionAdmission> ResolveProvisionRevisionAdmissionAsync(
        string scopeId,
        string provisionKey,
        string workflowId,
        string workflowYaml,
        ExternalCapabilityExecutionMode executionMode,
        string callerId,
        NyxIdCallerCredentialSelection? nyxIdCallerCredentialSelection,
        string? organizationBearerToken,
        WorkflowCapabilityAdmissionPlan? existingPlan,
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? suppliedConfirmations,
        ProvisionWorkflowRequest request,
        CancellationToken ct)
    {
        var revisionId = BuildProvisionRevisionId(
            provisionKey,
            workflowId,
            workflowYaml,
            executionMode);
        var mayBindResolvedRevision = existingPlan is null &&
                                      CanBindProvisionedConfirmationIdentity(suppliedConfirmations);

        var explicitRequestConfirmations = BindProvisionedExplicitRequestConfirmations(
            suppliedConfirmations,
            workflowId,
            revisionId);
        var liveAdmissionRequest = new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                scopeId,
                callerId,
                nyxIdCallerCredentialSelection,
                organizationBearerToken),
            workflowYaml,
            new Dictionary<string, string>(),
            "studio_workflow_provisioning",
            executionMode,
            explicitRequestConfirmations,
            workflowId,
            revisionId);
        var capabilityAdmissionPlan = await _provisioningAdmissionService.ResolveAsync(
            liveAdmissionRequest,
            existingPlan,
            request,
            ct);
        var resolvedRevisionId = BuildProvisionRevisionId(
            provisionKey,
            workflowId,
            workflowYaml,
            executionMode,
            capabilityAdmissionPlan);

        if (string.Equals(resolvedRevisionId, revisionId, StringComparison.Ordinal) ||
            !WorkflowCapabilityAdmissionPlanIntegrity.RequiresExplicitRequestBindingIdentity(
                capabilityAdmissionPlan))
        {
            return new ProvisionRevisionAdmission(resolvedRevisionId, capabilityAdmissionPlan);
        }

        // Exact caller-bound confirmations retain their reviewed revision identity.
        // Skill scheduling supplies unbound confirmations, allowing this service to
        // bind the already-admitted plan to the immutable revision without repeating
        // external discovery and changing its source snapshot between passes.
        if (!mayBindResolvedRevision)
            return new ProvisionRevisionAdmission(revisionId, capabilityAdmissionPlan);

        var reboundPlan = BindProvisionedAdmissionPlanRevision(
            capabilityAdmissionPlan,
            workflowYaml,
            workflowId,
            resolvedRevisionId);
        var reboundRevisionId = BuildProvisionRevisionId(
            provisionKey,
            workflowId,
            workflowYaml,
            executionMode,
            reboundPlan);
        if (!string.Equals(reboundRevisionId, resolvedRevisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Provisioned workflow admission did not produce a stable revision identity.");
        }

        return new ProvisionRevisionAdmission(resolvedRevisionId, reboundPlan);
    }

    private static bool CanBindProvisionedConfirmationIdentity(
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? confirmations) =>
        (confirmations ?? []).All(static confirmation =>
            string.IsNullOrWhiteSpace(confirmation.WorkflowId) &&
            string.IsNullOrWhiteSpace(confirmation.RevisionId));

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation> BindProvisionedExplicitRequestConfirmations(
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? confirmations,
        string workflowId,
        string revisionId) =>
        (confirmations ?? []).Select(confirmation =>
        {
            var bound = confirmation.Clone();
            if (bound.WorkflowId.Length == 0 && bound.RevisionId.Length == 0)
            {
                bound.WorkflowId = workflowId;
                bound.RevisionId = revisionId;
            }

            return bound;
        }).ToArray();

    private static WorkflowCapabilityAdmissionPlan BindProvisionedAdmissionPlanRevision(
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan,
        string workflowYaml,
        string workflowId,
        string revisionId)
    {
        var rebound = capabilityAdmissionPlan.Clone();
        foreach (var admission in rebound.InvocationAdmissions)
        {
            var grant = admission.NyxIdExplicitRequestGrant;
            if (grant is null)
                continue;
            if (admission.Capability?.CapabilityCase !=
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
            {
                throw new InvalidOperationException(
                    "Provisioned workflow explicit request admission is invalid.");
            }

            grant.WorkflowId = workflowId;
            grant.RevisionId = revisionId;
            admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);
        }

        rebound.DefinitionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
            workflowYaml,
            new Dictionary<string, string>(),
            workflowId,
            revisionId);
        rebound.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(rebound);
        return rebound;
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
    /// A schedule (and therefore a run) is created when there is something to
    /// fire: a recurring monitor (caller-supplied <see cref="ProvisionWorkflowRequest.Cron"/>)
    /// or a one-shot demo (<see cref="ProvisionWorkflowRequest.RunImmediately"/>).
    /// </summary>
    private static bool ShouldSchedule(ProvisionWorkflowRequest request) =>
        request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron);

    private static void ValidatePersistedAdmissionCapabilities(
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan)
    {
        foreach (var admission in capabilityAdmissionPlan.InvocationAdmissions)
        {
            if (admission.Capability?.CapabilityCase is
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector or
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService or
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
            {
                continue;
            }

            throw new InvalidOperationException(
                "Workflow capability admission contains an unsupported capability.");
        }
    }

    private static StudioWorkflowScheduleProvisioningIntent BuildScheduleProvisioningIntent(
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        string workflowId,
        string revisionId,
        string displayName,
        string prompt,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        string? bindingRunId,
        ProvisionWorkflowRequest request)
    {
        var cronExpression = NormalizeOptional(request.Cron);
        var scheduleMode = cronExpression is null
            ? ScheduledDispatchScheduleMode.OneShotAtUtc
            : ScheduledDispatchScheduleMode.RecurringCron;
        var timezone = scheduleMode == ScheduledDispatchScheduleMode.RecurringCron
            ? ScheduledDispatchCalculator.NormalizeTimezone(request.Timezone)
            : ScheduledDispatchCalculator.DefaultTimezone;
        var provisioningId = BuildScheduleProvisioningId(
            scopeId,
            teamId,
            memberId,
            publishedServiceId,
            workflowId,
            revisionId,
            displayName,
            prompt,
            scheduleMode,
            cronExpression ?? string.Empty,
            timezone);
        return new StudioWorkflowScheduleProvisioningIntent(
            provisioningId,
            scopeId,
            teamId,
            memberId,
            publishedServiceId,
            workflowId,
            revisionId,
            displayName,
            prompt,
            authenticatedOwner,
            scheduleMode,
            cronExpression ?? string.Empty,
            timezone,
            ProvisionWorkflowRequest.DefaultOneShotDelaySeconds,
            NormalizeOptional(bindingRunId),
            NormalizeOptional(request.ScheduleOperationId),
            NormalizeOptional(request.ScheduleIdempotencyKey));
    }

    private static string BuildScheduleProvisioningId(
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        string workflowId,
        string revisionId,
        string displayName,
        string prompt,
        ScheduledDispatchScheduleMode scheduleMode,
        string cronExpression,
        string timezone)
    {
        var identity = Encoding.UTF8.GetBytes(string.Join('\n',
            "studio-workflow-schedule-provisioning/v1",
            scopeId,
            teamId,
            memberId,
            publishedServiceId,
            workflowId,
            revisionId,
            displayName,
            prompt,
            ((int)scheduleMode).ToString(),
            cronExpression,
            timezone));
        return $"schedule-provisioning-{Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 16))}";
    }

    internal static string BuildStudioUrl(string scopeId, string teamId, string memberId) =>
        $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(memberId)}/workflow";

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

    /// <summary>
    /// A provisioned workflow keeps one stable draft identity while each distinct
    /// executable spec receives its own immutable service revision. The versioned
    /// contract marker also prevents a retry from colliding with revisions created
    /// before the published-member binding began resolving the workflow name from
    /// YAML instead of writing the workflow id into that semantic field.
    /// </summary>
    internal static string BuildProvisionRevisionId(
        string provisionKey,
        string workflowId,
        string workflowYaml,
        ExternalCapabilityExecutionMode executionMode,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null)
    {
        var admissionDiscriminator = BuildAdmissionRevisionDiscriminator(capabilityAdmissionPlan);
        var canonicalSpec = Encoding.UTF8.GetBytes(string.Join('\n',
            admissionDiscriminator.Length == 0
                ? "studio-workflow-provision-revision/v2"
                : "studio-workflow-provision-revision/v3",
            provisionKey,
            workflowId,
            "workflow-name=yaml",
            ((int)executionMode).ToString(),
            workflowYaml,
            admissionDiscriminator));
        var hash = SHA256.HashData(canonicalSpec);
        return $"revision-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string BuildAdmissionRevisionDiscriminator(
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan)
    {
        if (capabilityAdmissionPlan is null ||
            (capabilityAdmissionPlan.InvocationAdmissions.Count == 0 &&
             capabilityAdmissionPlan.SourceStamps.Count == 0 &&
             capabilityAdmissionPlan.DurableAuthorizationOwner is null))
        {
            return string.Empty;
        }

        var canonical = capabilityAdmissionPlan.Clone();
        canonical.DefinitionDigest = string.Empty;
        canonical.AdmissionDigest = string.Empty;
        foreach (var admission in canonical.InvocationAdmissions)
        {
            var grant = admission.NyxIdExplicitRequestGrant;
            if (grant is null)
                continue;

            grant.WorkflowId = string.Empty;
            grant.RevisionId = string.Empty;
            if (admission.Capability?.NyxIdUserRequest is { } requestCapability)
                requestCapability.ExplicitRequestGrantDigest = string.Empty;
        }

        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    private readonly record struct ProvisionRevisionAdmission(
        string RevisionId,
        WorkflowCapabilityAdmissionPlan Plan);

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
