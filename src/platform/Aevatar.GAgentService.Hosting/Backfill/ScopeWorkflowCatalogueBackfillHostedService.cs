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

internal sealed class ScopeWorkflowCatalogueBackfillHostedService : IHostedService
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var serviceCatalogs = await QueryAllAsync(_serviceCatalogReader, cancellationToken);
        var deploymentCatalogs = await QueryAllAsync(_deploymentCatalogReader, cancellationToken);
        var revisionCatalogs = await QueryAllAsync(_revisionCatalogReader, cancellationToken);
        var workspaces = await QueryAllAsync(_workspaceReader, cancellationToken);
        var catalogueSources = await QueryAllAsync(_catalogueSourceReader, cancellationToken);

        var existingDraftSourcesByScope = BuildExistingSourcesByScope(catalogueSources, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);
        var existingServiceSourcesByScope = BuildExistingSourcesByScope(catalogueSources, ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
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
                ResolveActiveDeployment(deploymentCatalog) is not { } activeDeployment ||
                !revisionCatalogsByServiceKey.TryGetValue(serviceCatalog.Id, out var revisionCatalog) ||
                !TryResolveWorkflowRevision(revisionCatalog, activeDeployment.RevisionId, out var revision, out var workflowId))
            {
                continue;
            }

            var serviceSource = ToServiceSource(serviceCatalog, deploymentCatalog, revision, activeDeployment, workflowId);
            currentServiceSourceIds.Add(serviceSource.Id);
            await _catalogueWriteDispatcher.UpsertAsync(serviceSource, cancellationToken);
            await RefreshRowAsync(
                serviceCatalog.TenantId,
                workflowId,
                deploymentCatalog.ActorId,
                deploymentCatalog.StateVersion,
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
                workspace.ActorId,
                workspace.StateVersion,
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
                    workspace.ActorId,
                    workspace.StateVersion,
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

                await DeleteSourceAsync(existingDraftSource.Id, cleanupAuthority, cancellationToken);
                await RefreshRowAsync(
                    existingDraftSource.ScopeId,
                    existingDraftSource.WorkflowId,
                    cleanupAuthority.ActorId,
                    cleanupAuthority.StateVersion,
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

                await DeleteSourceAsync(existingSource, ct);
                await RefreshRowAsync(
                    existingSource.ScopeId,
                    existingSource.WorkflowId,
                    existingSource.ActorId,
                    existingSource.StateVersion,
                    existingSource.LastEventId,
                    existingSource.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    ct);
                staleCount++;
            }
        }

        return staleCount;
    }

    private async Task DeleteSourceAsync(
        ScopeWorkflowCatalogueSourceDocument source,
        CancellationToken ct) =>
        await _catalogueWriteDispatcher.DeleteAsync(
            new ProjectionDocumentDeleteMarker(
                source.Id,
                source.ActorId,
                source.StateVersion,
                source.LastEventId,
                source.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue),
            ct);

    private async Task DeleteSourceAsync(
        string sourceId,
        DraftCleanupAuthority cleanupAuthority,
        CancellationToken ct) =>
        await _catalogueWriteDispatcher.DeleteAsync(
            new ProjectionDocumentDeleteMarker(
                sourceId,
                cleanupAuthority.ActorId,
                cleanupAuthority.StateVersion,
                cleanupAuthority.LastEventId,
                cleanupAuthority.UpdatedAt),
            ct);

    private Task RefreshRowAsync(
        string scopeId,
        string workflowId,
        string actorId,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct) =>
        _catalogueRowMaterializer.RefreshAsync(scopeId, workflowId, actorId, stateVersion, eventId, updatedAt, ct);

    private static ServiceDeploymentReadModel? ResolveActiveDeployment(ServiceDeploymentCatalogReadModel deploymentCatalog) =>
        deploymentCatalog.Deployments
            .Where(static deployment => string.Equals(deployment.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
            .OrderByDescending(static deployment => deployment.UpdatedAt)
            .ThenBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool TryResolveWorkflowRevision(
        ServiceRevisionCatalogReadModel revisionCatalog,
        string revisionId,
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
            workflowId = bindingIdentity.WorkflowId;
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
            ActorId = deploymentCatalog.ActorId,
            StateVersion = deploymentCatalog.StateVersion,
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

        var sourceUpdatedAt = draft.UpdatedAtUtc?.ToDateTimeOffset() ??
                              workspace.UpdatedAt?.ToDateTimeOffset() ??
                              DateTimeOffset.MinValue;

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceDocumentId(scopeId, draft.WorkflowId),
            ActorId = workspace.ActorId,
            StateVersion = workspace.StateVersion,
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
        string ActorId,
        long StateVersion,
        string LastEventId,
        DateTimeOffset UpdatedAt);
}
