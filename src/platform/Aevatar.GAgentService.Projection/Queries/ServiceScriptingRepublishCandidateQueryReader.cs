using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceScriptingRepublishCandidateQueryReader : IServiceScriptingRepublishCandidateQueryReader
{
    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _catalogStore;
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionStore;
    private readonly IProjectionDocumentReader<ServiceServingSetReadModel, string> _servingSetStore;
    private readonly bool _enabled;

    public ServiceScriptingRepublishCandidateQueryReader(
        IProjectionDocumentReader<ServiceCatalogReadModel, string> catalogStore,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionStore,
        IProjectionDocumentReader<ServiceServingSetReadModel, string> servingSetStore,
        ServiceProjectionOptions? options = null)
    {
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _servingSetStore = servingSetStore ?? throw new ArgumentNullException(nameof(servingSetStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<IReadOnlyList<ServiceScriptingRepublishCandidateSnapshot>> QueryServingByScopeScriptAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return [];

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = NormalizeRequired(scriptId, nameof(scriptId));

        var catalogResult = await _catalogStore.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1000,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.TenantId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(normalizedScopeId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.AppId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(ScopeServiceIdentityDefaults.ServiceAppId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ServiceCatalogReadModel.Namespace),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(ScopeServiceIdentityDefaults.ServiceNamespace),
                    },
                ],
            },
            ct);

        var candidates = new List<ServiceScriptingRepublishCandidateSnapshot>();
        foreach (var catalog in catalogResult.Items)
        {
            ct.ThrowIfCancellationRequested();

            var identity = new ServiceIdentity
            {
                TenantId = catalog.TenantId ?? string.Empty,
                AppId = catalog.AppId ?? string.Empty,
                Namespace = catalog.Namespace ?? string.Empty,
                ServiceId = catalog.ServiceId ?? string.Empty,
            };
            var serviceKey = ServiceKeys.Build(identity);

            var servingSet = await _servingSetStore.GetAsync(serviceKey, ct);
            if (servingSet == null)
                continue;

            var activeTargets = servingSet.Targets.Where(target =>
                target.AllocationWeight > 0 &&
                string.Equals(target.ServingState, ServiceServingState.Active.ToString(), StringComparison.Ordinal))
                .ToList();
            if (activeTargets.Count == 0)
                continue;

            var revisions = await _revisionStore.GetAsync(serviceKey, ct);
            if (revisions == null || revisions.Revisions.Count == 0)
                continue;

            ServiceServingTargetReadModel? selectedTarget = null;
            ServiceRevisionEntryReadModel? selectedRevision = null;

            foreach (var target in activeTargets)
            {
                var revision = revisions.Revisions.FirstOrDefault(entry =>
                    string.Equals(entry.RevisionId, target.RevisionId, StringComparison.Ordinal));
                if (revision == null ||
                    string.IsNullOrWhiteSpace(revision.ScriptingScriptId) ||
                    string.IsNullOrWhiteSpace(revision.ScriptingRevision) ||
                    string.IsNullOrWhiteSpace(revision.ScriptingDefinitionActorId) ||
                    !string.Equals(revision.ScriptingScriptId, normalizedScriptId, StringComparison.Ordinal))
                {
                    continue;
                }

                selectedTarget = target;
                selectedRevision = revision;

                if (string.Equals(catalog.DefaultServingRevisionId, target.RevisionId, StringComparison.Ordinal))
                    break;
            }

            if (selectedTarget == null || selectedRevision == null)
                continue;

            candidates.Add(new ServiceScriptingRepublishCandidateSnapshot(
                identity.Clone(),
                selectedTarget.RevisionId ?? string.Empty,
                selectedTarget.DeploymentId ?? string.Empty,
                new ServiceRevisionScriptingSnapshot(
                    selectedRevision.ScriptingScriptId,
                    selectedRevision.ScriptingRevision,
                    selectedRevision.ScriptingDefinitionActorId,
                    selectedRevision.ScriptingSourceHash ?? string.Empty),
                selectedRevision.PreparedArtifact?.Clone()));
        }

        return candidates;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{paramName} is required.", paramName);

        return normalized;
    }
}
