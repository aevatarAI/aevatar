using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Studio.Application.Delivery;

public enum WorkflowInstallationReadinessReconciliationStatus
{
    Pending = 1,
    Ready = 2,
    TerminalFailure = 3,
}

public sealed record WorkflowInstallationReadinessReconciliationResult(
    WorkflowInstallationReadinessReconciliationStatus Status,
    string Code,
    string Message,
    WorkflowInstallationReadinessEvidence? Evidence = null,
    WorkflowDeliveryCommandReceipt? CommandReceipt = null);

public interface IWorkflowInstallationReadinessReconciler
{
    Task<WorkflowInstallationReadinessReconciliationResult> ReconcileAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default);
}

/// <summary>
/// Produces installation readiness evidence exclusively from committed read
/// models. This service never primes a projection or reads an event store.
/// </summary>
public sealed class WorkflowInstallationReadinessReconciler : IWorkflowInstallationReadinessReconciler
{
    internal const string WorkflowEndpointId = "chat";

    private const int AutomationPageSize = 100;
    private const int MaximumAutomationPages = 10;
    private const int ServiceRunTake = 200;

    private readonly IStudioMemberService _memberService;
    private readonly IStudioMemberAutomationQueryPort _automations;
    private readonly IServiceRunQueryPort _serviceRuns;
    private readonly IWorkflowExecutionCurrentStateQueryPort _workflowCurrentStates;
    private readonly IContentArtifactQueryPort _contentArtifacts;
    private readonly IWorkflowDeliveryCommandPort _commands;
    private readonly TimeProvider _timeProvider;

    public WorkflowInstallationReadinessReconciler(
        IStudioMemberService memberService,
        IStudioMemberAutomationQueryPort automations,
        IServiceRunQueryPort serviceRuns,
        IWorkflowExecutionCurrentStateQueryPort workflowCurrentStates,
        IContentArtifactQueryPort contentArtifacts,
        IWorkflowDeliveryCommandPort commands,
        TimeProvider timeProvider)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _automations = automations ?? throw new ArgumentNullException(nameof(automations));
        _serviceRuns = serviceRuns ?? throw new ArgumentNullException(nameof(serviceRuns));
        _workflowCurrentStates = workflowCurrentStates ??
                                 throw new ArgumentNullException(nameof(workflowCurrentStates));
        _contentArtifacts = contentArtifacts ?? throw new ArgumentNullException(nameof(contentArtifacts));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WorkflowInstallationReadinessReconciliationResult> ReconcileAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var claimantId = WorkflowDeliveryContinuationClaimPolicy.NormalizeClaimantId(
            continuationClaimantId);
        var installation = delivery.Installation;
        if (installation == null)
            return Pending("installation_missing", "The delivery installation is not visible yet.");
        if (installation.Status == WorkflowInstallationStatus.Ready)
        {
            return new WorkflowInstallationReadinessReconciliationResult(
                WorkflowInstallationReadinessReconciliationStatus.Ready,
                "already_ready",
                "The installation read model is already Ready.",
                installation.ReadinessEvidence);
        }
        if (installation.Status == WorkflowInstallationStatus.Failed)
        {
            return Terminal(
                NormalizeOptional(installation.ErrorCode) ?? "installation_failed",
                NormalizeOptional(installation.ErrorMessage) ?? "The installation has failed.");
        }
        if (installation.Status != WorkflowInstallationStatus.ProvisioningAccepted)
        {
            return Pending(
                "provisioning_acceptance_pending",
                "The installation has not reached provisioning acceptance.");
        }
        var claim = installation.ContinuationClaim;
        if (!WorkflowDeliveryContinuationClaimPolicy.IsActiveFor(
                installation,
                claim,
                WorkflowInstallationStatus.ProvisioningAccepted,
                claimantId,
                _timeProvider.GetUtcNow()))
        {
            return Pending(
                "continuation_claim_pending",
                "The provisioning-accepted installation is waiting for an actor-owned continuation claim.");
        }

        var identityFailure = ValidateInstallationIdentity(delivery, installation);
        if (identityFailure != null)
            return identityFailure;

        var memberId = installation.MemberId!.Trim();
        var publishedServiceId = installation.PublishedServiceId!.Trim();
        var revisionId = installation.RevisionId!.Trim();

