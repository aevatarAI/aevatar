using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Projection.Mapping;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="StudioMemberState"/> committed events into
/// <see cref="StudioMemberCurrentStateDocument"/>. Surfaces a fully-typed
/// projection of the authority — wire-stable string enums, denormalized
/// implementation_ref, denormalized last_binding — so the query port never
/// has to <see cref="Any.Unpack"/> the actor's internal state.
/// </summary>
public sealed class StudioMemberCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _catalogueDocumentReader;
    private readonly IProjectionClock _clock;

    public StudioMemberCurrentStateProjector(
        IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> writeDispatcher,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> catalogueDocumentReader,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _catalogueWriteDispatcher = catalogueWriteDispatcher ?? throw new ArgumentNullException(nameof(catalogueWriteDispatcher));
        _catalogueDocumentReader = catalogueDocumentReader ?? throw new ArgumentNullException(nameof(catalogueDocumentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioMemberState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        if (state.Deleted)
        {
            var deleteMarker = new ProjectionDocumentDeleteMarker(
                context.RootActorId,
                context.RootActorId,
                stateEvent.Version,
                stateEvent.EventId ?? string.Empty,
                updatedAt);
            await _writeDispatcher.DeleteAsync(deleteMarker, ct);
            await DeleteCatalogueCommittedSourcesAsync(context.RootActorId, stateEvent.Version, stateEvent.EventId ?? string.Empty, updatedAt, ct);
            return;
        }

        var document = new StudioMemberCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            MemberId = state.MemberId,
            ScopeId = state.ScopeId,
            DisplayName = state.DisplayName,
            Description = state.Description,
            ImplementationKind = MemberImplementationKindMapper.ToWireName(state.ImplementationKind),
            LifecycleStage = MemberImplementationKindMapper.ToWireName(state.LifecycleStage),
            PublishedServiceId = state.PublishedServiceId,
            CreatedAt = state.CreatedAtUtc,
        };

        ApplyImplementationRef(document, state.ImplementationRef);
        ApplyLastBinding(document, state.LastBinding);
        ApplyBindingStatus(document, state.Binding);
        ApplyScheduleProvisioningStatus(document, state.WorkflowScheduleProvisioning);

        // Team membership (ADR-0017). Mirror the actor's optional team_id
        // into the document — absence means "unassigned" on both the actor
        // and the read model side.
        if (state.HasTeamId)
        {
            document.TeamId = state.TeamId;
        }

        await _writeDispatcher.UpsertAsync(document, ct);
        await MaterializeCatalogueCommittedSourceAsync(context.RootActorId, state, stateEvent.Version, stateEvent.EventId ?? string.Empty, updatedAt, ct);
    }

    private async Task MaterializeCatalogueCommittedSourceAsync(
        string actorId,
        StudioMemberState state,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var workflowId = state.ImplementationRef?.Workflow?.WorkflowId?.Trim() ?? string.Empty;
        if (workflowId.Length == 0)
        {
            await DeleteCatalogueCommittedSourcesAsync(actorId, stateVersion, eventId, updatedAt, ct);
            return;
        }

        var currentId = BuildCatalogueSourceDocumentId(
            state.ScopeId,
            workflowId,
            ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind,
            state.MemberId);
        var existingSources = await QueryCatalogueCommittedSourcesByActorAsync(actorId, ct);
        foreach (var existing in existingSources)
        {
            if (string.Equals(existing.Id, currentId, StringComparison.Ordinal))
                continue;

            await DeleteCatalogueSourceAsync(existing.Id, actorId, stateVersion, eventId, updatedAt, ct);
        }

        await _catalogueWriteDispatcher.UpsertAsync(
            ToCatalogueCommittedSource(currentId, actorId, state, workflowId, stateVersion, eventId, updatedAt),
            ct);
    }

    private async Task DeleteCatalogueCommittedSourcesAsync(
        string actorId,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var existingSources = await QueryCatalogueCommittedSourcesByActorAsync(actorId, ct);
        foreach (var existing in existingSources)
            await DeleteCatalogueSourceAsync(existing.Id, actorId, stateVersion, eventId, updatedAt, ct);
    }

    private async Task<IReadOnlyList<ScopeWorkflowCatalogueSourceDocument>> QueryCatalogueCommittedSourcesByActorAsync(
        string actorId,
        CancellationToken ct)
    {
        var result = await _catalogueDocumentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 10_000,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.ActorId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(actorId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.SourceKind),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind),
                    },
                ],
            },
            ct);
        return result.Items;
    }

    private async Task DeleteCatalogueSourceAsync(
        string documentId,
        string actorId,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct) =>
        await _catalogueWriteDispatcher.DeleteAsync(
            new ProjectionDocumentDeleteMarker(documentId, actorId, stateVersion, eventId, updatedAt),
            ct);

    private static ScopeWorkflowCatalogueSourceDocument ToCatalogueCommittedSource(
        string documentId,
        string actorId,
        StudioMemberState state,
        string workflowId,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt)
    {
        var publishedServiceId = !string.IsNullOrWhiteSpace(state.LastBinding?.PublishedServiceId)
            ? state.LastBinding.PublishedServiceId.Trim()
            : state.PublishedServiceId?.Trim() ?? string.Empty;
        var revisionId = !string.IsNullOrWhiteSpace(state.LastBinding?.RevisionId)
            ? state.LastBinding.RevisionId.Trim()
            : state.ImplementationRef?.Workflow?.WorkflowRevision?.Trim() ?? string.Empty;

        var document = new ScopeWorkflowCatalogueSourceDocument
        {
            Id = documentId,
            ActorId = actorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            ScopeId = state.ScopeId,
            WorkflowId = workflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind,
            Name = state.DisplayName,
            Description = state.Description,
            SourceUpdatedAtUtc = updatedAt,
            WorkflowName = state.DisplayName,
            CommittedActorId = state.LastBinding?.ExpectedActorId ?? string.Empty,
            ActiveRevisionId = revisionId,
            PublishedServiceId = publishedServiceId,
            MemberId = state.MemberId,
            LastBoundRevisionId = revisionId,
        };
        if (!string.IsNullOrWhiteSpace(publishedServiceId))
            document.ServiceKey = publishedServiceId;
        if (state.HasTeamId)
            document.TeamId = state.TeamId;
        return document;
    }

    private static string BuildCatalogueSourceDocumentId(
        string scopeId,
        string workflowId,
        string sourceKind,
        string memberId) =>
        $"{scopeId}:{workflowId}:{sourceKind}:{memberId}";

    private static void ApplyImplementationRef(
        StudioMemberCurrentStateDocument document,
        StudioMemberImplementationRef? implementationRef)
    {
        if (implementationRef == null)
            return;

        if (implementationRef.Workflow != null)
        {
            document.ImplementationWorkflowId = implementationRef.Workflow.WorkflowId ?? string.Empty;
            document.ImplementationWorkflowRevision = implementationRef.Workflow.WorkflowRevision ?? string.Empty;
        }

        if (implementationRef.Script != null)
        {
            document.ImplementationScriptId = implementationRef.Script.ScriptId ?? string.Empty;
            document.ImplementationScriptRevision = implementationRef.Script.ScriptRevision ?? string.Empty;
        }

        if (implementationRef.Gagent != null)
        {
            document.ImplementationActorTypeName = implementationRef.Gagent.ActorTypeName ?? string.Empty;
        }
    }

    private static void ApplyLastBinding(
        StudioMemberCurrentStateDocument document,
        StudioMemberBindingContract? lastBinding)
    {
        if (lastBinding == null || string.IsNullOrEmpty(lastBinding.PublishedServiceId))
            return;

        document.LastBoundPublishedServiceId = lastBinding.PublishedServiceId;
        document.LastBoundRevisionId = lastBinding.RevisionId ?? string.Empty;
        document.LastBoundImplementationKind = MemberImplementationKindMapper.ToWireName(
            lastBinding.ImplementationKind);
        document.LastBoundExpectedActorId = lastBinding.ExpectedActorId ?? string.Empty;
        if (lastBinding.BoundAtUtc != null)
            document.LastBoundAt = lastBinding.BoundAtUtc;
    }

    private static void ApplyBindingStatus(
        StudioMemberCurrentStateDocument document,
        StudioMemberBindingAuthorityState? binding)
    {
        if (binding == null)
            return;

        document.BindingCurrentRunId = binding.CurrentBindingRunId ?? string.Empty;
        document.BindingCurrentStatus = MemberImplementationKindMapper.ToWireName(binding.CurrentStatus);
        document.BindingLastTerminalRunId = binding.LastTerminalBindingRunId ?? string.Empty;
        document.BindingUpdatedAt = binding.UpdatedAtUtc;

        if (binding.LastFailure != null)
        {
            document.BindingFailureCode = binding.LastFailure.Code ?? string.Empty;
            document.BindingFailureMessage = binding.LastFailure.Message ?? string.Empty;
            document.BindingFailureAt = binding.LastFailure.FailedAtUtc;
        }
    }

    private static void ApplyScheduleProvisioningStatus(
        StudioMemberCurrentStateDocument document,
        StudioMemberWorkflowScheduleProvisioningState? provisioning)
    {
        if (provisioning?.Intent == null)
            return;

        document.ScheduleProvisioningId = provisioning.Intent.ProvisioningId;
        document.ScheduleProvisioningStatus = provisioning.Status switch
        {
            StudioMemberWorkflowScheduleProvisioningStatus.PendingBinding =>
                StudioWorkflowScheduleProvisioningStatusNames.PendingBinding,
            StudioMemberWorkflowScheduleProvisioningStatus.Provisioning =>
                StudioWorkflowScheduleProvisioningStatusNames.Provisioning,
            StudioMemberWorkflowScheduleProvisioningStatus.RetryPending =>
                StudioWorkflowScheduleProvisioningStatusNames.RetryPending,
            StudioMemberWorkflowScheduleProvisioningStatus.Succeeded =>
                StudioWorkflowScheduleProvisioningStatusNames.Succeeded,
            StudioMemberWorkflowScheduleProvisioningStatus.Failed =>
                StudioWorkflowScheduleProvisioningStatusNames.Failed,
            _ => string.Empty,
        };
        document.ScheduleProvisioningRevisionId = provisioning.Intent.RevisionId;
        document.ScheduleProvisioningScheduleId = provisioning.ScheduleId;
        document.ScheduleProvisioningOperationId = provisioning.OperationId;
        document.ScheduleProvisioningAttemptCount = provisioning.AttemptCount;
        document.ScheduleProvisioningUpdatedAt = provisioning.UpdatedAtUtc;
        if (provisioning.Failure != null)
        {
            document.ScheduleProvisioningFailureCode = provisioning.Failure.Code;
            document.ScheduleProvisioningFailureMessage = provisioning.Failure.Message;
        }
    }
}
