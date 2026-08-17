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
    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _serviceCatalogReader;
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionCatalogReader;
    private readonly IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> _deploymentCatalogReader;
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _catalogueSourceReader;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly ScopeWorkflowCatalogueRowMaterializer _catalogueRowMaterializer;
    private readonly IProjectionClock _clock;

    public ScopeWorkflowCatalogueServiceSourceProjector(
        IProjectionDocumentReader<ServiceCatalogReadModel, string> serviceCatalogReader,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionCatalogReader,
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> deploymentCatalogReader,
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> catalogueSourceReader,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        ScopeWorkflowCatalogueRowMaterializer catalogueRowMaterializer,
        IProjectionClock clock)
    {
        _serviceCatalogReader = serviceCatalogReader ?? throw new ArgumentNullException(nameof(serviceCatalogReader));
        _revisionCatalogReader = revisionCatalogReader ?? throw new ArgumentNullException(nameof(revisionCatalogReader));
        _deploymentCatalogReader = deploymentCatalogReader ?? throw new ArgumentNullException(nameof(deploymentCatalogReader));
        _catalogueSourceReader = catalogueSourceReader ?? throw new ArgumentNullException(nameof(catalogueSourceReader));
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
        var serviceCatalog = await _serviceCatalogReader.GetAsync(serviceKey, ct);
        var catalogueVisibleDeployment = ResolveCatalogueVisibleDeployment(
            state.Deployments.Values,
            serviceCatalog?.DefaultServingRevisionId);
        await MaterializeAsync(state.Identity, serviceKey, revisionCatalog, catalogueVisibleDeployment, eventId, observedAt, ct);
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

        var serviceCatalog = await _serviceCatalogReader.GetAsync(serviceKey, ct);
        var deploymentCatalog = await _deploymentCatalogReader.GetAsync(serviceKey, ct);
        var catalogueVisibleDeployment = ResolveCatalogueVisibleDeployment(
            deploymentCatalog?.Deployments,
            serviceCatalog?.DefaultServingRevisionId);
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
            catalogueVisibleDeployment,
            stateEvent.EventId ?? string.Empty,
            observedAt,
            ct);
    }

    private async Task MaterializeAsync(
        ServiceIdentity identity,
        string serviceKey,
        ServiceRevisionCatalogReadModel? revisionCatalog,
        ServiceDeploymentRecord? catalogueVisibleDeployment,
        string eventId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        if (catalogueVisibleDeployment == null)
        {
            await DeleteNonVisibleWorkflowSourcesAsync(identity, revisionCatalog, null, eventId, observedAt, ct);
            return;
        }

        if (!TryResolveWorkflowRevision(revisionCatalog, catalogueVisibleDeployment.RevisionId, identity.ServiceId, out var revision, out var workflowId))
        {
            await DeleteNonVisibleWorkflowSourcesAsync(identity, revisionCatalog, null, eventId, observedAt, ct);
            return;
        }

        var serviceSource = await PrepareServiceSourceAsync(
            ToCatalogueServiceSource(identity, serviceKey, workflowId, revision, catalogueVisibleDeployment, eventId, observedAt),
            ct);
        var shouldUpsert = !await ShouldSkipDeactivatedServiceSourceAsync(serviceSource, ct);
        if (shouldUpsert)
        {
            await _catalogueWriteDispatcher.UpsertAsync(serviceSource, ct);
            await _catalogueRowMaterializer.RefreshAsync(
                identity.TenantId,
                workflowId,
                eventId,
                observedAt,
                ct);
        }
        await DeleteNonVisibleWorkflowSourcesAsync(identity, revisionCatalog, workflowId, eventId, observedAt, ct);
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

    private static ServiceDeploymentRecord? ResolveCatalogueVisibleDeployment(
        IEnumerable<ServiceDeploymentRecord>? deployments,
        string? defaultServingRevisionId)
    {
        var eligibleDeployments = deployments?
            .Where(static deployment => deployment.Status is ServiceDeploymentStatus.Active or ServiceDeploymentStatus.Deactivated)
            .ToArray();
        if (eligibleDeployments == null || eligibleDeployments.Length == 0)
            return null;

        return ResolveDefaultDeployment(eligibleDeployments, defaultServingRevisionId)
               ?? ResolveActiveDeployment(eligibleDeployments)
               ?? ResolveLatestDeployment(eligibleDeployments, ServiceDeploymentStatus.Deactivated);
    }

    private static ServiceDeploymentRecord? ResolveCatalogueVisibleDeployment(
        IEnumerable<ServiceDeploymentReadModel>? deployments,
        string? defaultServingRevisionId) =>
        ResolveCatalogueVisibleDeployment(
            deployments?.Select(static deployment => new ServiceDeploymentRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                Status = global::System.Enum.TryParse<ServiceDeploymentStatus>(deployment.Status, ignoreCase: true, out var status)
                    ? status
                    : ServiceDeploymentStatus.Unspecified,
                ActivatedAt = deployment.ActivatedAt == null
                    ? null
                    : Timestamp.FromDateTimeOffset(deployment.ActivatedAt.Value),
                UpdatedAt = Timestamp.FromDateTimeOffset(deployment.UpdatedAt),
            }),
            defaultServingRevisionId);

    private static ServiceDeploymentRecord? ResolveDefaultDeployment(
        IEnumerable<ServiceDeploymentRecord> deployments,
        string? defaultServingRevisionId)
    {
        if (string.IsNullOrWhiteSpace(defaultServingRevisionId))
            return null;

        return deployments
            .Where(deployment => string.Equals(deployment.RevisionId, defaultServingRevisionId, StringComparison.Ordinal))
            .OrderByDescending(static deployment => deployment.Status == ServiceDeploymentStatus.Active)
            .ThenByDescending(static deployment => ResolveDeploymentActivationTime(deployment))
            .ThenByDescending(static deployment => deployment.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static ServiceDeploymentRecord? ResolveActiveDeployment(IEnumerable<ServiceDeploymentRecord> deployments) =>
        deployments
            .Where(static deployment => deployment.Status == ServiceDeploymentStatus.Active)
            .OrderByDescending(static deployment => ResolveDeploymentActivationTime(deployment))
            .ThenByDescending(static deployment => deployment.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static ServiceDeploymentRecord? ResolveLatestDeployment(
        IEnumerable<ServiceDeploymentRecord> deployments,
        ServiceDeploymentStatus status) =>
        deployments
            .Where(deployment => deployment.Status == status)
            .OrderByDescending(static deployment => deployment.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static DateTimeOffset ResolveDeploymentActivationTime(ServiceDeploymentRecord deployment) =>
        deployment.ActivatedAt?.ToDateTimeOffset()
        ?? deployment.UpdatedAt?.ToDateTimeOffset()
        ?? DateTimeOffset.UnixEpoch;

    private async Task<bool> ShouldSkipDeactivatedServiceSourceAsync(
        ScopeWorkflowCatalogueSourceDocument serviceSource,
        CancellationToken ct)
    {
        if (!string.Equals(serviceSource.DeploymentStatus, ServiceDeploymentStatus.Deactivated.ToString(), StringComparison.Ordinal))
            return false;

        var existingSource = await _catalogueSourceReader.GetAsync(serviceSource.Id, ct);
        return existingSource != null &&
               string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) &&
               !string.Equals(existingSource.PublishedServiceId, serviceSource.PublishedServiceId, StringComparison.Ordinal);
    }

    private async Task<ScopeWorkflowCatalogueSourceDocument> PrepareServiceSourceAsync(
        ScopeWorkflowCatalogueSourceDocument serviceSource,
        CancellationToken ct)
    {
        if (!string.Equals(serviceSource.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
            return serviceSource;

        var existingSource = await _catalogueSourceReader.GetAsync(serviceSource.Id, ct);
        if (existingSource == null ||
            !string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Deactivated.ToString(), StringComparison.Ordinal) ||
            string.Equals(existingSource.PublishedServiceId, serviceSource.PublishedServiceId, StringComparison.Ordinal))
        {
            return serviceSource;
        }

        serviceSource.StateVersion = Math.Max(serviceSource.StateVersion, NextStateVersion(existingSource.StateVersion));
        return serviceSource;
    }

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

    private async Task DeleteNonVisibleWorkflowSourcesAsync(
        ServiceIdentity identity,
        ServiceRevisionCatalogReadModel? revisionCatalog,
        string? catalogueVisibleWorkflowId,
        string eventId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        foreach (var workflowId in ResolveWorkflowIds(revisionCatalog, identity.ServiceId))
        {
            if (string.Equals(workflowId, catalogueVisibleWorkflowId, StringComparison.Ordinal))
                continue;

            var sourceId = ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceDocumentId(identity.TenantId, workflowId);
            var existingSource = await _catalogueSourceReader.GetAsync(sourceId, ct);
            if (ShouldSkipServiceSourceDelete(existingSource, identity.ServiceId))
                continue;

            await _catalogueWriteDispatcher.DeleteAsync(
                new ProjectionDocumentDeleteMarker(
                    sourceId,
                    ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceActorId(identity.TenantId, workflowId),
                    ResolveSourceDeleteStateVersion(existingSource, observedAt),
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

    private static bool ShouldSkipServiceSourceDelete(
        ScopeWorkflowCatalogueSourceDocument? existingSource,
        string publishedServiceId) =>
        existingSource != null &&
        string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) &&
        !string.Equals(existingSource.PublishedServiceId, publishedServiceId, StringComparison.Ordinal);

    private static long ResolveSourceDeleteStateVersion(
        ScopeWorkflowCatalogueSourceDocument? existingSource,
        DateTimeOffset observedAt) =>
        Math.Max(
            existingSource == null ? 0 : NextStateVersion(existingSource.StateVersion),
            ScopeWorkflowCatalogueRowMaterializer.BuildSourceDeleteStateVersion(observedAt));

    private static long NextStateVersion(long stateVersion) =>
        stateVersion == long.MaxValue ? long.MaxValue : stateVersion + 1;

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
