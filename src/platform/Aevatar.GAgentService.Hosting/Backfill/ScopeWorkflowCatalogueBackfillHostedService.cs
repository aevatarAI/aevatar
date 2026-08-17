using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Hosting.Backfill;

internal sealed class ScopeWorkflowCatalogueBackfillHostedService : BackgroundService
{
    private const int SourceReadTake = 10_000;

    private static readonly JsonParser WorkspaceStateParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _serviceCatalogReader;
    private readonly IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> _deploymentCatalogReader;
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionCatalogReader;
    private readonly IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string> _workspaceReader;
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _catalogueSourceReader;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly ScopeWorkflowCatalogueRowMaterializer _catalogueRowMaterializer;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly ILogger<ScopeWorkflowCatalogueBackfillHostedService> _logger;

    public ScopeWorkflowCatalogueBackfillHostedService(
        IProjectionDocumentReader<ServiceCatalogReadModel, string> serviceCatalogReader,
        IProjectionDocumentReader<ServiceDeploymentCatalogReadModel, string> deploymentCatalogReader,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionCatalogReader,
        IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string> workspaceReader,
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> catalogueSourceReader,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        ScopeWorkflowCatalogueRowMaterializer catalogueRowMaterializer,
        IWorkflowYamlDocumentService yamlDocumentService,
        ILogger<ScopeWorkflowCatalogueBackfillHostedService> logger)
    {
        _serviceCatalogReader = serviceCatalogReader;
        _deploymentCatalogReader = deploymentCatalogReader;
        _revisionCatalogReader = revisionCatalogReader;
        _workspaceReader = workspaceReader;
        _catalogueSourceReader = catalogueSourceReader;
        _catalogueWriteDispatcher = catalogueWriteDispatcher;
        _catalogueRowMaterializer = catalogueRowMaterializer;
        _yamlDocumentService = yamlDocumentService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await RunBackfillOnceAsync(stoppingToken);
    }

