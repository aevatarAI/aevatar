using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ObservatoryPath = "/workflow/observatory";
    private const string DedicatedCredentialProvisioningKind =
        "dedicated_scheduled_invocation_agent_key";
    private const string ProvisioningBearerCapabilityScope = "proxy";

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly IScheduledInvocationAuthorizationPlanner _authorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator _authorizationRevalidator;
    private readonly INyxIdAuthorizationCatalogRefreshPort? _catalogRefreshPort;
    private readonly IStudioScheduledCredentialMaterializer _credentialMaterializer;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;
    private readonly StudioMemberWorkflowSchedulePolicy _schedulePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StudioMemberWorkflowSchedulePort> _logger;
    private readonly ILogger _auditLogger;

    public StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        IStudioScheduledCredentialMaterializer credentialMaterializer,
        StudioMemberWorkflowSchedulePolicy schedulePolicy,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort = null,
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null,
        ILoggerFactory? auditLoggerFactory = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
        : this(
            memberService,
            scheduleService,
            authorizationPlanner,
            authorizationRevalidator,
            credentialMaterializer,
            schedulePolicy,
            TimeProvider.System,
            catalogRefreshPort,
            logger,
            auditLoggerFactory,
            callerAccessTokenProvider)
    {
    }

    internal StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        IStudioScheduledCredentialMaterializer credentialMaterializer,
        TimeProvider timeProvider,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort = null,
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null,
        ILoggerFactory? auditLoggerFactory = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
        : this(
            memberService,
            scheduleService,
            authorizationPlanner,
            authorizationRevalidator,
            credentialMaterializer,
            new StudioMemberWorkflowSchedulePolicy(),
            timeProvider,
            catalogRefreshPort,
            logger,
            auditLoggerFactory,
            callerAccessTokenProvider)
    {
    }

    internal StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        IStudioScheduledCredentialMaterializer credentialMaterializer,
        StudioMemberWorkflowSchedulePolicy schedulePolicy,
        TimeProvider timeProvider,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort = null,
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null,
        ILoggerFactory? auditLoggerFactory = null,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _authorizationRevalidator = authorizationRevalidator
            ?? throw new ArgumentNullException(nameof(authorizationRevalidator));
        _catalogRefreshPort = catalogRefreshPort;
        _credentialMaterializer = credentialMaterializer ?? throw new ArgumentNullException(nameof(credentialMaterializer));
        _callerAccessTokenProvider = callerAccessTokenProvider;
        _schedulePolicy = schedulePolicy ?? throw new ArgumentNullException(nameof(schedulePolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<StudioMemberWorkflowSchedulePort>.Instance;
        _auditLogger = (auditLoggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger(StudioMemberAutomationAuditContract.Category);
    }

    public async Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var result = await _authorizationPlanner.PlanAsync(resolved.AuthorizationRequest, ct);
        return ToAuthorizationResult(result);
    }

    public async Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var first = await _authorizationPlanner.PlanAsync(resolved.AuthorizationRequest, ct);
        if (first.Success || !IsRecoverableNyxIdCatalogSnapshotFailure(first.Detail))
            return ToAuthorizationResult(first);

        var refresh = await RefreshRecoverableNyxIdCatalogSnapshotAsync(
            resolved.AuthorizationRequest,
            first.RequiredNyxIdServices,
            first.LLMRefreshRequirement,
            cancellationToken => ResolveProvisioningBearerTokenAsync(request, cancellationToken),
            first.FailureCode,
            first.Detail,
            first.ObservedCatalogStateVersion,
            ct);
        if (!refresh.Success)
        {
            if (refresh.FailureCode ==
                ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending)
            {
                throw new StudioMemberAutomationProjectionPendingException(
                    refresh.RequiredStateVersion);
            }

            return new StudioMemberWorkflowAuthorizationResult(false, null, refresh.FailureCode, refresh.Detail);
        }

        var retryEvaluatedAtUtc = _timeProvider.GetUtcNow();
        var retryRequest = resolved.AuthorizationRequest with
        {
            EvaluatedAtUtc = retryEvaluatedAtUtc,
            ExpiresAtUtc = _schedulePolicy.ResolveCredentialExpiresAtUtc(retryEvaluatedAtUtc),
        };
        var second = await _authorizationPlanner.PlanAsync(retryRequest, ct);
        if (ShouldTreatRefreshedCatalogAsProjectionPending(
                second.Success,
                second.Detail,
                second.ObservedCatalogStateVersion,
                refresh.Refresh!.StateVersion))
        {
            throw new StudioMemberAutomationProjectionPendingException(refresh.Refresh.StateVersion);
        }

        return ToAuthorizationResult(second);
    }

    private async Task<ScheduledInvocationAuthorizationValidationResult> RevalidateWithCatalogRefreshRetryAsync(
        ScheduledInvocationAuthorizationRequest authorizationRequest,
        ScheduledInvocationAuthorizationConfirmation confirmation,
        Func<CancellationToken, Task<string>> provisioningBearerTokenResolver,
        DateTimeOffset? fixedCredentialExpiresAtUtc,
        CancellationToken ct)
    {
        var first = await _authorizationRevalidator.RevalidateAsync(authorizationRequest, confirmation, ct);
        if (first.Success || !IsRecoverableNyxIdCatalogSnapshotFailure(first.Detail))
            return first;

        var refresh = await RefreshRecoverableNyxIdCatalogSnapshotAsync(
            authorizationRequest,
            first.RequiredNyxIdServices,
            first.LLMRefreshRequirement,
            provisioningBearerTokenResolver,
            first.FailureCode,
            first.Detail,
            first.ObservedCatalogStateVersion,
            ct);
        if (!refresh.Success)
        {
            return refresh.FailureCode == ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending
                ? ScheduledInvocationAuthorizationValidationResult.ProjectionPending(
                    refresh.RequiredStateVersion,
                    refresh.ObservedCatalogStateVersion)
                : ScheduledInvocationAuthorizationValidationResult.Failed(
                    refresh.FailureCode,
                    refresh.Detail);
        }

        var retryEvaluatedAtUtc = _timeProvider.GetUtcNow();
        var retryRequest = authorizationRequest with
        {
            EvaluatedAtUtc = retryEvaluatedAtUtc,
            ExpiresAtUtc = fixedCredentialExpiresAtUtc ??
                           _schedulePolicy.ResolveCredentialExpiresAtUtc(retryEvaluatedAtUtc),
        };
        var second = await _authorizationRevalidator.RevalidateAsync(retryRequest, confirmation, ct);
        return ShouldTreatRefreshedCatalogAsProjectionPending(
                second.Success,
                second.Detail,
                second.ObservedCatalogStateVersion,
                refresh.Refresh!.StateVersion)
            ? ScheduledInvocationAuthorizationValidationResult.ProjectionPending(
                refresh.Refresh.StateVersion,
                second.ObservedCatalogStateVersion)
            : second;
    }

    public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) =>
        ApplyAsync(request, confirmedPermissionDigest, TeamAutomationOperationKind.Create, ct);

    public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) =>
        ApplyAsync(request, confirmedPermissionDigest, TeamAutomationOperationKind.Reauthorize, ct);

    public async Task<StudioMemberAutomationListResponse> ListAsync(
        string scopeId,
        string teamId,
        string? memberId,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        if (NormalizeOptional(memberId) is not { } normalizedMemberId)
        {
            var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
            var normalizedTeamId = NormalizeRequired(teamId, nameof(teamId));
            var teamResult = await _scheduleService.ListAsync(
                new ScheduledDispatchListQuery(
                    Take: take,
                    Cursor: cursor,
                    IncludeTotalCount: includeTotalCount,
                    TeamAutomationScopeId: normalizedScopeId,
                    TeamAutomationTeamId: normalizedTeamId,
                    TeamAutomationMemberId: null,
                    ExcludeCompletedTeamAutomationDeletions: true),
                ct);
            return new StudioMemberAutomationListResponse(
                teamResult.Items.Select(MapView).ToArray(),
                teamResult.NextCursor,
                teamResult.TotalCount);
        }

        var resolved = await ResolveTeamMemberAsync(scopeId, teamId, normalizedMemberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId, resolved.TeamId);
        var result = await _scheduleService.ListTeamAutomationsAsync(
            owner,
            take,
            cursor,
            includeTotalCount,
            ct);
        return new StudioMemberAutomationListResponse(
            result.Items.Select(item => MapView(item, resolved)).ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    public async Task<StudioMemberAutomationView?> GetAsync(
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        CancellationToken ct = default)
    {
        var resolved = await ResolveTeamMemberAsync(scopeId, teamId, memberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId, resolved.TeamId);
        var detail = await _scheduleService.GetTeamAutomationAsync(
            NormalizeRequired(scheduleId, nameof(scheduleId)),
            owner,
            ct);
        return detail == null ? null : MapView(detail.Schedule, resolved);
    }

    public async Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
        StudioMemberAutomationUpdateCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var resolved = await ResolveTeamMemberAsync(command.ScopeId, command.TeamId, command.MemberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId, resolved.TeamId);
        var scheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var existing = await _scheduleService.GetTeamAutomationAsync(scheduleId, owner, ct)
            ?? throw new ScheduledDispatchNotFoundException(scheduleId);
        var expiresAt = existing.Schedule.CredentialExpiresAt
            ?? throw new InvalidOperationException("team_automation_credential_expiry_missing");
        var authorizationRequest = new StudioMemberWorkflowScheduleRequest(
            resolved.ScopeId,
            resolved.MemberId,
            command.ScheduleCron,
            command.ScheduleTimezone,
            command.AuthenticatedOwner)
        {
            TeamId = resolved.TeamId,
        };
        var current = await ResolveAuthorizationRequestAsync(authorizationRequest, ct, expiresAt);
        var confirmation = BuildConfirmation(
            current.AuthorizationRequest,
            existing.Schedule.PermissionDigest,
            existing.Schedule.PolicyVersion);
        var validated = await RevalidateWithCatalogRefreshRetryAsync(
            current.AuthorizationRequest,
            confirmation,
            _ => Task.FromResult(NormalizeRequired(
                command.ProvisioningBearerToken,
                nameof(command.ProvisioningBearerToken))),
            current.AuthorizationRequest.ExpiresAtUtc,
            ct);
        if (!validated.Success)
        {
            if (validated.FailureCode ==
                ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending)
            {
                throw new StudioMemberAutomationProjectionPendingException(
                    validated.RequiredStateVersion);
            }

            throw new StudioMemberAutomationPlanConflictException(
                "reauthorization_required",
                validated.Detail);
        }

        var receipt = await _scheduleService.UpdateAsync(
            scheduleId,
            BuildScheduleConfiguration(
                scheduleId,
                command.DisplayName,
                resolved.ScopeId,
                resolved.MemberId,
                resolved.PublishedServiceId,
                NormalizeOptional(command.Prompt) ?? string.Empty,
                auth: null,
                authorizationFact: ToScheduleAuthorizationFact(validated.ValidatedPlan!.Plan),
                NormalizeRequired(command.ScheduleCron, nameof(command.ScheduleCron)),
                NormalizeRequired(command.ScheduleTimezone, nameof(command.ScheduleTimezone)),
                command.Enabled,
                owner,
                ScheduledDispatchScheduleMode.RecurringCron,
                null),
            new ScheduledDispatchMutationContext(TeamAutomationOwner: owner),
            ct);
        return ToMutationReceipt(receipt, command.OperationId);
    }

    public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default) =>
        ApplyActionAsync(command, TeamAutomationAction.Pause, ct);

    public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default) =>
        ApplyActionAsync(command, TeamAutomationAction.Resume, ct);

    public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default) =>
        ApplyActionAsync(command, TeamAutomationAction.RunNow, ct);

    public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default) =>
        ApplyActionAsync(command, TeamAutomationAction.Delete, ct);

    public async Task<StudioMemberAutomationMutationReceipt> RetryRevocationAsync(
        StudioMemberAutomationRetryRevocationCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var resolved = await ResolveTeamMemberAsync(command.ScopeId, command.TeamId, command.MemberId, ct);
        var scheduleOwner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId, resolved.TeamId);
        var authenticatedOwner = command.AuthenticatedOwner ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_required");
        var bearerToken = NormalizeRequired(
            command.ProvisioningBearerToken,
            nameof(command.ProvisioningBearerToken));
        var retry = await _scheduleService.RetryTeamAutomationRevocationAsync(
            NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId)),
            scheduleOwner,
            ToAuthorizationOwner(authenticatedOwner),
            ct);
        _ = await ExecutePendingRevocationAsync(
            retry.Outcome,
            bearerToken,
            authenticatedOwner,
            scheduleOwner,
            resolved.TeamId,
            CancellationToken.None);
        return ToMutationReceipt(retry.Admission, retry.Outcome.OperationId, "pending");
    }

    private async Task<StudioMemberAutomationMutationReceipt> ApplyActionAsync(
        StudioMemberAutomationActionCommand command,
        TeamAutomationAction action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var resolved = await ResolveTeamMemberAsync(command.ScopeId, command.TeamId, command.MemberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId, resolved.TeamId);
        var scheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        if (action == TeamAutomationAction.RunNow)
        {
            var run = await _scheduleService.RunTeamAutomationNowAsync(
                scheduleId,
                owner,
                ct);
            return new StudioMemberAutomationMutationReceipt(
                run.Accepted,
                "accepted",
                run.ScheduleId,
                command.OperationId,
                run.CommandId);
        }

        var operationId = NormalizeRequired(command.OperationId, nameof(command.OperationId));
        var idempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey));
        if (action == TeamAutomationAction.Delete)
        {
            var authenticatedOwner = command.AuthenticatedOwner ??
                throw new UnauthorizedAccessException("authenticated_authorization_owner_required");
            var bearerToken = NormalizeRequired(
                command.ProvisioningBearerToken,
                nameof(command.ProvisioningBearerToken));
            var committed = await _scheduleService.DeleteTeamAutomationAsync(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                NormalizeOptional(command.Reason) ??
                    "studio_team_automation_delete",
                ToAuthorizationOwner(authenticatedOwner),
                ct);
            await ExecutePendingRevocationAsync(
                committed.Outcome,
                bearerToken,
                authenticatedOwner,
                owner,
                resolved.TeamId,
                CancellationToken.None);
            return ToMutationReceipt(committed.Admission, operationId, "pending");
        }

        var receipt = action switch
        {
            TeamAutomationAction.Pause => await _scheduleService.DisableTeamAutomationAsync(
                scheduleId, owner, "studio_team_automation_pause", ct),
            TeamAutomationAction.Resume => await _scheduleService.EnableTeamAutomationAsync(
                scheduleId, owner, "studio_team_automation_resume", ct),
            _ => throw new InvalidOperationException("team_automation_action_invalid"),
        };
        return ToMutationReceipt(receipt, operationId);
    }

    private async Task<StudioMemberWorkflowScheduleResult> ApplyAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        TeamAutomationOperationKind operationKind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var callerAuthority = BuildScheduleCallerAuthority(request.AuthenticatedOwner);
        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var confirmation = BuildConfirmation(
            resolved.AuthorizationRequest,
            NormalizeRequired(confirmedPermissionDigest, nameof(confirmedPermissionDigest)),
            NormalizeRequired(request.ConfirmedPolicyVersion, nameof(request.ConfirmedPolicyVersion)));
        if (!string.Equals(
                NormalizeRequired(
                    request.CredentialProvisioningKind,
                    nameof(request.CredentialProvisioningKind)),
                DedicatedCredentialProvisioningKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("credential_provisioning_kind_invalid");
        }
        var validation = await RevalidateWithCatalogRefreshRetryAsync(
            resolved.AuthorizationRequest,
            confirmation,
            cancellationToken => ResolveProvisioningBearerTokenAsync(request, cancellationToken),
            fixedCredentialExpiresAtUtc: null,
            ct: ct);
        if (!validation.Success)
        {
            if (validation.FailureCode ==
                ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending)
            {
                throw new StudioMemberAutomationProjectionPendingException(
                    validation.RequiredStateVersion);
            }

            throw new StudioMemberAutomationPlanConflictException(
                validation.FailureCode == ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged
                    ? "authorization_plan_changed"
                    : "reauthorization_required",
                validation.Detail);
        }
        var plan = validation.ValidatedPlan!.Plan;
        EnsureRequiredDisclosures(plan);

        var scopeId = resolved.ScopeId;
        var memberId = resolved.MemberId;
        var timing = ResolveScheduleTiming(request);
        var publishedServiceId = resolved.PublishedServiceId;
        var teamOwner = new TeamMemberAutomationOwner(scopeId, memberId, resolved.TeamId);
        var operationId = NormalizeRequired(request.OperationId, nameof(request.OperationId));
        var idempotencyKey = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var scheduleId = NormalizeOptional(request.ScheduleId) ??
            (operationKind == TeamAutomationOperationKind.Create
                ? BuildScheduleId(scopeId, memberId, idempotencyKey)
                : throw new StudioMemberAutomationNotFoundException());
        var displayName = NormalizeOptional(request.DisplayName) ?? $"studio-member-workflow-{memberId}";
        var prompt = NormalizeOptional(request.Prompt) ?? string.Empty;
        var credentialOwner = new ScheduledInvocationAuthorizationOwner(
            NormalizeRequired(plan.Owner.Authority, nameof(plan.Owner.Authority)),
            plan.Owner.OwnerKind.ToString(),
            NormalizeRequired(plan.Owner.OwnerSubject, nameof(plan.Owner.OwnerSubject)));
        var authorizationFact = ToScheduleAuthorizationFact(plan);
        var chatPayload = Any.Pack(BuildChatRequest(prompt, scopeId, authorizationFact));
        var activationDecision = BuildTeamAutomationActivationDecision(
            scheduleId,
            displayName,
            scopeId,
            resolved.TeamId,
            memberId,
            publishedServiceId,
            chatPayload,
            callerAuthority,
            authorizationFact,
            timing.CronExpression,
            timing.Timezone,
            request.Enabled,
            timing.ScheduleMode,
            timing.OneShotFireAt);
        var mutationDigest = BuildTeamAutomationMutationDigest(activationDecision);
        var ownerScope = BuildOwnerScope(request);
        var bearerToken = await ResolveProvisioningBearerTokenAsync(request, ct);
        var existingAutomation = await _scheduleService.GetTeamAutomationAsync(scheduleId, teamOwner, ct);
        if (operationKind == TeamAutomationOperationKind.Reauthorize && existingAutomation == null)
            throw new ScheduledDispatchNotFoundException(scheduleId);
        if (operationKind == TeamAutomationOperationKind.Reauthorize)
            EnsureExistingCredentialOwnerMatches(request.AuthenticatedOwner, existingAutomation!.Schedule);
        var requestedEffectLocator = _credentialMaterializer.CreateEffectLocator(
            scheduleId,
            operationId,
            credentialOwner);
        var began = await _scheduleService.BeginTeamAutomationCredentialOperationAsync(
            new TeamAutomationCredentialOperation(
                scheduleId,
                teamOwner,
                operationId,
                idempotencyKey,
                plan.PermissionDigest,
                plan.CredentialPolicy.PolicyVersion,
                operationKind,
                requestedEffectLocator,
                activationDecision,
                mutationDigest),
            ct);
        if (!began.Admission.Accepted)
            throw new InvalidOperationException("team_automation_begin_rejected");
        if (existingAutomation?.Schedule.RevocationPending == true)
        {
            if (!string.Equals(existingAutomation.Schedule.TeamAutomationOperationId, operationId, StringComparison.Ordinal) ||
                !string.Equals(
                    existingAutomation.Schedule.TeamAutomationIdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal))
            {
                throw new ScheduledDispatchConflictException(
                    scheduleId,
                    "team_automation_revocation_operation_conflict");
            }

            var retry = await _scheduleService.RetryTeamAutomationRevocationAsync(
                scheduleId,
                teamOwner,
                ToAuthorizationOwner(request.AuthenticatedOwner),
                ct);
            _ = await ExecutePendingRevocationAsync(
                retry.Outcome,
                bearerToken,
                request.AuthenticatedOwner,
                teamOwner,
                resolved.TeamId,
                CancellationToken.None);
            return new StudioMemberWorkflowScheduleResult(
                Success: retry.Admission.Accepted,
                ScopeId: scopeId,
                MemberId: memberId,
                ScheduleId: scheduleId,
                PublishedServiceId: publishedServiceId,
                ObservatoryUrl: ObservatoryPath,
                Status: "pending")
            {
                OperationId = operationId,
                CommandId = retry.Admission.CommandId,
                NewOperationCommitted = began.Outcome.NewOperationCommitted,
            };
        }

        if (!began.Outcome.OwnsEffectAttempt)
        {
            return new StudioMemberWorkflowScheduleResult(
                Success: true,
                ScopeId: scopeId,
                MemberId: memberId,
                ScheduleId: scheduleId,
                PublishedServiceId: publishedServiceId,
                ObservatoryUrl: ObservatoryPath,
                Status: "pending")
            {
                OperationId = operationId,
                CommandId = began.Admission.CommandId,
                NewOperationCommitted = began.Outcome.NewOperationCommitted,
            };
        }

        var effectAttemptId = NormalizeRequired(
            began.Outcome.EffectAttemptId,
            nameof(began.Outcome.EffectAttemptId));
        var effectLocator = began.Outcome.CredentialEffectLocator
            ?? throw new InvalidOperationException("team_automation_credential_effect_locator_missing");
        if (effectLocator != requestedEffectLocator)
            throw new InvalidOperationException("team_automation_credential_effect_locator_conflict");
        StudioScheduledCredential? credential = null;
        var candidateCommitted = began.Outcome.CandidateCredential != null;
        var candidateCommitAttempted = candidateCommitted;
        var activationAttempted = false;
        TeamAutomationCommittedMutationReceipt activation;
        try
        {
            credential = candidateCommitted
                ? ToStudioScheduledCredential(
                    began.Outcome.CandidateCredential!,
                    began.Outcome.CandidateOwner)
                : await _credentialMaterializer.MaterializeAsync(
                    bearerToken,
                    validation.ValidatedPlan,
                    scheduleId,
                    operationId,
                    effectLocator,
                    began.Outcome.EffectAttemptGeneration,
                    ownerScope,
                    ct);
            EnsureCredentialMatchesPlan(credential, plan, _timeProvider.GetUtcNow());
            if (!candidateCommitted)
            {
                candidateCommitAttempted = true;
                var candidate = await _scheduleService.RecordTeamAutomationCredentialCandidateAsync(
                    scheduleId,
                    teamOwner,
                    operationId,
                    idempotencyKey,
                    effectAttemptId,
                    BuildScheduleCredential(credential),
                    credential.Owner,
                    ct);
                if (!candidate.Admission.Accepted)
                    throw new InvalidOperationException("team_automation_candidate_rejected");
                candidateCommitted = true;
            }
            var configuration = BuildScheduleConfiguration(
                scheduleId,
                displayName,
                scopeId,
                memberId,
                publishedServiceId,
                prompt,
                BuildScheduleAuth(credential, callerAuthority),
                CloneScheduleAuthorizationFact(activationDecision.AuthorizationFact),
                timing.CronExpression,
                timing.Timezone,
                request.Enabled,
                teamOwner,
                timing.ScheduleMode,
                timing.OneShotFireAt,
                activationDecision.Payload.Clone());

            activationAttempted = true;
            activation = await _scheduleService.CompleteTeamAutomationCredentialOperationAsync(
                scheduleId,
                teamOwner,
                operationId,
                idempotencyKey,
                effectAttemptId,
                BuildScheduleCredential(credential),
                configuration,
                ct);
            if (!activation.Admission.Accepted)
                throw new InvalidOperationException("team_automation_activation_rejected");
            _ = await ExecutePendingRevocationAsync(
                activation.Outcome,
                bearerToken,
                request.AuthenticatedOwner,
                teamOwner,
                resolved.TeamId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (candidateCommitted && !activationAttempted)
            {
                var failure = await TryRecordFailureAsync(
                    scheduleId,
                    teamOwner,
                    operationId,
                    idempotencyKey,
                    effectAttemptId,
                    ToStableFailureCode(ex),
                    CancellationToken.None);
                if (failure != null)
                {
                    _ = await ExecutePendingRevocationAsync(
                        failure,
                        bearerToken,
                        request.AuthenticatedOwner,
                        teamOwner,
                        resolved.TeamId,
                        CancellationToken.None);
                }
            }
            else if (!candidateCommitAttempted && credential != null)
            {
                try
                {
                    var revoked = await _credentialMaterializer.RevokeAsync(
                        bearerToken,
                        request.AuthenticatedOwner,
                        credential,
                        revokeNyxId: true,
                        revokeVault: true,
                        CancellationToken.None);
                    if (revoked.NyxIdRevoked && revoked.VaultRevoked)
                    {
                        _ = await TryRecordFailureAsync(
                            scheduleId,
                            teamOwner,
                            operationId,
                            idempotencyKey,
                            effectAttemptId,
                            ToStableFailureCode(ex),
                            CancellationToken.None);
                    }
                }
                catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        cleanupException,
                        "Failed to clean up an uncommitted scheduled credential for schedule {ScheduleId} and operation {OperationId}.",
                        scheduleId,
                        operationId);
                    // The pending operation and deterministic credential name are
                    // the recovery descriptor for the next fresh-owner retry.
                }
            }
            else if (!candidateCommitAttempted &&
                     credential == null &&
                     ex is StudioScheduledCredentialMaterializationException
                     {
                         EffectsCleaned: true,
                     } or StudioScheduledCredentialMaterializationException
                     {
                         RecoveryBlocked: true,
                     })
            {
                _ = await TryRecordFailureAsync(
                    scheduleId,
                    teamOwner,
                    operationId,
                    idempotencyKey,
                    effectAttemptId,
                    ToStableFailureCode(ex),
                    CancellationToken.None);
            }
            throw;
        }

        return new StudioMemberWorkflowScheduleResult(
            Success: activation.Admission.Accepted,
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleId: NormalizeRequired(activation.Admission.ScheduleId, nameof(activation.Admission.ScheduleId)),
            PublishedServiceId: publishedServiceId,
            ObservatoryUrl: ObservatoryPath,
            Status: "pending")
        {
            OperationId = operationId,
            CommandId = activation.Admission.CommandId,
            NewOperationCommitted = began.Outcome.NewOperationCommitted,
        };
    }

    private static ScheduledInvocationAuthorizationConfirmation BuildConfirmation(
        ScheduledInvocationAuthorizationRequest request,
        string permissionDigest,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ScheduledInvocationAuthorizationConfirmation
        {
            InvocationTarget = request.InvocationTarget.Clone(),
            Owner = request.Owner.Clone(),
            SchemaVersion = ScheduledInvocationAuthorizationContractVersions.Schema,
            PolicyVersion = NormalizeRequired(policyVersion, nameof(policyVersion)),
            PermissionDigest = NormalizeRequired(permissionDigest, nameof(permissionDigest)),
        };
    }

    private async Task<CatalogRefreshRecoveryResult> RefreshRecoverableNyxIdCatalogSnapshotAsync(
        ScheduledInvocationAuthorizationRequest authorizationRequest,
        IReadOnlyList<NyxIdUserServiceCapabilityRef>? resolvedRequiredServices,
        ScheduledInvocationLLMRefreshRequirement? llmRefreshRequirement,
        Func<CancellationToken, Task<string>> provisioningBearerTokenResolver,
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail,
        long observedCatalogStateVersion,
        CancellationToken ct)
    {
        var requiredServices = ResolveCatalogRefreshRequiredServices(authorizationRequest, resolvedRequiredServices);
        if (resolvedRequiredServices is { Count: 0 } &&
            requiredServices.Count == 0 &&
            llmRefreshRequirement == null)
        {
            return CatalogRefreshRecoveryResult.Failed(
                failureCode,
                $"nyxid_catalog_refresh_required_services_unavailable:{detail}");
        }

        string bearerToken;
        try
        {
            bearerToken = await provisioningBearerTokenResolver(ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return CatalogRefreshRecoveryResult.Failed(
                failureCode,
                $"nyxid_catalog_refresh_requires_bearer_token:{detail}");
        }

        if (_catalogRefreshPort is null)
        {
            return CatalogRefreshRecoveryResult.Failed(
                failureCode,
                $"nyxid_catalog_refresh_unavailable:{detail}");
        }

        NyxIdAuthorizationCatalogRefreshResult refresh;
        try
        {
            refresh = await _catalogRefreshPort.RefreshAsync(
                authorizationRequest.Owner,
                bearerToken,
                new NyxIdAuthorizationCatalogRefreshRequest(
                    requiredServices,
                    llmRefreshRequirement),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Failed to refresh NyxID authorization catalog for Studio member workflow schedule owner {OwnerKind}.",
                authorizationRequest.Owner.OwnerKind);
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();
        }

        if (refresh.Status == NyxIdAuthorizationCatalogRefreshStatus.Superseded)
        {
            if (refresh.StateVersion > 0 && observedCatalogStateVersion < refresh.StateVersion)
            {
                return CatalogRefreshRecoveryResult.ProjectionPending(
                    refresh.StateVersion,
                    observedCatalogStateVersion);
            }

            throw new StudioMemberAutomationCatalogRefreshSupersededException();
        }

        if (refresh.Status is NyxIdAuthorizationCatalogRefreshStatus.Failed or
            NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut or
            NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable)
        {
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();
        }

        if (!refresh.Success)
        {
            var refreshFailureCode = string.IsNullOrWhiteSpace(refresh.FailureCode)
                ? refresh.Status.ToString()
                : refresh.FailureCode.Trim();
            return CatalogRefreshRecoveryResult.Failed(
                failureCode,
                $"nyxid_catalog_refresh_failed:{refreshFailureCode}");
        }

        return CatalogRefreshRecoveryResult.Succeeded(refresh);
    }

    private static IReadOnlyList<NyxIdUserServiceCapabilityRef> ResolveCatalogRefreshRequiredServices(
        ScheduledInvocationAuthorizationRequest authorizationRequest,
        IReadOnlyList<NyxIdUserServiceCapabilityRef>? resolvedRequiredServices)
    {
        if (resolvedRequiredServices is { Count: > 0 })
            return resolvedRequiredServices;
        return authorizationRequest.RequiredNyxIdServices;
    }

    private async Task<ResolvedStudioAuthorizationRequest> ResolveAuthorizationRequestAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct,
        DateTimeOffset? credentialExpiresAtUtc = null)
    {
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var memberId = NormalizeRequired(request.MemberId, nameof(request.MemberId));
        StudioMemberDetailResponse member;
        try
        {
            member = await _memberService.GetAsync(scopeId, memberId, ct);
        }
        catch (StudioMemberNotFoundException)
        {
            return ResolveAcceptedBindingAuthorizationRequest(
                request,
                scopeId,
                memberId,
                credentialExpiresAtUtc)
                ?? throw new StudioMemberAutomationNotFoundException();
        }
        if (!string.Equals(member.Summary.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal))
            throw new InvalidOperationException($"member_id '{memberId}' is not a workflow member and cannot be scheduled as a workflow.");

        var publishedServiceId = NormalizeRequired(member.Summary.PublishedServiceId, nameof(member.Summary.PublishedServiceId));
        var teamId = NormalizeRequired(member.Summary.TeamId, nameof(member.Summary.TeamId));
        if (!string.IsNullOrWhiteSpace(request.TeamId) &&
            !string.Equals(teamId, request.TeamId.Trim(), StringComparison.Ordinal))
        {
            throw new StudioMemberAutomationNotFoundException();
        }

        try
        {
            EnsureWorkflowBindingCanBeScheduled(member, memberId, publishedServiceId);
        }
        catch (InvalidOperationException) when (request.AcceptedBinding is not null)
        {
            return BuildResolvedAuthorizationRequest(
                request,
                scopeId,
                teamId,
                memberId,
                publishedServiceId,
                NormalizeRequired(request.AcceptedBinding.WorkflowId, nameof(request.AcceptedBinding.WorkflowId)),
                NormalizeOptional(request.AcceptedBinding.WorkflowRevisionId) ?? string.Empty,
                credentialExpiresAtUtc);
        }

        var workflowRevision = member.LastBinding?.RevisionId ?? member.Summary.LastBoundRevisionId ?? string.Empty;
        var workflowId = NormalizeRequired(member.ImplementationRef?.WorkflowId, "workflowId");
        return BuildResolvedAuthorizationRequest(
            request,
            scopeId,
            teamId,
            memberId,
            publishedServiceId,
            workflowId,
            workflowRevision,
            credentialExpiresAtUtc);
    }

    private ResolvedStudioAuthorizationRequest? ResolveAcceptedBindingAuthorizationRequest(
        StudioMemberWorkflowScheduleRequest request,
        string scopeId,
        string memberId,
        DateTimeOffset? credentialExpiresAtUtc)
    {
        if (request.AcceptedBinding is not { } acceptedBinding)
            return null;

        var acceptedTeamId = NormalizeRequired(acceptedBinding.TeamId, nameof(acceptedBinding.TeamId));
        if (!string.IsNullOrWhiteSpace(request.TeamId) &&
            !string.Equals(acceptedTeamId, request.TeamId.Trim(), StringComparison.Ordinal))
        {
            throw new StudioMemberAutomationNotFoundException();
        }

        return BuildResolvedAuthorizationRequest(
            request,
            scopeId,
            acceptedTeamId,
            memberId,
            NormalizeRequired(acceptedBinding.PublishedServiceId, nameof(acceptedBinding.PublishedServiceId)),
            NormalizeRequired(acceptedBinding.WorkflowId, nameof(acceptedBinding.WorkflowId)),
            NormalizeOptional(acceptedBinding.WorkflowRevisionId) ?? string.Empty,
            credentialExpiresAtUtc);
    }

    private ResolvedStudioAuthorizationRequest BuildResolvedAuthorizationRequest(
        StudioMemberWorkflowScheduleRequest request,
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        string workflowId,
        string workflowRevision,
        DateTimeOffset? credentialExpiresAtUtc)
    {
        var target = new ScheduledInvocationTarget
        {
            StudioMember = new StudioMemberInvocationTarget
            {
                ScopeId = scopeId,
                TeamId = teamId,
                MemberId = memberId,
                PublishedServiceId = publishedServiceId,
                DraftWorkflowId = workflowId,
                WorkflowRevisionId = workflowRevision,
            },
        };
        var evaluatedAtUtc = _timeProvider.GetUtcNow();
        return new ResolvedStudioAuthorizationRequest(
            scopeId,
            teamId,
            memberId,
            publishedServiceId,
            new ScheduledInvocationAuthorizationRequest(
                target,
                request.AuthenticatedOwner,
                [],
                AuthorizationGrantRequirement.Required,
                credentialExpiresAtUtc ?? _schedulePolicy.ResolveCredentialExpiresAtUtc(evaluatedAtUtc),
                evaluatedAtUtc,
                TrustedMemberEvidence: BuildTrustedMemberEvidence(
                    request,
                    publishedServiceId,
                    workflowId,
                    workflowRevision),
                TrustedWorkflowEvidence: BuildTrustedWorkflowEvidence(
                    request,
                    publishedServiceId,
                    workflowId,
                    workflowRevision)));
    }

    private static ScheduledInvocationMemberEvidence? BuildTrustedMemberEvidence(
        StudioMemberWorkflowScheduleRequest request,
        string publishedServiceId,
        string workflowId,
        string workflowRevision)
    {
        if (request.AcceptedBinding is not { } acceptedBinding)
            return null;

        var acceptedPublishedServiceId = NormalizeRequired(
            acceptedBinding.PublishedServiceId,
            nameof(acceptedBinding.PublishedServiceId));
        var acceptedWorkflowId = NormalizeRequired(
            acceptedBinding.WorkflowId,
            nameof(acceptedBinding.WorkflowId));
        var acceptedWorkflowRevision = NormalizeRequired(
            acceptedBinding.WorkflowRevisionId,
            nameof(acceptedBinding.WorkflowRevisionId));
        if (!string.Equals(acceptedPublishedServiceId, publishedServiceId, StringComparison.Ordinal) ||
            !string.Equals(acceptedWorkflowId, workflowId, StringComparison.Ordinal) ||
            !string.Equals(acceptedWorkflowRevision, workflowRevision, StringComparison.Ordinal))
        {
            return null;
        }

        return new ScheduledInvocationMemberEvidence(
            StateVersion: 0,
            DraftWorkflowId: acceptedWorkflowId,
            WorkflowRevisionId: acceptedWorkflowRevision,
            PublishedServiceId: acceptedPublishedServiceId);
    }

    private static ScheduledInvocationWorkflowEvidence? BuildTrustedWorkflowEvidence(
        StudioMemberWorkflowScheduleRequest request,
        string publishedServiceId,
        string workflowId,
        string workflowRevision)
    {
        if (BuildTrustedMemberEvidence(request, publishedServiceId, workflowId, workflowRevision) is null)
            return null;

        return request.AcceptedBinding?.WorkflowEvidence;
    }

    private async Task<string> ResolveProvisioningBearerTokenAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(request.ProvisioningBearerToken);
        if (parsed.IsValid)
            return parsed.NormalizedBearerToken!;

        var owner = request.AuthenticatedOwner ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_required");
        var subjectPlatform = NormalizeOptional(owner.SubjectPlatform);
        var subjectExternalUserId = NormalizeOptional(owner.SubjectExternalUserId);
        var bindingId = NormalizeOptional(owner.VerifiedBindingId);
        if (subjectPlatform is null || subjectExternalUserId is null)
            throw new UnauthorizedAccessException("authenticated_authorization_owner_incomplete");
        if (bindingId is null)
            throw new UnauthorizedAccessException("authenticated_authorization_owner_binding_missing");
        if (_callerAccessTokenProvider is null)
            throw new InvalidOperationException("workflow_caller_access_token_provider_unavailable");

        var issued = await _callerAccessTokenProvider.IssueAsync(
            new WorkflowCallerNyxIdAuthority
            {
                Platform = subjectPlatform,
                Tenant = NormalizeOptional(owner.SubjectTenant) ?? string.Empty,
                ExternalUserId = subjectExternalUserId,
                Scope = ProvisioningBearerCapabilityScope,
                BindingId = bindingId,
            },
            ct);
        var issuedToken = WorkflowCallerCredentialTokens.ParseOptional(issued);
        return issuedToken.IsValid
            ? issuedToken.NormalizedBearerToken!
            : throw new InvalidOperationException("workflow_caller_access_token_provider_returned_invalid_token");
    }

    private async Task<ResolvedTeamMember> ResolveTeamMemberAsync(
        string scopeId,
        string teamId,
        string memberId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedTeamId = NormalizeRequired(teamId, nameof(teamId));
        var normalizedMemberId = NormalizeRequired(memberId, nameof(memberId));
        StudioMemberDetailResponse member;
        try
        {
            member = await _memberService.GetAsync(normalizedScopeId, normalizedMemberId, ct);
        }
        catch (StudioMemberNotFoundException)
        {
            throw new StudioMemberAutomationNotFoundException();
        }
        var actualTeamId = NormalizeRequired(member.Summary.TeamId, nameof(member.Summary.TeamId));
        if (!string.Equals(actualTeamId, normalizedTeamId, StringComparison.Ordinal))
            throw new StudioMemberAutomationNotFoundException();
        if (!string.Equals(
                member.Summary.ImplementationKind,
                MemberImplementationKindNames.Workflow,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_member_is_not_workflow");
        }

        var publishedServiceId = NormalizeRequired(
            member.Summary.PublishedServiceId,
            nameof(member.Summary.PublishedServiceId));
        EnsureWorkflowBindingCanBeScheduled(member, normalizedMemberId, publishedServiceId);
        return new ResolvedTeamMember(
            normalizedScopeId,
            normalizedTeamId,
            normalizedMemberId,
            publishedServiceId);
    }

    private static StudioMemberAutomationView MapView(
        ScheduledDispatchSummary schedule,
        ResolvedTeamMember member) =>
        new StudioMemberAutomationView(
            member.ScopeId,
            member.TeamId,
            member.MemberId,
            schedule.ScheduleId,
            schedule.ServiceId,
            schedule.DisplayName,
            schedule.Prompt ?? string.Empty,
            schedule.CronExpression,
            schedule.Timezone,
            schedule.Enabled,
            ToStatusName(schedule.TeamAutomationLifecycleStatus),
            schedule.CredentialExpiresAt,
            schedule.LastAuthorizationErrorCode,
            schedule.TeamAutomationOperationId,
            schedule.CredentialGeneration,
            schedule.RevocationPending,
            schedule.NextFireAt,
            schedule.LastFireAt,
            schedule.StateVersion)
        {
            CredentialSourceKind = "scheduled_invocation_agent_key",
            UpdatedAt = schedule.UpdatedAt,
            OwnerLLMRouteKind = schedule.OwnerLLMRouteKind,
            OwnerLLMRoute = schedule.OwnerLLMRoute,
            OwnerLLMUserServiceId = schedule.OwnerLLMUserServiceId,
            OwnerLLMServiceSlug = schedule.OwnerLLMServiceSlug,
            OwnerLLMModel = schedule.OwnerLLMModel,
            NyxIdRevocationStatus = schedule.NyxIdRevocationStatus,
            VaultRevocationStatus = schedule.VaultRevocationStatus,
        };

    private static StudioMemberAutomationView MapView(ScheduledDispatchSummary schedule) =>
        new StudioMemberAutomationView(
            NormalizeRequired(schedule.TeamOwnerScopeId, nameof(schedule.TeamOwnerScopeId)),
            NormalizeRequired(schedule.TeamId, nameof(schedule.TeamId)),
            NormalizeRequired(schedule.TeamOwnerMemberId, nameof(schedule.TeamOwnerMemberId)),
            schedule.ScheduleId,
            schedule.ServiceId,
            schedule.DisplayName,
            schedule.Prompt ?? string.Empty,
            schedule.CronExpression,
            schedule.Timezone,
            schedule.Enabled,
            ToStatusName(schedule.TeamAutomationLifecycleStatus),
            schedule.CredentialExpiresAt,
            schedule.LastAuthorizationErrorCode,
            schedule.TeamAutomationOperationId,
            schedule.CredentialGeneration,
            schedule.RevocationPending,
            schedule.NextFireAt,
            schedule.LastFireAt,
            schedule.StateVersion)
        {
            CredentialSourceKind = "scheduled_invocation_agent_key",
            UpdatedAt = schedule.UpdatedAt,
            OwnerLLMRouteKind = schedule.OwnerLLMRouteKind,
            OwnerLLMRoute = schedule.OwnerLLMRoute,
            OwnerLLMUserServiceId = schedule.OwnerLLMUserServiceId,
            OwnerLLMServiceSlug = schedule.OwnerLLMServiceSlug,
            OwnerLLMModel = schedule.OwnerLLMModel,
            NyxIdRevocationStatus = schedule.NyxIdRevocationStatus,
            VaultRevocationStatus = schedule.VaultRevocationStatus,
        };

    private static string ToStatusName(TeamAutomationLifecycleStatus status) => status switch
    {
        TeamAutomationLifecycleStatus.ProvisioningPending => "provisioning_pending",
        TeamAutomationLifecycleStatus.Active => "active",
        TeamAutomationLifecycleStatus.NeedsAuthorization => "needs_authorization",
        TeamAutomationLifecycleStatus.ReplacementPending => "replacement_pending",
        TeamAutomationLifecycleStatus.Deleting => "deleting",
        TeamAutomationLifecycleStatus.RevocationPending => "revocation_pending",
        TeamAutomationLifecycleStatus.Failed => "failed",
        _ => "needs_authorization",
    };

    private static StudioMemberAutomationMutationReceipt ToMutationReceipt(
        ScheduledDispatchMutationReceipt receipt,
        string operationId,
        string status = "accepted") =>
        new(
            receipt.Accepted,
            status,
            receipt.ScheduleId,
            NormalizeRequired(operationId, nameof(operationId)),
            receipt.CommandId);

    private async Task<ScheduledDispatchMutationReceipt> EnsureScheduleAsync(
        string scheduleId,
        string? displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        ScheduledInvocationAuthorizationFact authorizationFact,
        ScheduledDispatchMutationContext mutationContext,
        string cronExpression,
        string timezone,
        bool enabled,
        CancellationToken ct)
        => await _scheduleService.EnsureAsync(
            BuildScheduleConfiguration(
                scheduleId,
                displayName,
                scopeId,
                memberId,
                publishedServiceId,
                prompt,
                auth,
                authorizationFact,
                cronExpression,
                timezone,
                enabled,
                mutationContext.TeamAutomationOwner!,
                ScheduledDispatchScheduleMode.RecurringCron,
                null),
            mutationContext,
            ct);

    private static void EnsureWorkflowBindingCanBeScheduled(
        StudioMemberDetailResponse member,
        string memberId,
        string publishedServiceId)
    {
        if (member.LastBinding is not null)
        {
            var boundPublishedServiceId = NormalizeRequired(
                member.LastBinding.PublishedServiceId,
                nameof(member.LastBinding.PublishedServiceId));
            if (!string.Equals(publishedServiceId, boundPublishedServiceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"member_id '{memberId}' binding service id does not match the published service id.");
            }

            return;
        }

        if (member.CurrentBindingRun is null || !IsSchedulableCurrentBindingRun(member.CurrentBindingRun.Status))
        {
            throw new InvalidOperationException(
                $"member_id '{memberId}' has no bound workflow. Bind workflow YAML before scheduling the member.");
        }
    }

    private static bool IsSchedulableCurrentBindingRun(string? status) => status switch
    {
        StudioMemberBindingRunStatusNames.Accepted => true,
        StudioMemberBindingRunStatusNames.AdmissionPending => true,
        StudioMemberBindingRunStatusNames.Admitted => true,
        StudioMemberBindingRunStatusNames.PlatformBindingPending => true,
        StudioMemberBindingRunStatusNames.MemberNotificationPending => true,
        StudioMemberBindingRunStatusNames.Succeeded => true,
        _ => false,
    };

    private static StudioMemberWorkflowScheduleTiming ResolveScheduleTiming(
        StudioMemberWorkflowScheduleRequest request)
    {
        return request.ScheduleMode switch
        {
            ScheduledDispatchScheduleMode.RecurringCron => new StudioMemberWorkflowScheduleTiming(
                NormalizeRequired(request.ScheduleCron, nameof(request.ScheduleCron)),
                NormalizeRequired(request.ScheduleTimezone, nameof(request.ScheduleTimezone)),
                ScheduledDispatchScheduleMode.RecurringCron,
                null),
            ScheduledDispatchScheduleMode.OneShotAtUtc => new StudioMemberWorkflowScheduleTiming(
                NormalizeOptional(request.ScheduleCron) ?? string.Empty,
                NormalizeOptional(request.ScheduleTimezone) ?? ScheduledDispatchCalculator.DefaultTimezone,
                ScheduledDispatchScheduleMode.OneShotAtUtc,
                request.OneShotFireAt?.ToUniversalTime()
                    ?? throw new InvalidOperationException("one_shot_fire_at_required")),
            _ => throw new InvalidOperationException("schedule_mode_invalid"),
        };
    }

    private readonly record struct StudioMemberWorkflowScheduleTiming(
        string CronExpression,
        string Timezone,
        ScheduledDispatchScheduleMode ScheduleMode,
        DateTimeOffset? OneShotFireAt);

    private static ScheduledDispatchConfiguration BuildScheduleConfiguration(
        string scheduleId,
        string? displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth? auth,
        ScheduledInvocationAuthorizationFact? authorizationFact,
        string cronExpression,
        string timezone,
        bool enabled,
        TeamMemberAutomationOwner teamOwner,
        ScheduledDispatchScheduleMode scheduleMode,
        DateTimeOffset? oneShotFireAt,
        Any? payload = null) =>
        new(
            ScheduleId: scheduleId,
            DisplayName: NormalizeOptional(displayName) ?? $"studio-member-workflow-{memberId}",
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
                    Payload: payload?.Clone() ?? Any.Pack(BuildChatRequest(prompt, scopeId, authorizationFact)),
                    Auth: auth,
                    AuthorizationFact: authorizationFact)),
            CronExpression: cronExpression,
            Timezone: timezone,
            Enabled: enabled,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
            ScheduleMode: scheduleMode,
            OneShotFireAt: oneShotFireAt)
        {
            TeamAutomationOwner = teamOwner,
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
        };

    internal static ScheduledInvocationAuthorizationFact ToScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var policy = plan.CredentialPolicy
            ?? throw new InvalidOperationException("scheduled_authorization_policy_missing");
        var catalog = plan.CatalogAuthority;
        if (catalog is null && policy.ServiceGrantRequirement != AuthorizationGrantRequirement.NotRequired)
            throw new InvalidOperationException("scheduled_authorization_catalog_authority_missing");
        var disclosure = plan.Disclosures.ToHashSet();
        var grants = plan.NyxIdServiceGrants.Select(static grant =>
            new ScheduledInvocationAuthorizationServiceGrant(
                grant.UserServiceId,
                grant.NodeIds.ToArray(),
                grant.NodeGrantRequirement == AuthorizationGrantRequirement.NotRequired))
            .ToArray();
        return new ScheduledInvocationAuthorizationFact(
            plan.PermissionDigest,
            policy.PolicyVersion,
            new ScheduledInvocationAuthorizationOwner(
                plan.Owner.Authority,
                plan.Owner.OwnerKind.ToString(),
                plan.Owner.OwnerSubject),
            grants,
            string.Join(' ', policy.Scopes.Select(ToScopeName).Order(StringComparer.Ordinal)),
            policy.ExpiresAt.ToDateTimeOffset(),
            policy.ServiceGrantRequirement == AuthorizationGrantRequirement.NotRequired,
            new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationDisclosure(
                disclosure.Contains(ScheduledInvocationDisclosure.DedicatedCredential),
                disclosure.Contains(ScheduledInvocationDisclosure.AevatarSecretCustody),
                !disclosure.Contains(ScheduledInvocationDisclosure.BrowserNeverReceivesSecret),
                disclosure.Contains(ScheduledInvocationDisclosure.DeleteRevokesCredential),
                !disclosure.Contains(ScheduledInvocationDisclosure.PauseResumePreservesCredential)),
            new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationAuthority(
                SourceVersion(plan, AuthorizationSourceKind.StudioMember),
                SourceVersion(plan, AuthorizationSourceKind.WorkflowRevision),
                SourceVersion(plan, AuthorizationSourceKind.ConnectorCatalog),
                SourceVersion(plan, AuthorizationSourceKind.OwnerLlmRoute),
                catalog?.ActorStateVersion ?? 0,
                catalog?.ObservedAt?.ToDateTimeOffset() ?? default,
                catalog?.FreshUntil?.ToDateTimeOffset() ?? default,
                catalog?.ContentDigest ?? string.Empty,
                catalog?.ContractVersion ?? string.Empty,
                catalog?.PolicyVersion ?? string.Empty,
                catalog?.EvaluatedAt?.ToDateTimeOffset() ?? default),
            plan.OwnerLlmSelection?.Clone());
    }

    private static ChatRequestEvent BuildChatRequest(
        string prompt,
        string scopeId,
        ScheduledInvocationAuthorizationFact? fact)
    {
        var request = new ChatRequestEvent
        {
            Prompt = prompt,
            ScopeId = scopeId,
        };
        if (fact?.OwnerLLMSelection is { } selection &&
            selection.RouteKind != LLMRouteKind.Unspecified)
        {
            if (!ScheduledInvocationOwnerLLMSelectionPolicy.IsDurableSelectionValid(selection))
                throw new InvalidOperationException("scheduled_owner_llm_selection_invalid");
            request.LlmControl = new LLMControlContextPayload
            {
                ModelOverride = selection.Model,
                NyxIdRoutePreference = selection.RouteValue,
            };
        }

        return request;
    }

    private static string ToScopeName(NyxIdCredentialScope scope) => scope switch
    {
        NyxIdCredentialScope.Read => "read",
        NyxIdCredentialScope.Proxy => "proxy",
        _ => throw new InvalidOperationException("scheduled_authorization_scope_invalid"),
    };

    private static long SourceVersion(
        ScheduledInvocationAuthorizationPlan plan,
        AuthorizationSourceKind sourceKind) =>
        plan.SourceStamps.FirstOrDefault(stamp => stamp.SourceKind == sourceKind)?.StateVersion ?? 0;

    private static ScheduledServiceInvocationAuth BuildScheduleAuth(
        StudioScheduledCredential credential,
        ScheduledCallerNyxIdAuthority callerAuthority)
    {
        ArgumentNullException.ThrowIfNull(callerAuthority);
        return new ScheduledServiceInvocationAuth(BuildScheduleCredential(credential))
        {
            CallerAuthority = callerAuthority.Clone(),
        };
    }

    private static ScheduledCallerNyxIdAuthority BuildScheduleCallerAuthority(
        AuthenticatedAuthorizationOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var bindingId = NormalizeOptional(owner.VerifiedBindingId) ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_binding_missing");
        return new ScheduledCallerNyxIdAuthority
        {
            Platform = NormalizeRequired(owner.SubjectPlatform, nameof(owner.SubjectPlatform)),
            Tenant = NormalizeOptional(owner.SubjectTenant) ?? string.Empty,
            ExternalUserId = NormalizeRequired(
                owner.SubjectExternalUserId,
                nameof(owner.SubjectExternalUserId)),
            Scope = ProvisioningBearerCapabilityScope,
            BindingId = bindingId,
        };
    }

    private static ScheduledInvocationAgentKeyCredentialReference BuildScheduleCredential(
        StudioScheduledCredential credential) =>
        new(
            credential.SecretReference.Clone(),
            credential.ApiKeyId,
            credential.ExpiresAtUtc.ToUnixTimeMilliseconds());

    private static StudioScheduledCredential ToStudioScheduledCredential(
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new StudioScheduledCredential(
            NormalizeRequired(credential.ApiKeyId, nameof(credential.ApiKeyId)),
            credential.SecretReference?.Clone()
                ?? throw new InvalidOperationException("revocation_descriptor_missing"),
            DateTimeOffset.FromUnixTimeMilliseconds(credential.KeyExpiresAtUnixMs),
            owner ?? throw new InvalidOperationException("credential_owner_missing"));
    }

    private async Task<bool> ExecutePendingRevocationAsync(
        TeamAutomationOperationCommittedOutcome outcome,
        string bearerToken,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        TeamMemberAutomationOwner scheduleOwner,
        string teamId,
        CancellationToken ct)
    {
        if (!outcome.NyxIdRevocationPending && !outcome.VaultRevocationPending)
            return true;
        if (!outcome.OwnsEffectAttempt)
            return false;

        StudioScheduledCredentialRevocationResult result;
        if (outcome.PendingRevocationCredential == null || outcome.PendingRevocationOwner == null)
        {
            result = new StudioScheduledCredentialRevocationResult(
                NyxIdRevoked: !outcome.NyxIdRevocationPending,
                VaultRevoked: !outcome.VaultRevocationPending,
                ErrorCode: "revocation_descriptor_missing");
        }
        else
        {
            var pending = outcome.PendingRevocationCredential;
            var credential = new StudioScheduledCredential(
                pending.ApiKeyId,
                pending.SecretReference.Clone(),
                DateTimeOffset.FromUnixTimeMilliseconds(pending.KeyExpiresAtUnixMs),
                outcome.PendingRevocationOwner);
            try
            {
                result = await _credentialMaterializer.RevokeAsync(
                    bearerToken,
                    authenticatedOwner,
                    credential,
                    outcome.NyxIdRevocationPending,
                    outcome.VaultRevocationPending,
                    ct);
            }
            catch (UnauthorizedAccessException)
            {
                result = new StudioScheduledCredentialRevocationResult(
                    NyxIdRevoked: !outcome.NyxIdRevocationPending,
                    VaultRevoked: !outcome.VaultRevocationPending,
                    ErrorCode: "credential_owner_mismatch");
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                result = new StudioScheduledCredentialRevocationResult(
                    NyxIdRevoked: !outcome.NyxIdRevocationPending,
                    VaultRevoked: !outcome.VaultRevocationPending,
                    ErrorCode: "credential_revocation_transient");
            }
        }

        var completion = await _scheduleService.CompleteTeamAutomationRevocationAsync(
            outcome.ScheduleId,
            scheduleOwner,
            outcome.OperationId,
            outcome.IdempotencyKey,
            NormalizeRequired(outcome.EffectAttemptId, nameof(outcome.EffectAttemptId)),
            result.NyxIdRevoked,
            result.VaultRevoked,
            result.ErrorCode,
            ct);
        if (result.NyxIdRevoked && result.VaultRevoked)
        {
            var committed = completion.Outcome;
            if (!completion.Admission.Accepted ||
                committed.Status != TeamAutomationOperationObservationStatus.Committed ||
                !string.Equals(
                    committed.Stage,
                    TeamAutomationOperationObservationStages.Revocation,
                    StringComparison.Ordinal) ||
                !string.Equals(committed.ScheduleId, outcome.ScheduleId, StringComparison.Ordinal) ||
                !string.Equals(committed.OperationId, outcome.OperationId, StringComparison.Ordinal) ||
                committed.NyxIdRevocationPending ||
                committed.VaultRevocationPending ||
                committed.StateVersion <= 0 ||
                committed.ObservedAtUtc == default)
            {
                throw new InvalidOperationException("team_automation_revocation_completion_not_committed");
            }

            _auditLogger.LogInformation(
                new EventId(
                    StudioMemberAutomationAuditContract.RevocationCompletedEventId,
                    StudioMemberAutomationAuditContract.RevocationCompletedEventName),
                "Completed Studio member automation revocation for scope {ScopeId}, team {TeamId}, member {MemberId}, " +
                "schedule {ScheduleId}, operation {OperationId}, NyxID status {NyxIdRevocationStatus}, " +
                "Vault status {VaultRevocationStatus}, state version {StateVersion}, observed at {ObservedAtUtc:O}.",
                scheduleOwner.ScopeId,
                NormalizeRequired(teamId, nameof(teamId)),
                scheduleOwner.MemberId,
                committed.ScheduleId,
                committed.OperationId,
                StudioMemberAutomationAuditContract.CompletedRevocationStatus,
                StudioMemberAutomationAuditContract.CompletedRevocationStatus,
                committed.StateVersion,
                committed.ObservedAtUtc);
        }
        return result.NyxIdRevoked && result.VaultRevoked;
    }

    private static void EnsureRequiredDisclosures(ScheduledInvocationAuthorizationPlan plan)
    {
        var disclosures = plan.Disclosures.ToHashSet();
        var required = new[]
        {
            ScheduledInvocationDisclosure.DedicatedCredential,
            ScheduledInvocationDisclosure.AevatarSecretCustody,
            ScheduledInvocationDisclosure.BrowserNeverReceivesSecret,
            ScheduledInvocationDisclosure.DeleteRevokesCredential,
            ScheduledInvocationDisclosure.PauseResumePreservesCredential,
        };
        if (required.Any(disclosure => !disclosures.Contains(disclosure)))
            throw new InvalidOperationException("scheduled_authorization_disclosures_missing");
    }

    private static void EnsureCredentialOwnerMatches(
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        ScheduledInvocationAuthorizationOwner? credentialOwner)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        var owner = authenticatedOwner.Owner;
        if (credentialOwner == null || owner == null ||
            !string.Equals(owner.Authority?.Trim(), credentialOwner.Authority, StringComparison.Ordinal) ||
            !string.Equals(owner.OwnerKind.ToString(), credentialOwner.OwnerKind, StringComparison.Ordinal) ||
            !string.Equals(owner.OwnerSubject?.Trim(), credentialOwner.OwnerSubject, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("credential_owner_mismatch");
        }
    }

    private static void EnsureExistingCredentialOwnerMatches(
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        ScheduledDispatchSummary schedule) =>
        EnsureCredentialOwnerMatches(
            authenticatedOwner,
            new ScheduledInvocationAuthorizationOwner(
                NormalizeRequired(schedule.CredentialOwnerAuthority, nameof(schedule.CredentialOwnerAuthority)),
                NormalizeRequired(schedule.CredentialOwnerKind, nameof(schedule.CredentialOwnerKind)),
                NormalizeRequired(schedule.CredentialOwnerSubject, nameof(schedule.CredentialOwnerSubject))));

    private static ScheduledInvocationAuthorizationOwner ToAuthorizationOwner(
        AuthenticatedAuthorizationOwnerContext authenticatedOwner)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        var owner = authenticatedOwner.Owner ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_missing");
        return new ScheduledInvocationAuthorizationOwner(
            NormalizeRequired(owner.Authority, nameof(owner.Authority)),
            owner.OwnerKind.ToString(),
            NormalizeRequired(owner.OwnerSubject, nameof(owner.OwnerSubject)));
    }

    private static TeamAutomationActivationDecision BuildTeamAutomationActivationDecision(
        string scheduleId,
        string displayName,
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        Any payload,
        ScheduledCallerNyxIdAuthority callerAuthority,
        ScheduledInvocationAuthorizationFact authorizationFact,
        string cronExpression,
        string timezone,
        bool enabled,
        ScheduledDispatchScheduleMode scheduleMode,
        DateTimeOffset? oneShotFireAt) =>
        new(
            scheduleId,
            displayName,
            new TeamMemberAutomationOwner(scopeId, memberId, teamId),
            new ServiceIdentity
            {
                TenantId = scopeId,
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                ServiceId = publishedServiceId,
            },
            WorkflowInvokeEndpointId,
            payload.Clone(),
            callerAuthority.Clone(),
            CloneScheduleAuthorizationFact(authorizationFact),
            cronExpression,
            timezone,
            enabled,
            ScheduledDispatchScheduleKind.Workflow,
            new Dictionary<string, string>(StringComparer.Ordinal),
            scheduleMode,
            oneShotFireAt,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            string.Empty,
            null);

    private static ScheduledInvocationAuthorizationFact CloneScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationFact fact) =>
        new(
            fact.PermissionDigest,
            fact.PolicyVersion,
            new ScheduledInvocationAuthorizationOwner(
                fact.Owner.Authority,
                fact.Owner.OwnerKind,
                fact.Owner.OwnerSubject),
            fact.ServiceGrants.Select(static grant =>
                new ScheduledInvocationAuthorizationServiceGrant(
                    grant.ServiceId,
                    grant.NodeIds.ToArray(),
                    grant.NodeGrantsNotRequired)).ToArray(),
            fact.Scopes,
            fact.ExpiresAt,
            fact.ServiceGrantsNotRequired,
            new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationDisclosure(
                fact.Disclosure.DedicatedToSchedule,
                fact.Disclosure.SecretManagedByAevatar,
                fact.Disclosure.BrowserReceivesRawKey,
                fact.Disclosure.DeleteRevokesCredential,
                fact.Disclosure.PauseResumeRevokesCredential),
            new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationAuthority(
                fact.Authority.MemberStateVersion,
                fact.Authority.WorkflowStateVersion,
                fact.Authority.ConnectorStateVersion,
                fact.Authority.OwnerLlmStateVersion,
                fact.Authority.CatalogStateVersion,
                fact.Authority.CatalogObservedAt,
                fact.Authority.CatalogFreshUntil,
                fact.Authority.CatalogContentDigest,
                fact.Authority.CatalogContractVersion,
                fact.Authority.CatalogPolicyVersion,
                fact.Authority.CatalogEvaluatedAt),
            fact.OwnerLLMSelection?.Clone());

    private static string BuildTeamAutomationMutationDigest(TeamAutomationActivationDecision decision)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestValue(hash, "aevatar.team-automation-mutation.v4");
        AppendDigestValue(hash, decision.ScheduleId);
        AppendDigestValue(hash, decision.DisplayName);
        AppendDigestValue(hash, decision.Owner.ScopeId);
        AppendDigestValue(hash, decision.Owner.MemberId);
        AppendDigestValue(hash, decision.Owner.TeamId);
        AppendDigestValue(hash, decision.ServiceIdentity.TenantId);
        AppendDigestValue(hash, decision.ServiceIdentity.AppId);
        AppendDigestValue(hash, decision.ServiceIdentity.Namespace);
        AppendDigestValue(hash, decision.ServiceIdentity.ServiceId);
        AppendDigestValue(hash, decision.EndpointId);
        AppendDigestValue(hash, decision.Payload.TypeUrl);
        AppendDigestBytes(hash, decision.Payload.Value.Span);
        AppendDigestValue(hash, decision.CallerAuthority.Platform);
        AppendDigestValue(hash, decision.CallerAuthority.Tenant);
        AppendDigestValue(hash, decision.CallerAuthority.ExternalUserId);
        AppendDigestValue(hash, decision.CallerAuthority.Scope);
        AppendDigestValue(hash, decision.CallerAuthority.BindingId);
        AppendAuthorizationFactDigest(hash, decision.AuthorizationFact);
        AppendDigestValue(hash, decision.CronExpression);
        AppendDigestValue(hash, decision.Timezone);
        AppendDigestBoolean(hash, decision.Enabled);
        AppendDigestInt64(hash, (long)decision.ScheduleKind);
        AppendDigestInt64(hash, decision.Headers.Count);
        foreach (var (key, value) in decision.Headers.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendDigestValue(hash, key);
            AppendDigestValue(hash, value);
        }
        AppendDigestInt64(hash, (long)decision.ScheduleMode);
        AppendDigestBoolean(hash, decision.OneShotFireAt.HasValue);
        if (decision.OneShotFireAt.HasValue)
            AppendDigestInt64(hash, decision.OneShotFireAt.Value.ToUniversalTime().UtcTicks);
        AppendDigestInt64(hash, (long)decision.CredentialRequirementTargetKind);
        AppendDigestValue(hash, decision.RevisionId);
        AppendDigestBoolean(hash, decision.Caller != null);
        if (decision.Caller != null)
        {
            AppendDigestValue(hash, decision.Caller.ServiceKey);
            AppendDigestValue(hash, decision.Caller.TenantId);
            AppendDigestValue(hash, decision.Caller.AppId);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendAuthorizationFactDigest(
        IncrementalHash hash,
        ScheduledInvocationAuthorizationFact fact)
    {
        AppendDigestValue(hash, fact.PermissionDigest);
        AppendDigestValue(hash, fact.PolicyVersion);
        AppendDigestValue(hash, fact.Owner.Authority);
        AppendDigestValue(hash, fact.Owner.OwnerKind);
        AppendDigestValue(hash, fact.Owner.OwnerSubject);
        var grants = fact.ServiceGrants
            .OrderBy(static grant => grant.ServiceId, StringComparer.Ordinal)
            .ThenBy(static grant => grant.NodeGrantsNotRequired)
            .ThenBy(static grant => string.Join('\n', grant.NodeIds.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToArray();
        AppendDigestInt64(hash, grants.Length);
        foreach (var grant in grants)
        {
            AppendDigestValue(hash, grant.ServiceId);
            AppendDigestBoolean(hash, grant.NodeGrantsNotRequired);
            var nodeIds = grant.NodeIds.Order(StringComparer.Ordinal).ToArray();
            AppendDigestInt64(hash, nodeIds.Length);
            foreach (var nodeId in nodeIds)
                AppendDigestValue(hash, nodeId);
        }
        AppendDigestValue(hash, fact.Scopes);
        AppendDigestInt64(hash, fact.ExpiresAt.ToUniversalTime().UtcTicks);
        AppendDigestBoolean(hash, fact.ServiceGrantsNotRequired);
        AppendDigestBoolean(hash, fact.Disclosure.DedicatedToSchedule);
        AppendDigestBoolean(hash, fact.Disclosure.SecretManagedByAevatar);
        AppendDigestBoolean(hash, fact.Disclosure.BrowserReceivesRawKey);
        AppendDigestBoolean(hash, fact.Disclosure.DeleteRevokesCredential);
        AppendDigestBoolean(hash, fact.Disclosure.PauseResumeRevokesCredential);
        AppendDigestInt64(hash, fact.Authority.MemberStateVersion);
        AppendDigestInt64(hash, fact.Authority.WorkflowStateVersion);
        AppendDigestInt64(hash, fact.Authority.ConnectorStateVersion);
        AppendDigestInt64(hash, fact.Authority.OwnerLlmStateVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogStateVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogObservedAt.ToUniversalTime().UtcTicks);
        AppendDigestInt64(hash, fact.Authority.CatalogFreshUntil.ToUniversalTime().UtcTicks);
        AppendDigestValue(hash, fact.Authority.CatalogContentDigest);
        AppendDigestValue(hash, fact.Authority.CatalogContractVersion);
        AppendDigestValue(hash, fact.Authority.CatalogPolicyVersion);
        AppendDigestInt64(hash, fact.Authority.CatalogEvaluatedAt.ToUniversalTime().UtcTicks);
        AppendDigestBoolean(hash, fact.OwnerLLMSelection != null);
        if (fact.OwnerLLMSelection != null)
        {
            AppendDigestInt64(hash, (long)fact.OwnerLLMSelection.RouteKind);
            AppendDigestValue(hash, fact.OwnerLLMSelection.RouteValue);
            AppendDigestValue(hash, fact.OwnerLLMSelection.NyxIdUserServiceId);
            AppendDigestValue(hash, fact.OwnerLLMSelection.ServiceSlugSnapshot);
            AppendDigestValue(hash, fact.OwnerLLMSelection.Model);
        }
    }

    private static void AppendDigestValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendDigestBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendDigestBoolean(IncrementalHash hash, bool value) =>
        hash.AppendData(value ? [1] : [0]);

    private static void AppendDigestInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void EnsureCredentialMatchesPlan(
        StudioScheduledCredential credential,
        ScheduledInvocationAuthorizationPlan plan,
        DateTimeOffset now)
    {
        if (credential.ExpiresAtUtc <= now ||
            credential.ExpiresAtUtc > plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset())
            throw new InvalidOperationException("scheduled_credential_expiry_mismatch");
        if (!string.Equals(credential.SecretReference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey, StringComparison.Ordinal))
            throw new InvalidOperationException("scheduled_credential_purpose_mismatch");
        EnsureCredentialOwnerMatches(
            new AuthenticatedAuthorizationOwnerContext(
                plan.Owner.Clone(),
                OwnerScope.NyxIdPlatform,
                string.Empty,
                plan.Owner.OwnerSubject,
                string.Empty),
            credential.Owner);
    }

    private static OwnerScope BuildOwnerScope(StudioMemberWorkflowScheduleRequest request)
    {
        var owner = request.AuthenticatedOwner;
        return string.Equals(owner.SubjectPlatform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal)
            ? OwnerScope.ForNyxIdNative(owner.Owner.OwnerSubject)
            : OwnerScope.ForChannel(
                owner.Owner.OwnerSubject,
                owner.SubjectPlatform.Trim().ToLowerInvariant(),
                NormalizeRequired(owner.SubjectTenant, nameof(owner.SubjectTenant)),
                owner.SubjectExternalUserId);
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef BuildCallerSubject(
        StudioMemberWorkflowScheduleRequest request) =>
        new(
            Platform: NormalizeOptional(request.CallerSubjectPlatform) ?? NormalizeRequired(request.AuthenticatedOwner.SubjectPlatform, nameof(request.AuthenticatedOwner.SubjectPlatform)),
            Tenant: NormalizeOptional(request.CallerSubjectTenant) ?? NormalizeOptional(request.AuthenticatedOwner.SubjectTenant) ?? string.Empty,
            ExternalUserId: NormalizeRequired(request.AuthenticatedOwner.SubjectExternalUserId, nameof(request.AuthenticatedOwner.SubjectExternalUserId)));

    private sealed record ResolvedStudioAuthorizationRequest(
        string ScopeId,
        string TeamId,
        string MemberId,
        string PublishedServiceId,
        ScheduledInvocationAuthorizationRequest AuthorizationRequest);

    private sealed record ResolvedTeamMember(
        string ScopeId,
        string TeamId,
        string MemberId,
        string PublishedServiceId);

    private enum TeamAutomationAction
    {
        Pause,
        Resume,
        RunNow,
        Delete,
    }

    private static string BuildScheduleId(string scopeId, string memberId, string idempotencyKey)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{memberId}\n{idempotencyKey}");
        var hash = SHA256.HashData(identity);
        return $"studio-member-workflow-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string BuildOperationIdentity(
        TeamAutomationOperationKind kind,
        string scheduleId,
        string permissionDigest)
    {
        var identity = Encoding.UTF8.GetBytes($"{kind}\n{scheduleId}\n{permissionDigest}");
        return $"team-automation:{Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 16))}";
    }

    private async Task<TeamAutomationOperationCommittedOutcome?> TryRecordFailureAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        CancellationToken ct)
    {
        try
        {
            var failure = await _scheduleService.FailTeamAutomationCredentialOperationAsync(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                effectAttemptId,
                errorCode,
                ct);
            return failure.Outcome;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record scheduled credential operation failure for schedule {ScheduleId} and operation {OperationId}.",
                scheduleId,
                operationId);
            // Preserve the original materialization/apply failure. The actor's
            // pending state remains visible for reconciliation.
            return null;
        }
    }

    private static string ToStableFailureCode(Exception exception) => exception switch
    {
        OperationCanceledException => "operation_cancelled",
        InvalidOperationException { Message: { Length: > 0 } message }
            when IsStableErrorCode(message) => message,
        _ => "team_automation_apply_failed",
    };

    private static bool IsStableErrorCode(string value) =>
        value.Length <= 128 && value.All(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static StudioMemberWorkflowAuthorizationResult ToAuthorizationResult(
        ScheduledInvocationAuthorizationPlanResult result) =>
        new(result.Success, result.Plan, result.FailureCode, result.Detail);

    private static bool ShouldTreatRefreshedCatalogAsProjectionPending(
        bool success,
        string? detail,
        long observedCatalogStateVersion,
        long refreshedCatalogStateVersion)
    {
        if (observedCatalogStateVersion >= refreshedCatalogStateVersion)
            return false;

        return observedCatalogStateVersion > 0 || IsRecoverableNyxIdCatalogSnapshotFailure(detail);
    }

    private static bool IsRecoverableNyxIdCatalogSnapshotFailure(string? detail)
    {
        var normalizedDetail = NormalizeOptional(detail);
        return normalizedDetail is "nyxid_catalog_snapshot_not_found" or
            "nyxid_catalog_snapshot_invalidated" or
            "nyxid_catalog_snapshot_stale";
    }

    private sealed record CatalogRefreshRecoveryResult(
        bool Success,
        ScheduledInvocationAuthorizationFailureCode FailureCode,
        string Detail,
        long RequiredStateVersion,
        long ObservedCatalogStateVersion,
        NyxIdAuthorizationCatalogRefreshResult? Refresh)
    {
        public static CatalogRefreshRecoveryResult Succeeded(NyxIdAuthorizationCatalogRefreshResult refresh) =>
            new(true, ScheduledInvocationAuthorizationFailureCode.Unspecified, string.Empty, 0, 0, refresh);

        public static CatalogRefreshRecoveryResult Failed(
            ScheduledInvocationAuthorizationFailureCode failureCode,
            string detail) =>
            new(false, failureCode, detail, 0, 0, null);

        public static CatalogRefreshRecoveryResult ProjectionPending(
            long requiredStateVersion,
            long observedCatalogStateVersion) =>
            new(
                false,
                ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending,
                "nyxid_catalog_projection_pending",
                requiredStateVersion,
                observedCatalogStateVersion,
                null);
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
