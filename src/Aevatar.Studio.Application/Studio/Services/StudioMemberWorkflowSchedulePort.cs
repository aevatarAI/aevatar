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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ObservatoryPath = "/workflow/observatory";
    private const string DedicatedCredentialProvisioningKind =
        "dedicated_scheduled_invocation_agent_key";

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly IScheduledInvocationAuthorizationPlanner _authorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator _authorizationRevalidator;
    private readonly INyxIdAuthorizationCatalogRefreshPort? _catalogRefreshPort;
    private readonly IStudioScheduledCredentialMaterializer _credentialMaterializer;
    private readonly StudioMemberWorkflowSchedulePolicy _schedulePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StudioMemberWorkflowSchedulePort> _logger;

    public StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        IStudioScheduledCredentialMaterializer credentialMaterializer,
        StudioMemberWorkflowSchedulePolicy schedulePolicy,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort = null,
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null)
        : this(
            memberService,
            scheduleService,
            authorizationPlanner,
            authorizationRevalidator,
            credentialMaterializer,
            schedulePolicy,
            TimeProvider.System,
            catalogRefreshPort,
            logger)
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
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null)
        : this(
            memberService,
            scheduleService,
            authorizationPlanner,
            authorizationRevalidator,
            credentialMaterializer,
            new StudioMemberWorkflowSchedulePolicy(),
            timeProvider,
            catalogRefreshPort,
            logger)
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
        ILogger<StudioMemberWorkflowSchedulePort>? logger = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _authorizationRevalidator = authorizationRevalidator
            ?? throw new ArgumentNullException(nameof(authorizationRevalidator));
        _catalogRefreshPort = catalogRefreshPort;
        _credentialMaterializer = credentialMaterializer ?? throw new ArgumentNullException(nameof(credentialMaterializer));
        _schedulePolicy = schedulePolicy ?? throw new ArgumentNullException(nameof(schedulePolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<StudioMemberWorkflowSchedulePort>.Instance;
    }

    public async Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var result = await _authorizationPlanner.PlanAsync(resolved.AuthorizationRequest, ct);
        return new StudioMemberWorkflowAuthorizationResult(
            result.Success, result.Plan, result.FailureCode, result.Detail);
    }

    private async Task<ScheduledInvocationAuthorizationValidationResult> RevalidateWithCatalogRefreshRetryAsync(
        ScheduledInvocationAuthorizationRequest authorizationRequest,
        ScheduledInvocationAuthorizationConfirmation confirmation,
        string? provisioningBearerToken,
        DateTimeOffset? fixedCredentialExpiresAtUtc,
        CancellationToken ct)
    {
        var first = await _authorizationRevalidator.RevalidateAsync(authorizationRequest, confirmation, ct);
        if (first.Success || !IsRecoverableNyxIdCatalogSnapshotFailure(first.Detail))
            return first;

        var bearerToken = NormalizeOptional(provisioningBearerToken);
        if (bearerToken is null)
        {
            return ScheduledInvocationAuthorizationValidationResult.Failed(
                first.FailureCode,
                $"nyxid_catalog_refresh_requires_bearer_token:{first.Detail}");
        }

        if (_catalogRefreshPort is null)
        {
            return ScheduledInvocationAuthorizationValidationResult.Failed(
                first.FailureCode,
                $"nyxid_catalog_refresh_unavailable:{first.Detail}");
        }

        NyxIdAuthorizationCatalogRefreshResult refresh;
        try
        {
            refresh = await _catalogRefreshPort.RefreshAsync(authorizationRequest.Owner, bearerToken, ct);
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
            if (refresh.StateVersion > 0 &&
                first.ObservedCatalogStateVersion < refresh.StateVersion)
            {
                return ScheduledInvocationAuthorizationValidationResult.ProjectionPending(
                    refresh.StateVersion,
                    first.ObservedCatalogStateVersion);
            }

            throw new StudioMemberAutomationCatalogRefreshSupersededException();
        }

        if (refresh.Status is NyxIdAuthorizationCatalogRefreshStatus.Failed or
            NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut)
        {
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();
        }

        if (!refresh.Success)
        {
            var failureCode = string.IsNullOrWhiteSpace(refresh.FailureCode)
                ? refresh.Status.ToString()
                : refresh.FailureCode.Trim();
            return ScheduledInvocationAuthorizationValidationResult.Failed(
                first.FailureCode,
                $"nyxid_catalog_refresh_failed:{failureCode}");
        }

        var retryEvaluatedAtUtc = _timeProvider.GetUtcNow();
        var retryRequest = authorizationRequest with
        {
            EvaluatedAtUtc = retryEvaluatedAtUtc,
            ExpiresAtUtc = fixedCredentialExpiresAtUtc ??
                           _schedulePolicy.ResolveCredentialExpiresAtUtc(retryEvaluatedAtUtc),
        };
        var second = await _authorizationRevalidator.RevalidateAsync(retryRequest, confirmation, ct);
        return second.ObservedCatalogStateVersion < refresh.StateVersion
            ? ScheduledInvocationAuthorizationValidationResult.ProjectionPending(
                refresh.StateVersion,
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
        string memberId,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var resolved = await ResolveTeamMemberAsync(scopeId, teamId, memberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId);
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
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId);
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
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId);
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
            command.ProvisioningBearerToken,
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
                authorizationFact: null,
                NormalizeRequired(command.ScheduleCron, nameof(command.ScheduleCron)),
                NormalizeRequired(command.ScheduleTimezone, nameof(command.ScheduleTimezone)),
                command.Enabled,
                owner),
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
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var resolved = await ResolveTeamMemberAsync(command.ScopeId, command.TeamId, command.MemberId, ct);
        var scheduleOwner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId);
        var authenticatedOwner = command.AuthenticatedOwner ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_required");
        var bearerToken = NormalizeRequired(
            command.ProvisioningBearerToken,
            nameof(command.ProvisioningBearerToken));
        var operationId = NormalizeRequired(command.OperationId, nameof(command.OperationId));
        var retry = await _scheduleService.RetryTeamAutomationRevocationAsync(
            NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId)),
            scheduleOwner,
            operationId,
            NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey)),
            ToAuthorizationOwner(authenticatedOwner),
            ct);
        _ = await ExecutePendingRevocationAsync(
            retry.Outcome,
            bearerToken,
            authenticatedOwner,
            scheduleOwner,
            CancellationToken.None);
        return ToMutationReceipt(retry.Admission, operationId, "pending");
    }

    private async Task<StudioMemberAutomationMutationReceipt> ApplyActionAsync(
        StudioMemberAutomationActionCommand command,
        TeamAutomationAction action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var resolved = await ResolveTeamMemberAsync(command.ScopeId, command.TeamId, command.MemberId, ct);
        var owner = new TeamMemberAutomationOwner(resolved.ScopeId, resolved.MemberId);
        var scheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var operationId = NormalizeRequired(command.OperationId, nameof(command.OperationId));
        var idempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey));
        if (action == TeamAutomationAction.RunNow)
        {
            var run = await _scheduleService.RunTeamAutomationNowAsync(
                scheduleId,
                owner,
                operationId,
                idempotencyKey,
                ct);
            return new StudioMemberAutomationMutationReceipt(
                run.Accepted,
                "accepted",
                run.ScheduleId,
                operationId,
                run.CommandId);
        }

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
                "studio_team_automation_delete",
                ToAuthorizationOwner(authenticatedOwner),
                ct);
            await ExecutePendingRevocationAsync(
                committed.Outcome,
                bearerToken,
                authenticatedOwner,
                owner,
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
            request.ProvisioningBearerToken,
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
        var scheduleCron = NormalizeRequired(request.ScheduleCron, nameof(request.ScheduleCron));
        var scheduleTimezone = NormalizeRequired(request.ScheduleTimezone, nameof(request.ScheduleTimezone));
        var publishedServiceId = resolved.PublishedServiceId;
        var teamOwner = new TeamMemberAutomationOwner(scopeId, memberId);
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
        var mutationDigest = BuildTeamAutomationMutationDigest(
            scheduleId,
            displayName,
            scopeId,
            memberId,
            publishedServiceId,
            prompt,
            scheduleCron,
            scheduleTimezone,
            request.Enabled);
        var ownerScope = BuildOwnerScope(request);
        var bearerToken = NormalizeRequired(request.ProvisioningBearerToken, nameof(request.ProvisioningBearerToken));
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
                operationId,
                idempotencyKey,
                ToAuthorizationOwner(request.AuthenticatedOwner),
                ct);
            _ = await ExecutePendingRevocationAsync(
                retry.Outcome,
                bearerToken,
                request.AuthenticatedOwner,
                teamOwner,
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
                BuildScheduleAuth(credential),
                ToScheduleAuthorizationFact(plan),
                scheduleCron,
                scheduleTimezone,
                request.Enabled,
                teamOwner);

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
            throw new StudioMemberAutomationNotFoundException();
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
        EnsureWorkflowBindingCanBeScheduled(member, memberId, publishedServiceId);
        var workflowRevision = member.LastBinding?.RevisionId ?? member.Summary.LastBoundRevisionId ?? string.Empty;
        var workflowId = NormalizeRequired(member.ImplementationRef?.WorkflowId, "workflowId");
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
                evaluatedAtUtc));
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
                mutationContext.TeamAutomationOwner!),
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
        TeamMemberAutomationOwner teamOwner) =>
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
                    Payload: Any.Pack(new ChatRequestEvent
                    {
                        Prompt = prompt,
                        ScopeId = scopeId,
                    }),
                    Auth: auth,
                    AuthorizationFact: authorizationFact)),
            CronExpression: cronExpression,
            Timezone: timezone,
            Enabled: enabled,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow)
        {
            TeamAutomationOwner = teamOwner,
        };

    internal static ScheduledInvocationAuthorizationFact ToScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var policy = plan.CredentialPolicy
            ?? throw new InvalidOperationException("scheduled_authorization_policy_missing");
        var catalog = plan.CatalogAuthority
            ?? throw new InvalidOperationException("scheduled_authorization_catalog_authority_missing");
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
                catalog.ActorStateVersion,
                catalog.ObservedAt.ToDateTimeOffset(),
                catalog.FreshUntil.ToDateTimeOffset(),
                catalog.ContentDigest,
                catalog.ContractVersion,
                catalog.PolicyVersion,
                catalog.EvaluatedAt.ToDateTimeOffset()));
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

    private static ScheduledServiceInvocationAuth BuildScheduleAuth(StudioScheduledCredential credential) =>
        new(BuildScheduleCredential(credential));

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

        _ = await _scheduleService.CompleteTeamAutomationRevocationAsync(
            outcome.ScheduleId,
            scheduleOwner,
            outcome.OperationId,
            outcome.IdempotencyKey,
            NormalizeRequired(outcome.EffectAttemptId, nameof(outcome.EffectAttemptId)),
            result.NyxIdRevoked,
            result.VaultRevoked,
            result.ErrorCode,
            ct);
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

    private static string BuildTeamAutomationMutationDigest(
        string scheduleId,
        string displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        string cronExpression,
        string timezone,
        bool enabled)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestValue(hash, "aevatar.team-automation-mutation.v1");
        AppendDigestValue(hash, scheduleId);
        AppendDigestValue(hash, displayName);
        AppendDigestValue(hash, scopeId);
        AppendDigestValue(hash, memberId);
        AppendDigestValue(hash, scopeId);
        AppendDigestValue(hash, ScopeServiceIdentityDefaults.ServiceAppId);
        AppendDigestValue(hash, ScopeServiceIdentityDefaults.ServiceNamespace);
        AppendDigestValue(hash, publishedServiceId);
        AppendDigestValue(hash, WorkflowInvokeEndpointId);
        AppendDigestValue(hash, prompt);
        AppendDigestValue(hash, cronExpression);
        AppendDigestValue(hash, timezone);
        hash.AppendData(enabled ? [1] : [0]);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendDigestValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
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

    private static bool IsRecoverableNyxIdCatalogSnapshotFailure(string? detail)
    {
        var normalizedDetail = NormalizeOptional(detail);
        return normalizedDetail is "nyxid_catalog_snapshot_not_found" or
            "nyxid_catalog_snapshot_invalidated" or
            "nyxid_catalog_snapshot_stale";
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
