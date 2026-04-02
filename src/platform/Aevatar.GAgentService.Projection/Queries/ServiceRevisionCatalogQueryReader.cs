using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
{
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _documentStore;
    private readonly bool _enabled;

    public ServiceRevisionCatalogQueryReader(
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> documentStore,
        ServiceProjectionOptions? options = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceRevisionCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceRevisionCatalogSnapshot(
            readModel.Id,
            readModel.Revisions
                .OrderByDescending(x => x.CreatedAt ?? DateTimeOffset.MinValue)
                .Select(x => new ServiceRevisionSnapshot(
                    x.RevisionId,
                    x.ImplementationKind,
                    x.Status,
                    x.ArtifactHash,
                    x.FailureReason,
                    x.Endpoints
                        .Select(y => new ServiceEndpointSnapshot(
                            y.EndpointId,
                            y.DisplayName,
                            y.Kind,
                            y.RequestTypeUrl,
                            y.ResponseTypeUrl,
                            y.Description))
                        .ToList(),
                    x.CreatedAt,
                    x.PreparedAt,
                    x.PublishedAt,
                    x.RetiredAt,
                    BuildImplementationSnapshot(x)))
                .ToList(),
            readModel.UpdatedAt,
            readModel.StateVersion,
            readModel.LastEventId);
    }

    private static ServiceRevisionImplementationSnapshot? BuildImplementationSnapshot(
        ServiceRevisionEntryReadModel revision)
    {
        var staticSnapshot =
            !string.IsNullOrWhiteSpace(revision.StaticActorTypeName) ||
            !string.IsNullOrWhiteSpace(revision.StaticPreferredActorId)
                ? new ServiceRevisionStaticSnapshot(
                    revision.StaticActorTypeName ?? string.Empty,
                    revision.StaticPreferredActorId ?? string.Empty)
                : null;

        var scriptingSnapshot =
            !string.IsNullOrWhiteSpace(revision.ScriptingScriptId) ||
            !string.IsNullOrWhiteSpace(revision.ScriptingRevision) ||
            !string.IsNullOrWhiteSpace(revision.ScriptingDefinitionActorId) ||
            !string.IsNullOrWhiteSpace(revision.ScriptingSourceHash)
                ? new ServiceRevisionScriptingSnapshot(
                    revision.ScriptingScriptId ?? string.Empty,
                    revision.ScriptingRevision ?? string.Empty,
                    revision.ScriptingDefinitionActorId ?? string.Empty,
                    revision.ScriptingSourceHash ?? string.Empty)
                : null;

        var workflowSnapshot =
            !string.IsNullOrWhiteSpace(revision.WorkflowName) ||
            !string.IsNullOrWhiteSpace(revision.WorkflowDefinitionActorId) ||
            revision.WorkflowInlineWorkflowCount > 0
                ? new ServiceRevisionWorkflowSnapshot(
                    revision.WorkflowName ?? string.Empty,
                    revision.WorkflowDefinitionActorId ?? string.Empty,
                    revision.WorkflowInlineWorkflowCount)
                : null;

        if (staticSnapshot == null && scriptingSnapshot == null && workflowSnapshot == null)
            return null;

        return new ServiceRevisionImplementationSnapshot(
            staticSnapshot,
            scriptingSnapshot,
            workflowSnapshot);
    }
}
