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
    private readonly IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> _deploymentCatalogReader;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly ScopeWorkflowCatalogueRowMaterializer _catalogueRowMaterializer;
    private readonly IProjectionClock _clock;

    public ScopeWorkflowCatalogueServiceSourceProjector(
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionCatalogReader,
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> deploymentCatalogReader,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        ScopeWorkflowCatalogueRowMaterializer catalogueRowMaterializer,
        IProjectionClock clock)
    {
        _revisionCatalogReader = revisionCatalogReader ?? throw new ArgumentNullException(nameof(revisionCatalogReader));
        _deploymentCatalogReader = deploymentCatalogReader ?? throw new ArgumentNullException(nameof(deploymentCatalogReader));
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
        var activeDeployment = ResolveActiveDeployment(state.Deployments.Values);
        await MaterializeAsync(state.Identity, serviceKey, revisionCatalog, activeDeployment, eventId, observedAt, ct);
    }

    public async ValueTask ProjectRevisionCatalogAsync(
        ServiceRevisionCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceRevisionCatalogState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            state.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceKeys.Build(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var deploymentCatalog = await _deploymentCatalogReader.GetAsync(serviceKey, ct);
        if (deploymentCatalog == null)
            return;

        var activeDeployment = ResolveActiveDeployment(deploymentCatalog.Deployments);
        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var revisionCatalog = ToRevisionCatalogReadModel(
            context,
            stateEvent.Version,
            stateEvent.EventId ?? string.Empty,
            state,
            serviceKey,
            observedAt);
        await MaterializeAsync(
            state.Identity,
            serviceKey,
            revisionCatalog,
            activeDeployment,
            stateEvent.EventId ?? string.Empty,
            observedAt,
            ct);
    }

    private async Task MaterializeAsync(
        ServiceIdentity identity,
        string serviceKey,
        ServiceRevisionCatalogReadModel? revisionCatalog,
        ServiceDeploymentRecord? activeDeployment,
        string eventId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        if (activeDeployment == null)
        {
            await DeleteInactiveWorkflowSourcesAsync(identity, revisionCatalog, null, eventId, observedAt, ct);
            return;
        }

        if (!TryResolveWorkflowRevision(revisionCatalog, activeDeployment.RevisionId, identity.ServiceId, out var revision, out var workflowId))
        {
            await DeleteInactiveWorkflowSourcesAsync(identity, revisionCatalog, null, eventId, observedAt, ct);
            return;
        }

        await _catalogueWriteDispatcher.UpsertAsync(
            ToCatalogueServiceSource(identity, serviceKey, workflowId, revision, activeDeployment, eventId, observedAt),
            ct);
        await _catalogueRowMaterializer.RefreshAsync(
            identity.TenantId,
            workflowId,
            eventId,
            observedAt,
            ct);
        await DeleteInactiveWorkflowSourcesAsync(identity, revisionCatalog, workflowId, eventId, observedAt, ct);
    }

    private bool TryGetObservedState(
        EventEnvelope envelope,
        out ServiceDeploymentState? state,
        out string eventId,
        out DateTimeOffset observedAt)
    {
        state = null;
        eventId = string.Empty;
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
        observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        return true;
    }

    private static ServiceDeploymentRecord? ResolveActiveDeployment(IEnumerable<ServiceDeploymentRecord>? deployments) =>
        deployments?
            .Where(static deployment => deployment.Status == ServiceDeploymentStatus.Active)
            .OrderByDescending(static deployment => deployment.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static ServiceDeploymentRecord? ResolveActiveDeployment(IEnumerable<ServiceDeploymentReadModel>? deployments) =>
        deployments?
            .Where(static deployment => string.Equals(deployment.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
            .OrderByDescending(static deployment => deployment.UpdatedAt)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .Select(static deployment => new ServiceDeploymentRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                Status = ServiceDeploymentStatus.Active,
                UpdatedAt = Timestamp.FromDateTimeOffset(deployment.UpdatedAt),
            })
            .FirstOrDefault();

    private static ServiceRevisionCatalogReadModel ToRevisionCatalogReadModel(
        ServiceRevisionCatalogProjectionContext context,
        long stateVersion,
        string eventId,
        ServiceRevisionCatalogState state,
        string serviceKey,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = serviceKey,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = observedAt,
            Revisions = state.Revisions
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new ServiceRevisionEntryReadModel
                {
                    RevisionId = entry.Key?.Trim() ?? string.Empty,
                    WorkflowName = entry.Value.Spec?.WorkflowSpec?.WorkflowName ?? string.Empty,
                    PreparedArtifact = entry.Value.PreparedArtifact?.Clone(),
                })
                .ToList(),
        };

    private async Task DeleteInactiveWorkflowSourcesAsync(
        ServiceIdentity identity,
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string? activeWorkflowId,
        string eventId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        foreach (var workflowId in ResolveWorkflowIds(revisionCatalog, identity.ServiceId))
        {
            if (string.Equals(workflowId, activeWorkflowId, StringComparison.Ordinal))
                continue;

            await _catalogueWriteDispatcher.DeleteAsync(
                new ProjectionDocumentDeleteMarker(
                    ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceDocumentId(identity.TenantId, workflowId),
                    ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceActorId(identity.TenantId, workflowId),
                    ScopeWorkflowCatalogueRowMaterializer.BuildSourceDeleteStateVersion(observedAt),
                    eventId,
                    observedAt),
                ct);
            await _catalogueRowMaterializer.RefreshAsync(
                identity.TenantId,
                workflowId,
                eventId,
                observedAt,
                ct);
        }
    }

    private static IReadOnlyList<string> ResolveWorkflowIds(
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string fallbackWorkflowId)
    {
        if (revisionCatalog == null)
            return [];

        return revisionCatalog.Revisions
            .Select(revision => TryResolveWorkflowId(revision, fallbackWorkflowId, out var workflowId) ? workflowId : string.Empty)
            .Where(static workflowId => !string.IsNullOrWhiteSpace(workflowId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryResolveWorkflowRevision(
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string revisionId,
        string fallbackWorkflowId,
        out ServiceRevisionEntryReadModel revision,
        out string workflowId)
    {
        revision = null!;
        workflowId = string.Empty;
        if (revisionCatalog == null || string.IsNullOrWhiteSpace(revisionId))
            return false;

        revision = revisionCatalog.Revisions.FirstOrDefault(entry =>
            string.Equals(entry.RevisionId, revisionId, StringComparison.Ordinal))!;
        return TryResolveWorkflowId(revision, fallbackWorkflowId, out workflowId);
    }

    private static bool TryResolveWorkflowId(
        ServiceRevisionEntryReadModel? revision,
        string fallbackWorkflowId,
        out string workflowId)
    {
        workflowId = string.Empty;
        if (revision?.PreparedArtifact?.DeploymentPlan?.PlanSpecCase != ServiceDeploymentPlan.PlanSpecOneofCase.WorkflowPlan)
            return false;

        try
        {
            var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
                revision.PreparedArtifact,
                revision.RevisionId);
            workflowId = string.IsNullOrWhiteSpace(bindingIdentity.WorkflowId)
                ? fallbackWorkflowId
                : bindingIdentity.WorkflowId;
            return !string.IsNullOrWhiteSpace(workflowId);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ScopeWorkflowCatalogueSourceDocument ToCatalogueServiceSource(
        ServiceIdentity identity,
        string serviceKey,
        string workflowId,
        ServiceRevisionEntryReadModel revision,
        ServiceDeploymentRecord deployment,
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
            ActorId = ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceActorId(identity.TenantId, workflowId),
            StateVersion = ScopeWorkflowCatalogueRowMaterializer.BuildSourceStateVersion(sourceUpdatedAt),
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

internal sealed class ScopeWorkflowCatalogueRevisionSourceProjector
    : IProjectionArtifactMaterializer<ServiceRevisionCatalogProjectionContext>
{
    private readonly ScopeWorkflowCatalogueServiceSourceProjector _serviceSourceProjector;

    public ScopeWorkflowCatalogueRevisionSourceProjector(
        ScopeWorkflowCatalogueServiceSourceProjector serviceSourceProjector)
    {
        _serviceSourceProjector = serviceSourceProjector ?? throw new ArgumentNullException(nameof(serviceSourceProjector));
    }

    public ValueTask ProjectAsync(
        ServiceRevisionCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default) =>
        _serviceSourceProjector.ProjectRevisionCatalogAsync(context, envelope, ct);
}