    internal async Task RunBackfillOnceAsync(CancellationToken cancellationToken)
    {
        // The backfill is convergence acceleration, not a boot invariant: the
        // event-driven projectors keep the catalogue converging regardless,
        // and a pod restart re-runs the backfill. It must therefore never
        // fault the background service — during a rolling upgrade the first new-image
        // pod can hit actors an old-image silo cannot resolve
        // (UnknownAgentKindException, which additionally has no Orleans
        // codec), and propagating that failure would stop the host.
        try
        {
            await RunBackfillCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Scope workflow catalogue backfill failed; background execution has stopped. " +
                "Event-driven projections keep the catalogue converging and a pod restart re-runs the backfill.");
        }
    }

    private async Task RunBackfillCoreAsync(CancellationToken cancellationToken)
    {
        var serviceCatalogs = await QueryAllAsync(_serviceCatalogReader, cancellationToken);
        var deploymentCatalogs = await QueryAllAsync(_deploymentCatalogReader, cancellationToken);
        var revisionCatalogs = await QueryAllAsync(_revisionCatalogReader, cancellationToken);
        var workspaces = await QueryAllAsync(_workspaceReader, cancellationToken);
        var catalogueSources = await QueryAllAsync(_catalogueSourceReader, cancellationToken);

        var existingDraftSourcesByScope = BuildExistingSourcesByScope(catalogueSources, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);
        var existingServiceSourcesByScope = BuildExistingSourcesByScope(catalogueSources, ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        var currentServiceSourcesById = BuildExistingSourcesById(catalogueSources, ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        var currentDraftSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var currentServiceSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var draftCleanupAuthorities = new Dictionary<string, DraftCleanupAuthority>(StringComparer.Ordinal);
        var deploymentCatalogsByServiceKey = deploymentCatalogs.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var revisionCatalogsByServiceKey = revisionCatalogs.ToDictionary(static x => x.Id, StringComparer.Ordinal);

        var serviceCount = 0;
        foreach (var serviceCatalog in serviceCatalogs)
        {
            if (string.IsNullOrWhiteSpace(serviceCatalog.TenantId) ||
                string.IsNullOrWhiteSpace(serviceCatalog.Id) ||
                string.IsNullOrWhiteSpace(serviceCatalog.ServiceId))
            {
                continue;
            }

            if (!deploymentCatalogsByServiceKey.TryGetValue(serviceCatalog.Id, out var deploymentCatalog) ||
                ResolveCatalogueVisibleDeployment(serviceCatalog, deploymentCatalog) is not { } catalogueVisibleDeployment ||
                !revisionCatalogsByServiceKey.TryGetValue(serviceCatalog.Id, out var revisionCatalog) ||
                !TryResolveWorkflowRevision(
                    revisionCatalog,
                    catalogueVisibleDeployment.RevisionId,
                    serviceCatalog.ServiceId,
                    out var revision,
                    out var workflowId))
            {
                continue;
            }

            var serviceSource = ToServiceSource(serviceCatalog, deploymentCatalog, revision, catalogueVisibleDeployment, workflowId);
            var existingServiceSource = await ResolveKnownServiceSourceAsync(
                currentServiceSourcesById,
                serviceSource.Id,
                cancellationToken);
            serviceSource = PrepareServiceSource(serviceSource, existingServiceSource);
            currentServiceSourceIds.Add(serviceSource.Id);
            if (ShouldSkipDeactivatedServiceSource(existingServiceSource, serviceSource))
                continue;

            await _catalogueWriteDispatcher.UpsertAsync(serviceSource, cancellationToken);
            currentServiceSourcesById[serviceSource.Id] = serviceSource;
            await RefreshRowAsync(
                serviceCatalog.TenantId,
                workflowId,
                deploymentCatalog.LastEventId,
                deploymentCatalog.UpdatedAt,
                cancellationToken);
            serviceCount++;
        }

        var draftCount = 0;
        foreach (var workspace in workspaces)
        {
            if (string.IsNullOrWhiteSpace(workspace.StateRootJson))
                continue;

            StudioWorkspaceState state;
            try
            {
                state = WorkspaceStateParser.Parse<StudioWorkspaceState>(workspace.StateRootJson);
            }
            catch (InvalidJsonException ex)
            {
                _logger.LogWarning(ex, "Skipping workflow catalogue draft backfill for workspace {WorkspaceActorId}: state_root_json is invalid.", workspace.ActorId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(state.ScopeId))
                continue;

            var cleanupAuthority = new DraftCleanupAuthority(
                workspace.LastEventId,
                workspace.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
            draftCleanupAuthorities[state.ScopeId] = cleanupAuthority;

            foreach (var draft in state.Drafts.Values)
            {
                if (string.IsNullOrWhiteSpace(draft.WorkflowId))
                    continue;

                var draftSource = ToDraftSource(workspace, state.ScopeId, draft);
                currentDraftSourceIds.Add(draftSource.Id);
                await _catalogueWriteDispatcher.UpsertAsync(draftSource, cancellationToken);
                await RefreshRowAsync(
                    state.ScopeId,
                    draft.WorkflowId,
                    workspace.LastEventId,
                    workspace.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    cancellationToken);
                draftCount++;
            }
        }

        var staleServiceCount = await DeleteStaleSourcesAsync(existingServiceSourcesByScope, currentServiceSourceIds, cancellationToken);
        var staleDraftCount = 0;
        foreach (var (scopeId, cleanupAuthority) in draftCleanupAuthorities)
        {
            if (!existingDraftSourcesByScope.TryGetValue(scopeId, out var existingDraftSources))
                continue;

            foreach (var existingDraftSource in existingDraftSources)
            {
                if (currentDraftSourceIds.Contains(existingDraftSource.Id))
                    continue;

                await DeleteSourceAsync(existingDraftSource, cleanupAuthority.LastEventId, cleanupAuthority.UpdatedAt, cancellationToken);
                await RefreshRowAsync(
                    existingDraftSource.ScopeId,
                    existingDraftSource.WorkflowId,
                    cleanupAuthority.LastEventId,
                    cleanupAuthority.UpdatedAt,
                    cancellationToken);
                staleDraftCount++;
            }
        }

        _logger.LogInformation(
            "Backfilled scope workflow catalogue sources: {ServiceCount} service, {DraftCount} draft, {StaleServiceCount} stale service deleted, {StaleDraftCount} stale draft deleted.",
            serviceCount,
            draftCount,
            staleServiceCount,
            staleDraftCount);
    }

    private static async Task<IReadOnlyList<TReadModel>> QueryAllAsync<TReadModel>(
        IProjectionDocumentReader<TReadModel, string> reader,
        CancellationToken ct)
        where TReadModel : class, IProjectionReadModel
    {
        var documents = new List<TReadModel>();
        string? cursor = null;
        do
        {
            var result = await reader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Take = SourceReadTake,
                    Cursor = cursor,
                },
                ct);
            documents.AddRange(result.Items);
            cursor = result.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return documents;
    }

    private static IReadOnlyDictionary<string, ScopeWorkflowCatalogueSourceDocument[]> BuildExistingSourcesByScope(
        IReadOnlyList<ScopeWorkflowCatalogueSourceDocument> catalogueSources,
        string sourceKind) =>
        catalogueSources
            .Where(source => string.Equals(source.SourceKind, sourceKind, StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(source.ScopeId) &&
                             !string.IsNullOrWhiteSpace(source.WorkflowId) &&
                             !string.IsNullOrWhiteSpace(source.Id))
            .GroupBy(static source => source.ScopeId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

    private static Dictionary<string, ScopeWorkflowCatalogueSourceDocument> BuildExistingSourcesById(
        IReadOnlyList<ScopeWorkflowCatalogueSourceDocument> catalogueSources,
        string sourceKind) =>
        catalogueSources
            .Where(source => string.Equals(source.SourceKind, sourceKind, StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(source.Id))
            .GroupBy(static source => source.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);

    private async Task<ScopeWorkflowCatalogueSourceDocument?> ResolveKnownServiceSourceAsync(
        IReadOnlyDictionary<string, ScopeWorkflowCatalogueSourceDocument> currentServiceSourcesById,
        string sourceId,
        CancellationToken ct)
    {
        currentServiceSourcesById.TryGetValue(sourceId, out var trackedSource);
        var storedSource = await _catalogueSourceReader.GetAsync(sourceId, ct);
        if (trackedSource == null)
            return storedSource;

        if (storedSource == null)
            return trackedSource;

        return storedSource.StateVersion > trackedSource.StateVersion ? storedSource : trackedSource;
    }

    private static ScopeWorkflowCatalogueSourceDocument PrepareServiceSource(
        ScopeWorkflowCatalogueSourceDocument candidate,
        ScopeWorkflowCatalogueSourceDocument? existingSource)
    {
        if (existingSource == null ||
            !string.Equals(candidate.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) ||
            !string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Deactivated.ToString(), StringComparison.Ordinal) ||
            string.Equals(existingSource.PublishedServiceId, candidate.PublishedServiceId, StringComparison.Ordinal))
        {
            return candidate;
        }

        candidate.StateVersion = Math.Max(candidate.StateVersion, NextStateVersion(existingSource.StateVersion));
        return candidate;
    }

    private static bool ShouldSkipDeactivatedServiceSource(
        ScopeWorkflowCatalogueSourceDocument? existingSource,
        ScopeWorkflowCatalogueSourceDocument candidate)
    {
        if (!string.Equals(candidate.DeploymentStatus, ServiceDeploymentStatus.Deactivated.ToString(), StringComparison.Ordinal))
            return false;

        return existingSource != null &&
               string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) &&
               !string.Equals(existingSource.PublishedServiceId, candidate.PublishedServiceId, StringComparison.Ordinal);
    }

    private async Task<int> DeleteStaleSourcesAsync(
        IReadOnlyDictionary<string, ScopeWorkflowCatalogueSourceDocument[]> existingSourcesByScope,
        HashSet<string> currentSourceIds,
        CancellationToken ct)
    {
        var staleCount = 0;
        foreach (var existingSources in existingSourcesByScope.Values)
        {
            foreach (var existingSource in existingSources)
            {
                if (currentSourceIds.Contains(existingSource.Id))
                    continue;

                var latestSource = await _catalogueSourceReader.GetAsync(existingSource.Id, ct) ?? existingSource;
                if (ShouldSkipServiceSourceDelete(existingSource.PublishedServiceId, latestSource))
                    continue;

                await DeleteSourceAsync(latestSource, ct);
                await RefreshRowAsync(
                    latestSource.ScopeId,
                    latestSource.WorkflowId,
                    latestSource.LastEventId,
                    latestSource.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    ct);
                staleCount++;
            }
        }

        return staleCount;
    }

    private static bool ShouldSkipServiceSourceDelete(
        string cleanupPublishedServiceId,
        ScopeWorkflowCatalogueSourceDocument existingSource) =>
        string.Equals(existingSource.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) &&
        !string.Equals(existingSource.PublishedServiceId, cleanupPublishedServiceId, StringComparison.Ordinal);

    private Task DeleteSourceAsync(
        ScopeWorkflowCatalogueSourceDocument source,
        CancellationToken ct) =>
        DeleteSourceAsync(
            source,
            source.LastEventId,
            source.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            ct);

    private async Task DeleteSourceAsync(
        ScopeWorkflowCatalogueSourceDocument source,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct) =>
        await _catalogueWriteDispatcher.DeleteAsync(
            new ProjectionDocumentDeleteMarker(
                source.Id,
                source.ActorId,
                ResolveSourceDeleteStateVersion(source, updatedAt),
                eventId,
                updatedAt),
            ct);

    private static long ResolveSourceDeleteStateVersion(
        ScopeWorkflowCatalogueSourceDocument source,
        DateTimeOffset updatedAt) =>
        Math.Max(
            NextStateVersion(source.StateVersion),
            ScopeWorkflowCatalogueRowMaterializer.BuildSourceDeleteStateVersion(updatedAt));

    private static long NextStateVersion(long stateVersion) =>
        stateVersion == long.MaxValue ? long.MaxValue : stateVersion + 1;

    private async Task RefreshRowAsync(
        string scopeId,
        string workflowId,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        // One poisoned row (e.g. its actor is only resolvable on a newer
        // image mid-rollout) must not stop the rest of the backfill.
        try
        {
            await _catalogueRowMaterializer.RefreshAsync(scopeId, workflowId, eventId, updatedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping workflow catalogue backfill row for scope {ScopeId}, workflow {WorkflowId}: refresh failed.",
                scopeId,
                workflowId);
        }
    }

    private static ServiceDeploymentReadModel? ResolveCatalogueVisibleDeployment(
        ServiceCatalogReadModel serviceCatalog,
        ServiceDeploymentCatalogReadModel deploymentCatalog)
    {
        var eligibleDeployments = deploymentCatalog.Deployments
            .Where(static deployment => IsActiveDeployment(deployment) || IsDeactivatedDeployment(deployment))
            .ToArray();

        return ResolveDefaultDeployment(eligibleDeployments, serviceCatalog.DefaultServingRevisionId)
               ?? ResolveActiveDeployment(eligibleDeployments)
               ?? ResolveLatestDeployment(eligibleDeployments, ServiceDeploymentStatus.Deactivated);
    }

    private static ServiceDeploymentReadModel? ResolveDefaultDeployment(
        IEnumerable<ServiceDeploymentReadModel> deployments,
        string? defaultServingRevisionId)
    {
        if (string.IsNullOrWhiteSpace(defaultServingRevisionId))
            return null;

        return deployments
            .Where(deployment => string.Equals(deployment.RevisionId, defaultServingRevisionId, StringComparison.Ordinal))
            .OrderByDescending(static deployment => IsActiveDeployment(deployment))
            .ThenByDescending(static deployment => ResolveDeploymentActivationTime(deployment))
            .ThenByDescending(static deployment => deployment.UpdatedAt)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static ServiceDeploymentReadModel? ResolveActiveDeployment(IEnumerable<ServiceDeploymentReadModel> deployments) =>
        deployments
            .Where(static deployment => IsActiveDeployment(deployment))
            .OrderByDescending(static deployment => ResolveDeploymentActivationTime(deployment))
            .ThenByDescending(static deployment => deployment.UpdatedAt)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static ServiceDeploymentReadModel? ResolveLatestDeployment(
        IEnumerable<ServiceDeploymentReadModel> deployments,
        ServiceDeploymentStatus status) =>
        deployments
            .Where(deployment => string.Equals(deployment.Status, status.ToString(), StringComparison.Ordinal))
            .OrderByDescending(static deployment => deployment.UpdatedAt)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsActiveDeployment(ServiceDeploymentReadModel deployment) =>
        string.Equals(deployment.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal);

    private static bool IsDeactivatedDeployment(ServiceDeploymentReadModel deployment) =>
        string.Equals(deployment.Status, ServiceDeploymentStatus.Deactivated.ToString(), StringComparison.Ordinal);

    private static DateTimeOffset ResolveDeploymentActivationTime(ServiceDeploymentReadModel deployment) =>
        deployment.ActivatedAt ?? deployment.UpdatedAt;

    private static bool TryResolveWorkflowRevision(
        ServiceRevisionCatalogReadModel revisionCatalog,
        string revisionId,
        string fallbackWorkflowId,
        out ServiceRevisionEntryReadModel revision,
        out string workflowId)
    {
        revision = revisionCatalog.Revisions.FirstOrDefault(entry =>
            string.Equals(entry.RevisionId, revisionId, StringComparison.Ordinal))!;
        workflowId = string.Empty;
        if (revision?.PreparedArtifact?.DeploymentPlan?.PlanSpecCase != ServiceDeploymentPlan.PlanSpecOneofCase.WorkflowPlan)
            return false;

        try
        {
            var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
                revision.PreparedArtifact,
                revisionId);
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

    private static ScopeWorkflowCatalogueSourceDocument ToServiceSource(
        ServiceCatalogReadModel serviceCatalog,
        ServiceDeploymentCatalogReadModel deploymentCatalog,
        ServiceRevisionEntryReadModel revision,
        ServiceDeploymentReadModel deployment,
        string workflowId)
    {
        var workflowName = revision.PreparedArtifact.DeploymentPlan.WorkflowPlan.WorkflowName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowName))
            workflowName = revision.WorkflowName;
        if (string.IsNullOrWhiteSpace(workflowName))
            workflowName = string.IsNullOrWhiteSpace(serviceCatalog.DisplayName) ? workflowId : serviceCatalog.DisplayName;

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceDocumentId(serviceCatalog.TenantId, workflowId),
            ActorId = ScopeWorkflowCatalogueRowMaterializer.BuildServiceSourceActorId(serviceCatalog.TenantId, workflowId),
            StateVersion = ScopeWorkflowCatalogueRowMaterializer.BuildSourceStateVersion(deployment.UpdatedAt),
            LastEventId = deploymentCatalog.LastEventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(deploymentCatalog.UpdatedAt),
            ScopeId = serviceCatalog.TenantId,
            WorkflowId = workflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind,
            Name = workflowName,
            SourceUpdatedAtUtc = deployment.UpdatedAt,
            ServiceKey = serviceCatalog.Id,
            WorkflowName = workflowName,
            CommittedActorId = deployment.PrimaryActorId,
            ActiveRevisionId = deployment.RevisionId,
            DeploymentId = deployment.DeploymentId,
            DeploymentStatus = deployment.Status,
            ServiceAppId = serviceCatalog.AppId,
            ServiceNamespace = serviceCatalog.Namespace,
            PublishedServiceId = serviceCatalog.ServiceId,
        };
    }

    private ScopeWorkflowCatalogueSourceDocument ToDraftSource(
        StudioWorkspaceCurrentStateDocument workspace,
        string scopeId,
        StudioWorkflowDraft draft)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var name = parse.Document?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(draft.Name) ? draft.WorkflowId : draft.Name.Trim();

        var updatedAt = workspace.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        var sourceUpdatedAt = draft.UpdatedAtUtc?.ToDateTimeOffset() ?? updatedAt;

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceDocumentId(scopeId, draft.WorkflowId),
            ActorId = ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceActorId(scopeId, draft.WorkflowId),
            StateVersion = ScopeWorkflowCatalogueRowMaterializer.BuildSourceStateVersion(sourceUpdatedAt),
            LastEventId = workspace.LastEventId,
            UpdatedAt = workspace.UpdatedAt,
            ScopeId = scopeId,
            WorkflowId = draft.WorkflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            Name = name,
            Description = parse.Document?.Description ?? string.Empty,
            SourceUpdatedAtUtc = sourceUpdatedAt,
        };
    }

    private sealed record DraftCleanupAuthority(
        string LastEventId,
        DateTimeOffset UpdatedAt);
}