        var contractResult = await ResolveEndpointContractAsync(
            installation.ScopeId,
            memberId,
            publishedServiceId,
            revisionId,
            ct);
        if (contractResult.Result != null)
            return contractResult.Result;
        var contract = contractResult.Contract!;

        var triggerResult = await ResolveTriggerAsync(delivery.DeliveryId, installation, ct);
        if (triggerResult.Result != null)
            return triggerResult.Result;

        if (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.None)
        {
            var noTriggerEvidence = new WorkflowInstallationReadinessEvidence(
                new WorkflowPublishedServiceReadinessEvidence(
                    publishedServiceId,
                    Committed: true,
                    Runnable: true,
                    contract.PublishedServiceStateVersion),
                new WorkflowBoundRevisionReadinessEvidence(
                    revisionId,
                    NormalizeOptional(installation.BindingRunId) ?? string.Empty,
                    Bound: true,
                    contract.BoundRevisionStateVersion),
                triggerResult.Evidence!,
                AcceptanceRun: null,
                Artifacts: []);
            return await RecordReadyAsync(
                delivery.DeliveryId,
                installation,
                claim!,
                noTriggerEvidence,
                ct);
        }

        var runResult = await ResolveAcceptanceRunAsync(
            delivery,
            installation,
            publishedServiceId,
            revisionId,
            triggerResult.Schedule?.ScheduleId,
            ct);
        if (runResult.Result != null)
            return runResult.Result;

        var run = runResult.Run!;
        var evidence = new WorkflowInstallationReadinessEvidence(
            new WorkflowPublishedServiceReadinessEvidence(
                publishedServiceId,
                Committed: true,
                Runnable: true,
                contract.PublishedServiceStateVersion),
            new WorkflowBoundRevisionReadinessEvidence(
                revisionId,
                NormalizeOptional(installation.BindingRunId) ?? string.Empty,
                Bound: true,
                contract.BoundRevisionStateVersion),
            triggerResult.Evidence!,
            new WorkflowAcceptanceRunReadinessEvidence(
                run.RegistryRun.RunId,
                WorkflowAcceptanceRunStatus.TerminalSuccess,
                run.CommittedStateVersion),
            runResult.Artifacts!);

