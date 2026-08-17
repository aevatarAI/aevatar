using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Studio.Application.Delivery;

public enum WorkflowAcceptanceArtifactMaterializationStatus
{
    Pending = 1,
    Satisfied = 2,
    TerminalFailure = 3,
}

public sealed record WorkflowAcceptanceArtifactMaterializationResult(
    WorkflowAcceptanceArtifactMaterializationStatus Status,
    string Code,
    string Message);

public interface IWorkflowAcceptanceArtifactMaterializer
{
    Task<WorkflowAcceptanceArtifactMaterializationResult> MaterializeAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default);
}

/// <summary>
/// Advances acceptance output persistence from committed read models. Creation,
/// artifact projection, Run attachment, and Run projection remain separate scans.
/// </summary>
public sealed class WorkflowAcceptanceArtifactMaterializer : IWorkflowAcceptanceArtifactMaterializer
{
    private const int ServiceRunTake = 200;

    private readonly IServiceRunQueryPort _serviceRuns;
    private readonly IWorkflowExecutionCurrentStateQueryPort _workflowCurrentStates;
    private readonly IContentArtifactQueryPort _contentArtifacts;
    private readonly IContentArtifactService _artifactService;
    private readonly TimeProvider _timeProvider;

    public WorkflowAcceptanceArtifactMaterializer(
        IServiceRunQueryPort serviceRuns,
        IWorkflowExecutionCurrentStateQueryPort workflowCurrentStates,
        IContentArtifactQueryPort contentArtifacts,
        IContentArtifactService artifactService,
        TimeProvider timeProvider)
    {
        _serviceRuns = serviceRuns ?? throw new ArgumentNullException(nameof(serviceRuns));
        _workflowCurrentStates = workflowCurrentStates ??
                                 throw new ArgumentNullException(nameof(workflowCurrentStates));
        _contentArtifacts = contentArtifacts ?? throw new ArgumentNullException(nameof(contentArtifacts));
        _artifactService = artifactService ?? throw new ArgumentNullException(nameof(artifactService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WorkflowAcceptanceArtifactMaterializationResult> MaterializeAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var claimantId = WorkflowDeliveryContinuationClaimPolicy.NormalizeClaimantId(
            continuationClaimantId);
        var installation = delivery.Installation;
        if (installation == null || installation.Status != WorkflowInstallationStatus.ProvisioningAccepted)
            return Satisfied("materialization_not_applicable", "Acceptance artifact materialization is not applicable.");
        if (!WorkflowDeliveryContinuationClaimPolicy.IsActiveFor(
                installation,
                installation.ContinuationClaim,
                WorkflowInstallationStatus.ProvisioningAccepted,
                claimantId,
                _timeProvider.GetUtcNow()))
        {
            return Pending(
                "continuation_claim_pending",
                "Acceptance artifact materialization is waiting for its actor-owned continuation claim.");
        }

        if (NormalizeOptional(installation.PublishedServiceId) is not { } serviceId ||
            NormalizeOptional(installation.RevisionId) is not { } revisionId ||
            NormalizeOptional(installation.MemberId) is null ||
            NormalizeOptional(installation.WorkflowId) is null)
        {
            return Pending("acceptance_run_identity_pending", "The provisioned service identities are not visible yet.");
        }

        var expectedScheduleId = installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.None
            ? null
            : NormalizeOptional(installation.ScheduleId);
        if (installation.TriggerIntent.Kind != WorkflowDeliveryTriggerKind.None && expectedScheduleId == null)
            return Pending("acceptance_schedule_identity_pending", "The acceptance schedule identity is not visible yet.");

        var runs = await _serviceRuns.ListAsync(
            new ServiceRunQuery(
                installation.ScopeId,
                serviceId,
                Take: ServiceRunTake,
                ScheduleId: expectedScheduleId,
                UpdatedFrom: installation.CreatedAtUtc),
            ct);
        var exactRuns = runs
            .Where(run =>
                Same(run.ScopeId, installation.ScopeId) &&
                Same(run.ServiceId, serviceId) &&
                Same(run.RevisionId, revisionId) &&
                SameOptional(run.ScheduleId, expectedScheduleId) &&
                OperationMatches(installation, run))
            .OrderByDescending(static run => run.UpdatedAt)
            .ThenByDescending(static run => run.StateVersion)
            .ThenBy(static run => run.RunId, StringComparer.Ordinal)
            .ToArray();
        if (exactRuns.Length == 0)
            return Pending("acceptance_run_pending", "No run for the current installation attempt is visible yet.");

        WorkflowDeliveryRunOutcome? outcome = null;
        foreach (var candidate in exactRuns)
        {
            var resolved = await WorkflowDeliveryRunOutcomeResolver.ResolveAsync(
                candidate,
                _workflowCurrentStates,
                ct);
            if (resolved.Status != WorkflowDeliveryRunOutcomeStatus.Completed)
                continue;
            outcome = resolved;
            break;
        }
        if (outcome == null)
            return Satisfied("acceptance_run_not_completed", "The current installation attempt has no completed run to materialize.");
        var run = outcome.RegistryRun;

        WorkflowAcceptanceArtifactIdentity identity;
        ContentArtifactPrincipalContract owner;
        try
        {
            identity = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, run.RunId);
            owner = WorkflowAcceptanceArtifactContract.ResolveOwner(installation);
        }
        catch (InvalidOperationException exception)
        {
            return Terminal("acceptance_artifact_owner_invalid", exception.Message);
        }

        var matchingReference = run.ResultArtifacts.FirstOrDefault(reference =>
            Same(reference.ArtifactId, identity.ArtifactId) &&
            Same(reference.RevisionId, identity.RevisionId));
        if (matchingReference != null)
        {
            if (!IsCompleteExpectedReference(matchingReference))
            {
                return Terminal(
                    "acceptance_artifact_reference_conflict",
                    "The deterministic acceptance artifact revision is already attached with incomplete facts.");
            }
            return Satisfied("acceptance_artifact_attached", "The deterministic acceptance artifact revision is attached.");
        }

        var output = WorkflowAcceptanceArtifactContract.ValidateOutput(
            delivery.Package.WorkflowName,
            outcome.Output);
        if (!output.IsValid)
            return Terminal(output.ErrorCode!, output.ErrorMessage!);

        var current = await _contentArtifacts.GetByDedupKeyAsync(
            installation.ScopeId,
            identity.DedupKey,
            ct);
        if (current == null)
        {
            await _artifactService.CreateAsync(
                installation.ScopeId,
                BuildCreateRequest(delivery, run, identity, output),
                owner,
                ct);
            return Pending(
                "acceptance_artifact_creation_accepted",
                "The deterministic acceptance artifact create command was accepted for dispatch.");
        }

        var validation = await ValidateCurrentArtifactAsync(
            delivery,
            run,
            identity,
            owner,
            output,
            current,
            ct);
        if (validation != null)
            return validation;

        try
        {
            await _artifactService.AttachToRunAsync(
                installation.ScopeId,
                new AttachContentArtifactsToRunRequest(
                    serviceId,
                    run.RunId,
                    run.StateVersion,
                    [new ContentArtifactReferenceContract(
                        identity.ArtifactId,
                        identity.RevisionId,
                        output.ContentHash!,
                        WorkflowAcceptanceArtifactContract.MediaType)]),
                owner,
                ct);
        }
        catch (InvalidOperationException)
        {
            return Pending(
                "acceptance_run_projection_changed",
                "The acceptance Run changed before artifact attachment; the next scan will reconcile it.");
        }
        return Pending(
            "acceptance_artifact_attachment_accepted",
            "The deterministic acceptance artifact attachment was accepted for dispatch.");
    }

    private async Task<WorkflowAcceptanceArtifactMaterializationResult?> ValidateCurrentArtifactAsync(
        WorkflowDeliverySnapshot delivery,
        ServiceRunSnapshot run,
        WorkflowAcceptanceArtifactIdentity identity,
        ContentArtifactPrincipalContract owner,
        WorkflowAcceptanceOutputValidation output,
        ContentArtifactCurrentStateResponse current,
        CancellationToken ct)
    {
        var installation = delivery.Installation!;
        if (!Same(current.ScopeId, installation.ScopeId) ||
            !Same(current.TeamId, installation.TeamId) ||
            !Same(current.ArtifactId, identity.ArtifactId) ||
            !Same(current.Kind, WorkflowAcceptanceArtifactContract.Kind) ||
            !Same(current.Classification, WorkflowAcceptanceArtifactContract.Classification) ||
            !Same(current.Owner.PrincipalId, owner.PrincipalId) ||
            !Same(current.Owner.PrincipalKind, owner.PrincipalKind) ||
            !Same(current.CurrentRevisionId, identity.RevisionId))
        {
            return Terminal(
                "acceptance_artifact_identity_conflict",
                "The deterministic acceptance artifact identity is occupied by different facts.");
        }
        if (current.StateVersion <= 0 ||
            !string.Equals(current.LifecycleStatus, ContentArtifactLifecycleStatusNames.Active, StringComparison.Ordinal))
        {
            return string.Equals(
                    current.LifecycleStatus,
                    ContentArtifactLifecycleStatusNames.Tombstoned,
                    StringComparison.Ordinal)
                ? Terminal("acceptance_artifact_tombstoned", "The deterministic acceptance artifact is tombstoned.")
                : Pending("acceptance_artifact_projection_pending", "The deterministic acceptance artifact is not committed and active yet.");
        }

        var revision = current.Revisions.SingleOrDefault(item => Same(item.RevisionId, identity.RevisionId));
        if (revision == null)
            return Pending("acceptance_artifact_revision_pending", "The deterministic acceptance artifact revision is not visible yet.");
        if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
        {
            return revision.Availability is ContentArtifactRevisionAvailabilityNames.Redacted or
                ContentArtifactRevisionAvailabilityNames.RetentionExpired
                ? Terminal("acceptance_artifact_revision_unavailable", "The deterministic acceptance artifact revision is unavailable.")
                : Pending("acceptance_artifact_revision_pending", "The deterministic acceptance artifact revision is not available yet.");
        }
        if (revision.RevisionNumber != 1 ||
            NormalizeOptional(revision.ParentRevisionId) != null ||
            !Same(revision.MediaType, WorkflowAcceptanceArtifactContract.MediaType) ||
            !string.Equals(revision.ContentHash, output.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            revision.ByteLength != output.Content!.LongLength ||
            !revision.HasInlineContent ||
            revision.HasBackingContent)
        {
            return Terminal(
                "acceptance_artifact_content_conflict",
                "The deterministic acceptance artifact revision does not match the completed Run output.");
        }
        if (!ProvenanceMatches(installation, run, revision.Provenance))
        {
            return Terminal(
                "acceptance_artifact_provenance_mismatch",
                "The deterministic acceptance artifact revision provenance does not match the installation and Run.");
        }

        ContentArtifactRevisionContentResponse content;
        try
        {
            content = await _contentArtifacts.GetRevisionContentAsync(
                installation.ScopeId,
                identity.ArtifactId,
                identity.RevisionId,
                owner,
                ct);
        }
        catch (ContentArtifactNotFoundException)
        {
            return Pending("acceptance_artifact_content_pending", "The deterministic acceptance artifact content is not visible yet.");
        }
        catch (ContentArtifactContentUnavailableException exception)
        {
            return Terminal("acceptance_artifact_revision_unavailable", exception.Message);
        }

        var storedOutput = WorkflowAcceptanceArtifactContract.ValidateContent(
            delivery.Package.WorkflowName,
            content.Content);
        if (!storedOutput.IsValid ||
            !string.Equals(storedOutput.ContentHash, output.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !content.Content.AsSpan().SequenceEqual(output.Content))
        {
            return Terminal(
                "acceptance_artifact_content_conflict",
                "The committed acceptance artifact content does not match the completed Run output contract.");
        }
        return null;
    }

    private static CreateContentArtifactRequest BuildCreateRequest(
        WorkflowDeliverySnapshot delivery,
        ServiceRunSnapshot run,
        WorkflowAcceptanceArtifactIdentity identity,
        WorkflowAcceptanceOutputValidation output)
    {
        var installation = delivery.Installation!;
        return new CreateContentArtifactRequest(
            installation.TeamId,
            WorkflowAcceptanceArtifactContract.Kind,
            $"{delivery.Package.DisplayName} acceptance output",
            WorkflowAcceptanceArtifactContract.Classification,
            identity.DedupKey,
            new ContentArtifactRevisionWriteRequest(
                $"{identity.DedupKey}/revision/1",
                WorkflowAcceptanceArtifactContract.MediaType,
                output.ContentHash!,
                output.Content!.LongLength,
                new ContentArtifactExecutionProvenanceContract(
                    installation.ScopeId,
                    installation.TeamId,
                    installation.MemberId,
                    installation.WorkflowId,
                    installation.PublishedServiceId,
                    run.RunId),
                InlineContent: output.Content));
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

    private static bool IsCompleteExpectedReference(ContentArtifactReference reference) =>
        Same(reference.MediaType, WorkflowAcceptanceArtifactContract.MediaType) &&
        reference.ContentHash?.Length == 64 &&
        IsSha256(reference.ContentHash);

    private static bool IsSha256(string value)
    {
        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool OperationMatches(
        WorkflowInstallationSnapshot installation,
        ServiceRunSnapshot run) =>
        installation.TriggerIntent.Kind != WorkflowDeliveryTriggerKind.None &&
        Same(run.ScheduleOperationId, installation.OperationId);

    private static WorkflowAcceptanceArtifactMaterializationResult Pending(string code, string message) =>
        new(WorkflowAcceptanceArtifactMaterializationStatus.Pending, code, message);

    private static WorkflowAcceptanceArtifactMaterializationResult Satisfied(string code, string message) =>
        new(WorkflowAcceptanceArtifactMaterializationStatus.Satisfied, code, message);

    private static WorkflowAcceptanceArtifactMaterializationResult Terminal(string code, string message) =>
        new(WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure, code, Limit(message));

    private static string Limit(string value) => value.Length <= 512 ? value : value[..512];

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static bool SameOptional(string? actual, string? expected) =>
        Same(NormalizeOptional(actual), NormalizeOptional(expected));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
