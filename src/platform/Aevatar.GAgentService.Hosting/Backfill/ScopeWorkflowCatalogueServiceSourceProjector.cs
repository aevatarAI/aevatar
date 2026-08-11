using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Hosting.Backfill;

internal sealed class ScopeWorkflowCatalogueServiceSourceProjector
    : IProjectionArtifactMaterializer<ServiceDeploymentCatalogProjectionContext>
{
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionCatalogReader;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly ScopeWorkflowCatalogueRowMaterializer _catalogueRowMaterializer;
    private readonly IProjectionClock _clock;

    public ScopeWorkflowCatalogueServiceSourceProjector(
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionCatalogReader,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        ScopeWorkflowCatalogueRowMaterializer catalogueRowMaterializer,
        IProjectionClock clock)
    {
        _revisionCatalogReader = revisionCatalogReader ?? throw new ArgumentNullException(nameof(revisionCatalogReader));
        _catalogueWriteDispatcher = catalogueWriteDispatcher ?? throw new ArgumentNullException(nameof(catalogueWriteDispatcher));
        _catalogueRowMaterializer = catalogueRowMaterializer ?? throw new ArgumentNullException(nameof(catalogueRowMaterializer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ServiceDeploymentCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!TryGetObservedState(
                envelope,
                out var state,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            state?.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceKeys.Build(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey) ||
            string.IsNullOrWhiteSpace(state.Identity.TenantId) ||
            string.IsNullOrWhiteSpace(state.Identity.ServiceId))
        {
            return;
        }

        var revisionCatalog = await _revisionCatalogReader.GetAsync(serviceKey, ct);
        var activeDeployment = ResolveActiveDeployment(state);
        if (activeDeployment == null)
        {
            await DeleteInactiveWorkflowSourcesAsync(state.Identity, revisionCatalog, null, context.RootActorId, stateVersion, eventId, observedAt, ct);
            return;
        }

        if (!TryResolveWorkflowRevision(revisionCatalog, activeDeployment.RevisionId, out var revision, out var workflowId))
        {
            await DeleteInactiveWorkflowSourcesAsync(state.Identity, revisionCatalog, null, context.RootActorId, stateVersion, eventId, observedAt, ct);
            return;
        }

        await _catalogueWriteDispatcher.UpsertAsync(
            ToCatalogueServiceSource(context.RootActorId, state.Identity, serviceKey, workflowId, revision, activeDeployment, stateVersion, eventId, observedAt),
            ct);
        await _catalogueRowMaterializer.RefreshAsync(
            state.Identity.TenantId,
            workflowId,
            context.RootActorId,
            stateVersion,
            eventId,
            observedAt,
            ct);
        await DeleteInactiveWorkflowSourcesAsync(state.Identity, revisionCatalog, workflowId, context.RootActorId, stateVersion, eventId, observedAt, ct);
    }

    private bool TryGetObservedState(
        EventEnvelope envelope,
        out ServiceDeploymentState? state,
        out string eventId,
        out long stateVersion,
        out DateTimeOffset observedAt)
    {
        state = null;
        eventId = string.Empty;
        stateVersion = 0;
        observedAt = default;

        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceDeploymentState>(
                envelope,
                out _,
                out var stateEvent,
                out state) ||
            stateEvent == null ||
            state == null ||
            stateEvent.Version <= 0)
        {
            return false;
        }

        eventId = stateEvent.EventId ?? string.Empty;
        stateVersion = stateEvent.Version;
        observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        return true;
    }

    private static ServiceDeploymentRecord? ResolveActiveDeployment(ServiceDeploymentState state) =>
        state.Deployments.Values
            .Where(static deployment => deployment.Status == ServiceDeploymentStatus.Active)
            .OrderByDescending(static deployment => deployment.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private async Task DeleteInactiveWorkflowSourcesAsync(
        ServiceIdentity identity,
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string? activeWorkflowId,
        string actorId,
        long stateVersion,
        string eventId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        foreach (var workflowId in ResolveWorkflowIds(revisionCatalog))
        {
            if (string.Equals(workflowId, activeWorkflowId, StringComparison.Ordinal))
                continue;

            await _catalogueWriteDispatcher.DeleteAsync(
                new ProjectionDocumentDeleteMarker(
                    ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceDocumentId(identity.TenantId, workflowId),
                    actorId,
                    stateVersion,
                    eventId,
                    observedAt),
                ct);
            await _catalogueRowMaterializer.RefreshAsync(
                identity.TenantId,
                workflowId,
                actorId,
                stateVersion,
                eventId,
                observedAt,
                ct);
        }
    }

    private static IReadOnlyList<string> ResolveWorkflowIds(ServiceRevisionCatalogReadModel? revisionCatalog)
    {
        if (revisionCatalog == null)
            return [];

        return revisionCatalog.Revisions
            .Select(static revision => TryResolveWorkflowId(revision, out var workflowId) ? workflowId : string.Empty)
            .Where(static workflowId => !string.IsNullOrWhiteSpace(workflowId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryResolveWorkflowRevision(
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string revisionId,
        out ServiceRevisionEntryReadModel revision,
        out string workflowId)
    {
        revision = null!;
        workflowId = string.Empty;
        if (revisionCatalog == null || string.IsNullOrWhiteSpace(revisionId))
            return false;

        revision = revisionCatalog.Revisions.FirstOrDefault(entry =>
            string.Equals(entry.RevisionId, revisionId, StringComparison.Ordinal))!;
        return TryResolveWorkflowId(revision, out workflowId);
    }

    private static bool TryResolveWorkflowId(ServiceRevisionEntryReadModel? revision, out string workflowId)
    {
        workflowId = string.Empty;
        if (revision?.PreparedArtifact?.DeploymentPlan?.PlanSpecCase != ServiceDeploymentPlan.PlanSpecOneofCase.WorkflowPlan)
            return false;

        try
        {
            var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
                revision.PreparedArtifact,
                revision.RevisionId);
            workflowId = bindingIdentity.WorkflowId;
            return !string.IsNullOrWhiteSpace(workflowId);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ScopeWorkflowCatalogueSourceDocument ToCatalogueServiceSource(
        string actorId,
        ServiceIdentity identity,
        string serviceKey,
        string workflowId,
        ServiceRevisionEntryReadModel revision,
        ServiceDeploymentRecord deployment,
        long stateVersion,
        string eventId,
        DateTimeOffset observedAt)
    {
        var sourceUpdatedAt = deployment.UpdatedAt?.ToDateTimeOffset() ?? observedAt;
        var workflowName = revision.PreparedArtifact.DeploymentPlan.WorkflowPlan.WorkflowName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowName))
            workflowName = revision.WorkflowName;

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceDocumentId(identity.TenantId, workflowId),
            ActorId = actorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
            ScopeId = identity.TenantId,
            WorkflowId = workflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind,
            Name = string.IsNullOrWhiteSpace(workflowName) ? workflowId : workflowName,
            SourceUpdatedAtUtc = sourceUpdatedAt,
            ServiceKey = serviceKey,
            WorkflowName = string.IsNullOrWhiteSpace(workflowName) ? workflowId : workflowName,
            CommittedActorId = deployment.PrimaryActorId ?? string.Empty,
            ActiveRevisionId = deployment.RevisionId ?? string.Empty,
            DeploymentId = deployment.DeploymentId ?? string.Empty,
            DeploymentStatus = deployment.Status.ToString(),
            ServiceAppId = identity.AppId ?? string.Empty,
            ServiceNamespace = identity.Namespace ?? string.Empty,
            PublishedServiceId = identity.ServiceId ?? string.Empty,
        };
    }

}