        return await RecordReadyAsync(
            delivery.DeliveryId,
            installation,
            claim!,
            evidence,
            ct);
    }

    private async Task<WorkflowInstallationReadinessReconciliationResult> RecordReadyAsync(
        string deliveryId,
        WorkflowInstallationSnapshot installation,
        WorkflowInstallationContinuationClaimSnapshot claim,
        WorkflowInstallationReadinessEvidence evidence,
        CancellationToken ct)
    {
        var receipt = await _commands.RecordInstallationReadyAsync(
            new RecordWorkflowInstallationReadyMutation(
                deliveryId,
                installation.InstallationId,
                evidence,
                installation.Attempt,
                installation.OperationId,
                _timeProvider.GetUtcNow(),
                claim.ClaimId,
                claim.ClaimantId),
            ct);
        return new WorkflowInstallationReadinessReconciliationResult(
            WorkflowInstallationReadinessReconciliationStatus.Ready,
            "readiness_recording_accepted",
            "All readiness invariants are proven and the Ready command was accepted for dispatch.",
            evidence,
            receipt);
    }

    private async Task<EndpointContractResolution> ResolveEndpointContractAsync(
        string scopeId,
        string memberId,
        string publishedServiceId,
        string revisionId,
        CancellationToken ct)
    {
        StudioMemberEndpointContractResponse? contract;
        try
        {
            contract = await _memberService.GetEndpointContractAsync(
                scopeId,
                memberId,
                WorkflowEndpointId,
                ct);
        }
        catch (StudioMemberNotFoundException)
        {
            return EndpointContractResolution.FromResult(Pending(
                "member_projection_pending",
                "The provisioned Studio member is not visible yet."));
        }
        catch (InvalidOperationException)
        {
            return EndpointContractResolution.FromResult(Pending(
                "published_service_projection_pending",
                "The member's published service is not visible yet."));
        }

        if (contract == null)
        {
            return EndpointContractResolution.FromResult(Pending(
                "workflow_endpoint_pending",
                "The workflow chat endpoint is not visible yet."));
        }
        if (!Same(contract.ScopeId, scopeId) ||
            !Same(contract.MemberId, memberId) ||
            !Same(contract.EndpointId, WorkflowEndpointId))
        {
            return EndpointContractResolution.FromResult(Terminal(
                "published_service_identity_mismatch",
                "The member endpoint contract does not match the installation identity."));
        }
        if (!Same(contract.PublishedServiceId, publishedServiceId))
        {
            return EndpointContractResolution.FromResult(Terminal(
                "published_service_identity_mismatch",
                "The member is bound to a different published service."));
        }
        if (!Same(contract.RevisionId, revisionId) ||
            (NormalizeOptional(contract.InvocationReadiness.RevisionId) is { } readyRevisionId &&
             !Same(readyRevisionId, revisionId)))
        {
            return EndpointContractResolution.FromResult(Pending(
                "bound_revision_pending",
                "The exact installed revision is not the runnable serving revision yet."));
        }
        if (!contract.InvocationReadiness.CanInvoke)
        {
            return EndpointContractResolution.FromResult(Pending(
                NormalizeOptional(contract.InvocationReadiness.ReasonCode) ?? "published_service_not_runnable",
                "The exact installed revision is not runnable yet."));
        }
        if (contract.PublishedServiceStateVersion <= 0 || contract.BoundRevisionStateVersion <= 0)
        {
            return EndpointContractResolution.FromResult(Pending(
                "published_service_version_pending",
                "Committed service and revision source versions are not visible yet."));
        }

        return new EndpointContractResolution(contract, null);
    }

    private async Task<TriggerResolution> ResolveTriggerAsync(
        string deliveryId,
        WorkflowInstallationSnapshot installation,
        CancellationToken ct)
    {
        if (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.None)
        {
            if (NormalizeOptional(installation.ScheduleId) != null)
            {
                return TriggerResolution.FromResult(Terminal(
                    "unexpected_schedule_identity",
                    "A no-trigger installation unexpectedly contains a schedule identity."));
            }
            return new TriggerResolution(
                new WorkflowTriggerReadinessEvidence(
                    installation.TriggerIntent,
                    new WorkflowNoTriggerReadinessEvidence(Ready: true),
                    Schedule: null),
                null,
                null);
        }

        if (installation.TriggerIntent.Kind is not (WorkflowDeliveryTriggerKind.OneShot or
            WorkflowDeliveryTriggerKind.Cron))
        {
            return TriggerResolution.FromResult(Terminal(
                "unsupported_trigger_intent",
                "The installation trigger kind is not supported for readiness."));
        }

        var scheduleResolution = await ResolveScheduleAsync(installation, ct);
        if (scheduleResolution.Result != null)
            return TriggerResolution.FromResult(scheduleResolution.Result);
        var schedule = scheduleResolution.Schedule!;

        if (NormalizeOptional(installation.ScheduleId) == null)
        {
            var receipt = await _commands.RecordProvisioningAcceptedAsync(
                new RecordWorkflowProvisioningAcceptedMutation(
                    deliveryId,
                    installation.InstallationId,
                    installation.MemberId!,
                    installation.WorkflowId,
                    installation.PublishedServiceId,
                    installation.RevisionId,
                    installation.BindingRunId,
                    schedule.ScheduleId,
                    installation.ScheduleProvisioningId,
                    installation.ScheduleProvisioningStatus,
                    installation.Attempt,
                    installation.OperationId,
                    _timeProvider.GetUtcNow(),
                    installation.ContinuationClaim!.ClaimId,
                    installation.ContinuationClaim.ClaimantId),
                ct);
            return TriggerResolution.FromResult(new WorkflowInstallationReadinessReconciliationResult(
                WorkflowInstallationReadinessReconciliationStatus.Pending,
                "schedule_identity_enrichment_accepted",
                "The authoritative schedule identity was accepted for installation enrichment.",
                CommandReceipt: receipt));
        }

        return new TriggerResolution(
            new WorkflowTriggerReadinessEvidence(
                installation.TriggerIntent,
                NoTrigger: null,
                new WorkflowScheduleReadinessEvidence(
                    schedule.ScheduleId,
                    NormalizeOptional(installation.ScheduleProvisioningId) ?? string.Empty,
                    WorkflowScheduleReadinessStatus.Ready,
                    schedule.StateVersion)),
            schedule,
            null);
    }

    private async Task<ScheduleResolution> ResolveScheduleAsync(
        WorkflowInstallationSnapshot installation,
        CancellationToken ct)
    {
        var storedScheduleId = NormalizeOptional(installation.ScheduleId);
        if (storedScheduleId != null)
        {
            var schedule = await _automations.GetAsync(
                installation.ScopeId,
                installation.TeamId,
                installation.MemberId!,
                storedScheduleId,
                ct);
            if (schedule == null)
            {
                return ScheduleResolution.FromResult(Pending(
                    "schedule_projection_pending",
                    "The provisioned schedule is not visible yet."));
            }
            var validation = ValidateSchedule(installation, schedule, storedScheduleId);
            return validation ?? new ScheduleResolution(schedule, null);
        }

        var schedules = await ListMemberAutomationsAsync(installation, ct);
        var operationMatches = schedules
            .Where(schedule => Same(schedule.OperationId, installation.OperationId))
            .ToArray();
        if (operationMatches.Length == 0)
        {
            var provisioningFailure = await ResolveScheduleProvisioningFailureAsync(installation, ct);
            if (provisioningFailure != null)
                return ScheduleResolution.FromResult(provisioningFailure);

            return ScheduleResolution.FromResult(Pending(
                "schedule_projection_pending",
                "The provisioned schedule operation is not visible yet."));
        }

        var exactMatches = operationMatches
            .Where(schedule =>
                Same(schedule.ScopeId, installation.ScopeId) &&
                Same(schedule.TeamId, installation.TeamId) &&
                Same(schedule.MemberId, installation.MemberId) &&
                Same(schedule.PublishedServiceId, installation.PublishedServiceId) &&
                Same(schedule.TargetRevisionId, installation.RevisionId))
            .ToArray();
        if (exactMatches.Length == 0)
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_target_mismatch",
                "The schedule operation targets a different service identity or revision."));
        }
        if (exactMatches.Length != 1)
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_identity_ambiguous",
                "The schedule operation resolves to more than one authoritative schedule."));
        }

        var exact = exactMatches[0];
        var exactValidation = ValidateSchedule(installation, exact, exact.ScheduleId);
        return exactValidation ?? new ScheduleResolution(exact, null);
    }

    private async Task<WorkflowInstallationReadinessReconciliationResult?>
        ResolveScheduleProvisioningFailureAsync(
            WorkflowInstallationSnapshot installation,
            CancellationToken ct)
    {
        var expectedProvisioningId = NormalizeOptional(installation.ScheduleProvisioningId);
        if (expectedProvisioningId == null)
            return null;

        StudioMemberDetailResponse member;
        try
        {
            member = await _memberService.GetAsync(
                installation.ScopeId,
                installation.MemberId!,
                ct);
        }
        catch (StudioMemberNotFoundException)
        {
            return null;
        }

        var provisioning = member.ScheduleProvisioning;
        if (provisioning == null ||
            !Same(provisioning.ProvisioningId, expectedProvisioningId) ||
            !Same(provisioning.Status, StudioWorkflowScheduleProvisioningStatusNames.Failed))
        {
            return null;
        }

        return Terminal(
            NormalizeOptional(provisioning.FailureCode) ?? "schedule_provisioning_failed",
            NormalizeOptional(provisioning.FailureMessage) ?? "Workflow schedule provisioning failed.");
    }

    private async Task<IReadOnlyList<StudioMemberAutomationView>> ListMemberAutomationsAsync(
        WorkflowInstallationSnapshot installation,
        CancellationToken ct)
    {
        var items = new List<StudioMemberAutomationView>();
        string? cursor = null;
        for (var page = 0; page < MaximumAutomationPages; page++)
        {
            var response = await _automations.ListAsync(
                installation.ScopeId,
                installation.TeamId,
                installation.MemberId,
                AutomationPageSize,
                cursor,
                includeTotalCount: false,
                ct);
            items.AddRange(response.Items);
            var nextCursor = NormalizeOptional(response.NextCursor);
            if (nextCursor == null || Same(nextCursor, cursor))
                break;
            cursor = nextCursor;
        }
        return items;
    }

    private static ScheduleResolution? ValidateSchedule(
        WorkflowInstallationSnapshot installation,
        StudioMemberAutomationView schedule,
        string expectedScheduleId)
    {
        if (!Same(schedule.ScopeId, installation.ScopeId) ||
            !Same(schedule.TeamId, installation.TeamId) ||
            !Same(schedule.MemberId, installation.MemberId) ||
            !Same(schedule.ScheduleId, expectedScheduleId) ||
            !Same(schedule.PublishedServiceId, installation.PublishedServiceId) ||
            !Same(schedule.TargetRevisionId, installation.RevisionId) ||
            !Same(schedule.OperationId, installation.OperationId))
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_target_mismatch",
                "The schedule does not match the installation operation, service, revision, and owner."));
        }

        if (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.Cron &&
            (!Same(schedule.ScheduleCron, installation.TriggerIntent.Cron) ||
             !Same(schedule.ScheduleTimezone, installation.TriggerIntent.TimeZone)))
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_trigger_mismatch",
                "The schedule cron or timezone differs from the installation trigger intent."));
        }
        if (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.OneShot &&
            NormalizeOptional(schedule.ScheduleCron) != null)
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_trigger_mismatch",
                "The one-shot installation resolved to a recurring schedule."));
        }

        var authorizationStatus = NormalizeOptional(schedule.AuthorizationStatus)?.ToLowerInvariant();
        if (authorizationStatus is "failed" or "needs_authorization" or "deleting" or "revocation_pending")
        {
            return ScheduleResolution.FromResult(Terminal(
                "schedule_terminal_failure",
                $"The schedule reached terminal status '{authorizationStatus}'."));
        }
        if (!string.Equals(authorizationStatus, "active", StringComparison.Ordinal) ||
            schedule.StateVersion <= 0 ||
            (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.Cron && !schedule.Enabled))
        {
            return ScheduleResolution.FromResult(Pending(
                "schedule_readiness_pending",
                "The exact schedule is not ready in its committed read model yet."));
        }

        return null;
    }

    private async Task<AcceptanceRunResolution> ResolveAcceptanceRunAsync(
        WorkflowDeliverySnapshot delivery,
        WorkflowInstallationSnapshot installation,
        string publishedServiceId,
        string revisionId,
        string? scheduleId,
        CancellationToken ct)
    {
        var expectedScheduleId = NormalizeOptional(scheduleId);
        var runs = await _serviceRuns.ListAsync(
            new ServiceRunQuery(
                installation.ScopeId,
                publishedServiceId,
                Take: ServiceRunTake,
                ScheduleId: expectedScheduleId,
                UpdatedFrom: installation.CreatedAtUtc),
            ct);
        var exactRuns = runs
            .Where(run =>
                Same(run.ScopeId, installation.ScopeId) &&
                Same(run.ServiceId, publishedServiceId) &&
                Same(run.RevisionId, revisionId) &&
                SameOptional(run.ScheduleId, expectedScheduleId) &&
                OperationMatches(installation, run))
            .OrderByDescending(static run => run.UpdatedAt)
            .ThenByDescending(static run => run.StateVersion)
            .ThenBy(static run => run.RunId, StringComparer.Ordinal)
            .ToArray();

        if (exactRuns.Length == 0)
        {
            if (runs.Any(run =>
                    SameOptional(run.ScheduleId, expectedScheduleId) &&
                    OperationMatches(installation, run)))
            {
                return AcceptanceRunResolution.FromResult(Terminal(
                    "acceptance_run_target_mismatch",
                    "The current installation attempt produced a run for a different service identity or revision."));
            }
            return AcceptanceRunResolution.FromResult(Pending(
                "acceptance_run_pending",
                "No run for the exact service, revision, schedule, and installation attempt is visible yet."));
        }

        var outcomes = new List<WorkflowDeliveryRunOutcome>(exactRuns.Length);
        foreach (var run in exactRuns)
        {
            outcomes.Add(await WorkflowDeliveryRunOutcomeResolver.ResolveAsync(
                run,
                _workflowCurrentStates,
                ct));
        }

        WorkflowInstallationReadinessReconciliationResult? artifactPending = null;
        foreach (var completed in outcomes.Where(static outcome =>
                     outcome.Status == WorkflowDeliveryRunOutcomeStatus.Completed))
        {
            if (completed.CommittedStateVersion <= 0)
                continue;
            var artifactResolution = await ResolveVerifiedArtifactsAsync(
                delivery,
                installation,
                completed,
                ct);
            if (artifactResolution.Result is
                { Status: WorkflowInstallationReadinessReconciliationStatus.TerminalFailure } artifactTerminal)
            {
                return AcceptanceRunResolution.FromResult(artifactTerminal);
            }
            if (artifactResolution.Artifacts is { Count: > 0 } artifacts)
                return new AcceptanceRunResolution(completed, artifacts, null);
            artifactPending ??= artifactResolution.Result;
        }

        if (outcomes.Any(static outcome => outcome.Status is
                WorkflowDeliveryRunOutcomeStatus.Pending or
                WorkflowDeliveryRunOutcomeStatus.Completed))
        {
            return AcceptanceRunResolution.FromResult(artifactPending ?? Pending(
                "acceptance_artifact_pending",
                "The acceptance run has not exposed committed terminal-success artifact evidence yet."));
        }

        var terminal = outcomes[0];
        var code = terminal.Status switch
        {
            WorkflowDeliveryRunOutcomeStatus.Failed => "acceptance_run_failed",
            WorkflowDeliveryRunOutcomeStatus.Stopped => "acceptance_run_stopped",
            WorkflowDeliveryRunOutcomeStatus.TimedOut => "acceptance_run_timed_out",
            WorkflowDeliveryRunOutcomeStatus.OutcomeUncertain => "acceptance_run_outcome_uncertain",
            _ => "acceptance_run_terminal_failure",
        };
        return AcceptanceRunResolution.FromResult(Terminal(
            code,
            NormalizeOptional(terminal.Error) ??
            $"Acceptance run '{terminal.RegistryRun.RunId}' ended with status '{terminal.Status}'."));
    }

    private async Task<ArtifactResolution> ResolveVerifiedArtifactsAsync(
        WorkflowDeliverySnapshot delivery,
        WorkflowInstallationSnapshot installation,
        WorkflowDeliveryRunOutcome outcome,
        CancellationToken ct)
    {
        var run = outcome.RegistryRun;
        WorkflowAcceptanceArtifactIdentity expected;
        ContentArtifactPrincipalContract owner;
        try
        {
            expected = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, run.RunId);
            owner = WorkflowAcceptanceArtifactContract.ResolveOwner(installation);
        }
        catch (InvalidOperationException exception)
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_owner_invalid",
                exception.Message));
        }

        var output = WorkflowAcceptanceArtifactContract.ValidateOutput(
            delivery.Package.WorkflowName,
            outcome.Output);
        if (!output.IsValid)
            return ArtifactResolution.FromResult(Terminal(output.ErrorCode!, output.ErrorMessage!));

        var expectedReferences = run.ResultArtifacts
            .Where(artifact =>
                Same(artifact.ArtifactId, expected.ArtifactId) &&
                Same(artifact.RevisionId, expected.RevisionId))
            .ToArray();
        if (expectedReferences.Length == 0)
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_pending",
                "The acceptance run has not exposed its deterministic content artifact revision yet."));
        }
        if (expectedReferences.Any(reference => !IsCompleteContentArtifactReference(reference)) ||
            expectedReferences.Select(reference =>
                    $"{reference.ContentHash.Trim().ToLowerInvariant()}\n{reference.MediaType.Trim()}")
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_reference_conflict",
                "The deterministic acceptance artifact revision is attached with conflicting facts."));
        }
        var reference = expectedReferences[0];
        if (!Same(reference.MediaType, WorkflowAcceptanceArtifactContract.MediaType) ||
            !string.Equals(reference.ContentHash, output.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_integrity_mismatch",
                "The deterministic acceptance artifact reference does not match the completed Run output."));
        }

        var artifactId = expected.ArtifactId;
        var revisionId = expected.RevisionId;
        var current = await _contentArtifacts.GetAsync(installation.ScopeId, artifactId, ct);
        if (current == null)
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_projection_pending",
                $"Content artifact '{artifactId}' is not visible in its current-state read model yet."));
        }
        if (!Same(current.ScopeId, installation.ScopeId) ||
            !Same(current.TeamId, installation.TeamId) ||
            !Same(current.ArtifactId, artifactId) ||
            !Same(current.Kind, WorkflowAcceptanceArtifactContract.Kind) ||
            !Same(current.Classification, WorkflowAcceptanceArtifactContract.Classification) ||
            !Same(current.Owner.PrincipalId, owner.PrincipalId) ||
            !Same(current.Owner.PrincipalKind, owner.PrincipalKind) ||
            !Same(current.CurrentRevisionId, revisionId))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_identity_mismatch",
                $"Content artifact '{artifactId}' does not match the deterministic acceptance artifact contract."));
        }
        if (current.StateVersion <= 0)
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_projection_pending",
                $"Content artifact '{artifactId}' has no committed source version yet."));
        }
        if (string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Tombstoned, StringComparison.Ordinal))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_tombstoned",
                $"Content artifact '{artifactId}' is tombstoned."));
        }
        if (!string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Active, StringComparison.Ordinal))
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_projection_pending",
                $"Content artifact '{artifactId}' is not active in its current-state read model yet."));
        }

        var revision = current.Revisions.SingleOrDefault(item => Same(item.RevisionId, revisionId));
        if (revision == null)
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_revision_pending",
                $"Content artifact '{artifactId}' revision '{revisionId}' is not visible yet."));
        }
        if (revision.Availability is ContentArtifactRevisionAvailabilityNames.Redacted or
            ContentArtifactRevisionAvailabilityNames.RetentionExpired)
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_revision_unavailable",
                $"Content artifact '{artifactId}' revision '{revisionId}' is {revision.Availability}."));
        }
        if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_revision_pending",
                $"Content artifact '{artifactId}' revision '{revisionId}' is not available yet."));
        }
        if (revision.RevisionNumber != 1 ||
            NormalizeOptional(revision.ParentRevisionId) != null ||
            !revision.HasInlineContent ||
            revision.HasBackingContent ||
            revision.ByteLength != output.Content!.LongLength ||
            !string.Equals(revision.ContentHash.Trim(), reference.ContentHash.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !Same(revision.MediaType, WorkflowAcceptanceArtifactContract.MediaType))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_integrity_mismatch",
                $"Content artifact '{artifactId}' revision '{revisionId}' does not match the Run output and evidence contract."));
        }
        if (!ProvenanceMatches(installation, run, revision.Provenance))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_provenance_mismatch",
                $"Content artifact '{artifactId}' revision '{revisionId}' provenance does not match the installation and Run."));
        }

        ContentArtifactRevisionContentResponse content;
        try
        {
            content = await _contentArtifacts.GetRevisionContentAsync(
                installation.ScopeId,
                artifactId,
                revisionId,
                owner,
                ct);
        }
        catch (ContentArtifactNotFoundException)
        {
            return ArtifactResolution.FromResult(Pending(
                "acceptance_artifact_content_pending",
                $"Content artifact '{artifactId}' revision '{revisionId}' content is not visible yet."));
        }
        catch (ContentArtifactContentUnavailableException exception)
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_revision_unavailable",
                exception.Message));
        }

        var storedOutput = WorkflowAcceptanceArtifactContract.ValidateContent(
            delivery.Package.WorkflowName,
            content.Content);
        if (!storedOutput.IsValid ||
            !Same(content.Reference.ArtifactId, artifactId) ||
            !Same(content.Reference.RevisionId, revisionId) ||
            !Same(content.Reference.MediaType, WorkflowAcceptanceArtifactContract.MediaType) ||
            !string.Equals(content.Reference.ContentHash, output.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(storedOutput.ContentHash, output.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !content.Content.AsSpan().SequenceEqual(output.Content))
        {
            return ArtifactResolution.FromResult(Terminal(
                "acceptance_artifact_content_mismatch",
                $"Content artifact '{artifactId}' revision '{revisionId}' content does not match the completed Run output contract."));
        }

        return new ArtifactResolution(
            [new WorkflowInstallationArtifactEvidence(
                WorkflowInstallationArtifactKind.RunOutput,
                artifactId,
                WorkflowInstallationArtifactVerificationStatus.Verified,
                $"content-artifact:{artifactId}:revision:{revisionId}",
                storedOutput.ContentHash!)],
            null);
    }

    private static bool ProvenanceMatches(
        WorkflowInstallationSnapshot installation,
        ServiceRunSnapshot run,
        ContentArtifactExecutionProvenanceContract provenance) =>
        Same(provenance.ScopeId, installation.ScopeId) &&
        Same(provenance.TeamId, installation.TeamId) &&
        Same(provenance.MemberId, installation.MemberId) &&
        Same(provenance.WorkflowId, installation.WorkflowId) &&
        Same(provenance.PublishedServiceId, installation.PublishedServiceId) &&
        Same(provenance.RunId, run.RunId);

    private static bool OperationMatches(
        WorkflowInstallationSnapshot installation,
        ServiceRunSnapshot run) =>
        installation.TriggerIntent.Kind != WorkflowDeliveryTriggerKind.None &&
        Same(run.ScheduleOperationId, installation.OperationId);

    private static bool IsCompleteContentArtifactReference(
        Aevatar.ContentArtifacts.Abstractions.ContentArtifactReference? artifact)
    {
        if (artifact == null ||
            NormalizeOptional(artifact.ArtifactId) == null ||
            NormalizeOptional(artifact.RevisionId) == null ||
            NormalizeOptional(artifact.MediaType) == null ||
            artifact.ContentHash?.Length != 64)
        {
            return false;
        }
        try
        {
            return Convert.FromHexString(artifact.ContentHash).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static WorkflowInstallationReadinessReconciliationResult? ValidateInstallationIdentity(
        WorkflowDeliverySnapshot delivery,
        WorkflowInstallationSnapshot installation)
    {
        if (!Same(delivery.DeliveryId, NormalizeOptional(delivery.DeliveryId)) ||
            !Same(delivery.TargetScopeId, installation.ScopeId))
        {
            return Terminal(
                "installation_identity_mismatch",
                "The delivery and installation identities do not agree.");
        }
        if (NormalizeOptional(installation.InstallationId) == null ||
            NormalizeOptional(installation.MemberId) == null ||
            NormalizeOptional(installation.PublishedServiceId) == null ||
            NormalizeOptional(installation.RevisionId) == null ||
            NormalizeOptional(installation.OperationId) == null)
        {
            return Pending(
                "provisioning_identity_pending",
                "Provisioning identities are not completely visible yet.");
        }
        return null;
    }

    private static WorkflowInstallationReadinessReconciliationResult Pending(
        string code,
        string message) =>
        new(WorkflowInstallationReadinessReconciliationStatus.Pending, code, message);

    private static WorkflowInstallationReadinessReconciliationResult Terminal(
        string code,
        string message) =>
        new(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure, code, Limit(message));

    private static string Limit(string value) =>
        value.Length <= 512 ? value : value[..512];

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static bool SameOptional(string? actual, string? expected) =>
        Same(NormalizeOptional(actual), NormalizeOptional(expected));

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed record EndpointContractResolution(
        StudioMemberEndpointContractResponse? Contract,
        WorkflowInstallationReadinessReconciliationResult? Result)
    {
        public static EndpointContractResolution FromResult(
            WorkflowInstallationReadinessReconciliationResult result) => new(null, result);
    }

    private sealed record TriggerResolution(
        WorkflowTriggerReadinessEvidence? Evidence,
        StudioMemberAutomationView? Schedule,
        WorkflowInstallationReadinessReconciliationResult? Result)
    {
        public static TriggerResolution FromResult(
            WorkflowInstallationReadinessReconciliationResult result) => new(null, null, result);
    }

    private sealed record ScheduleResolution(
        StudioMemberAutomationView? Schedule,
        WorkflowInstallationReadinessReconciliationResult? Result)
    {
        public static ScheduleResolution FromResult(
            WorkflowInstallationReadinessReconciliationResult result) => new(null, result);
    }

    private sealed record AcceptanceRunResolution(
        WorkflowDeliveryRunOutcome? Run,
        IReadOnlyList<WorkflowInstallationArtifactEvidence>? Artifacts,
        WorkflowInstallationReadinessReconciliationResult? Result)
    {
        public static AcceptanceRunResolution FromResult(
            WorkflowInstallationReadinessReconciliationResult result) => new(null, null, result);
    }

    private sealed record ArtifactResolution(
        IReadOnlyList<WorkflowInstallationArtifactEvidence>? Artifacts,
        WorkflowInstallationReadinessReconciliationResult? Result)
    {
        public static ArtifactResolution FromResult(
            WorkflowInstallationReadinessReconciliationResult result) => new(null, result);
    }
}
