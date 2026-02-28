using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Projection.ReadModels;

namespace Aevatar.DynamicRuntime.Projection;

public sealed class ProjectionBackedDynamicRuntimeReadStore : IDynamicRuntimeReadStore
{
    private readonly IProjectionDocumentStore<DynamicRuntimeImageReadModel, string> _images;
    private readonly IProjectionDocumentStore<DynamicRuntimeStackReadModel, string> _stacks;
    private readonly IProjectionDocumentStore<DynamicRuntimeComposeServiceReadModel, string> _composeServices;
    private readonly IProjectionDocumentStore<DynamicRuntimeComposeEventReadModel, string> _composeEvents;
    private readonly IProjectionDocumentStore<DynamicRuntimeServiceDefinitionReadModel, string> _services;
    private readonly IProjectionDocumentStore<DynamicRuntimeContainerReadModel, string> _containers;
    private readonly IProjectionDocumentStore<DynamicRuntimeRunReadModel, string> _runs;
    private readonly IProjectionDocumentStore<DynamicRuntimeBuildJobReadModel, string> _buildJobs;
    private readonly IProjectionDocumentStore<DynamicRuntimeScriptReadModelDefinitionReadModel, string> _scriptReadModelDefinitions;
    private readonly IProjectionDocumentStore<DynamicRuntimeScriptReadModelRelationReadModel, string> _scriptReadModelRelations;
    private readonly IProjectionDocumentStore<DynamicRuntimeScriptReadModelDocumentReadModel, string> _scriptReadModelDocuments;

    public ProjectionBackedDynamicRuntimeReadStore(
        IProjectionDocumentStore<DynamicRuntimeImageReadModel, string> images,
        IProjectionDocumentStore<DynamicRuntimeStackReadModel, string> stacks,
        IProjectionDocumentStore<DynamicRuntimeComposeServiceReadModel, string> composeServices,
        IProjectionDocumentStore<DynamicRuntimeComposeEventReadModel, string> composeEvents,
        IProjectionDocumentStore<DynamicRuntimeServiceDefinitionReadModel, string> services,
        IProjectionDocumentStore<DynamicRuntimeContainerReadModel, string> containers,
        IProjectionDocumentStore<DynamicRuntimeRunReadModel, string> runs,
        IProjectionDocumentStore<DynamicRuntimeBuildJobReadModel, string> buildJobs,
        IProjectionDocumentStore<DynamicRuntimeScriptReadModelDefinitionReadModel, string> scriptReadModelDefinitions,
        IProjectionDocumentStore<DynamicRuntimeScriptReadModelRelationReadModel, string> scriptReadModelRelations,
        IProjectionDocumentStore<DynamicRuntimeScriptReadModelDocumentReadModel, string> scriptReadModelDocuments)
    {
        _images = images;
        _stacks = stacks;
        _composeServices = composeServices;
        _composeEvents = composeEvents;
        _services = services;
        _containers = containers;
        _runs = runs;
        _buildJobs = buildJobs;
        _scriptReadModelDefinitions = scriptReadModelDefinitions;
        _scriptReadModelRelations = scriptReadModelRelations;
        _scriptReadModelDocuments = scriptReadModelDocuments;
    }

    public Task UpsertImageAsync(ImageSnapshot snapshot, CancellationToken ct = default)
        => _images.UpsertAsync(new DynamicRuntimeImageReadModel(
            Id: snapshot.ImageName,
            ImageName: snapshot.ImageName,
            Tags: snapshot.Tags,
            Digests: snapshot.Digests), ct);

    public Task UpsertStackAsync(StackSnapshot snapshot, CancellationToken ct = default)
        => _stacks.UpsertAsync(new DynamicRuntimeStackReadModel(
            Id: snapshot.StackId,
            StackId: snapshot.StackId,
            ComposeSpecDigest: snapshot.ComposeSpecDigest,
            ComposeYaml: snapshot.ComposeYaml,
            DesiredGeneration: snapshot.DesiredGeneration,
            ObservedGeneration: snapshot.ObservedGeneration,
            ReconcileStatus: snapshot.ReconcileStatus), ct);

    public Task UpsertComposeServiceAsync(ComposeServiceSnapshot snapshot, CancellationToken ct = default)
        => _composeServices.UpsertAsync(new DynamicRuntimeComposeServiceReadModel(
            Id: BuildComposeServiceId(snapshot.StackId, snapshot.ServiceName),
            StackId: snapshot.StackId,
            ServiceName: snapshot.ServiceName,
            ImageRef: snapshot.ImageRef,
            ReplicasDesired: snapshot.ReplicasDesired,
            ReplicasReady: snapshot.ReplicasReady,
            ServiceMode: snapshot.ServiceMode.ToString(),
            Generation: snapshot.Generation,
            RolloutStatus: snapshot.RolloutStatus), ct);

    public Task AppendComposeEventAsync(ComposeEventSnapshot snapshot, CancellationToken ct = default)
        => _composeEvents.UpsertAsync(new DynamicRuntimeComposeEventReadModel(
            Id: BuildComposeEventId(snapshot),
            StackId: snapshot.StackId,
            Generation: snapshot.Generation,
            EventType: snapshot.EventType,
            Details: snapshot.Details,
            OccurredAtUtc: snapshot.OccurredAtUtc), ct);

    public Task UpsertServiceDefinitionAsync(ServiceDefinitionSnapshot snapshot, CancellationToken ct = default)
        => _services.UpsertAsync(new DynamicRuntimeServiceDefinitionReadModel(
            Id: snapshot.ServiceId,
            ServiceId: snapshot.ServiceId,
            Version: snapshot.Version,
            Status: snapshot.Status.ToString(),
            ScriptCode: snapshot.ScriptCode,
            EntrypointType: snapshot.EntrypointType,
            ServiceMode: snapshot.ServiceMode.ToString(),
            PublicEndpoints: snapshot.PublicEndpoints,
            EventSubscriptions: snapshot.EventSubscriptions,
            CapabilitiesHash: snapshot.CapabilitiesHash,
            UpdatedAtUtc: snapshot.UpdatedAtUtc,
            CustomState: snapshot.CustomState?.Clone()), ct);

    public Task UpsertContainerAsync(ContainerSnapshot snapshot, CancellationToken ct = default)
        => _containers.UpsertAsync(new DynamicRuntimeContainerReadModel(
            Id: snapshot.ContainerId,
            ContainerId: snapshot.ContainerId,
            StackId: snapshot.StackId,
            ServiceName: snapshot.ServiceName,
            ServiceId: snapshot.ServiceId,
            ImageDigest: snapshot.ImageDigest,
            Status: snapshot.Status,
            RoleActorId: snapshot.RoleActorId), ct);

    public Task UpsertRunAsync(RunSnapshot snapshot, CancellationToken ct = default)
        => _runs.UpsertAsync(new DynamicRuntimeRunReadModel(
            Id: snapshot.RunId,
            RunId: snapshot.RunId,
            ContainerId: snapshot.ContainerId,
            ServiceId: snapshot.ServiceId,
            Status: snapshot.Status,
            Result: snapshot.Result,
            Error: snapshot.Error,
            CancellationReason: snapshot.CancellationReason), ct);

    public Task UpsertBuildJobAsync(BuildJobSnapshot snapshot, CancellationToken ct = default)
        => _buildJobs.UpsertAsync(new DynamicRuntimeBuildJobReadModel(
            Id: snapshot.BuildJobId,
            BuildJobId: snapshot.BuildJobId,
            StackId: snapshot.StackId,
            ServiceName: snapshot.ServiceName,
            SourceBundleDigest: snapshot.SourceBundleDigest,
            BuildPlanDigest: snapshot.BuildPlanDigest,
            PolicyDecision: snapshot.PolicyDecision,
            ResultImageDigest: snapshot.ResultImageDigest,
            Status: snapshot.Status,
            RequiresManualApproval: snapshot.RequiresManualApproval,
            RequestedByAgentId: snapshot.RequestedByAgentId), ct);

    public Task UpsertScriptReadModelDefinitionAsync(ScriptReadModelDefinitionSnapshot snapshot, CancellationToken ct = default)
        => _scriptReadModelDefinitions.UpsertAsync(new DynamicRuntimeScriptReadModelDefinitionReadModel(
            Id: BuildScriptReadModelDefinitionId(snapshot.ServiceId, snapshot.ReadModelName),
            ServiceId: snapshot.ServiceId,
            ReadModelName: snapshot.ReadModelName,
            KeyField: snapshot.KeyField,
            Fields: snapshot.Fields,
            Indexes: snapshot.Indexes,
            UpdatedAtUtc: snapshot.UpdatedAtUtc), ct);

    public Task UpsertScriptReadModelRelationAsync(ScriptReadModelRelationSnapshot snapshot, CancellationToken ct = default)
        => _scriptReadModelRelations.UpsertAsync(new DynamicRuntimeScriptReadModelRelationReadModel(
            Id: BuildScriptReadModelRelationId(snapshot.ServiceId, snapshot.RelationName),
            ServiceId: snapshot.ServiceId,
            RelationName: snapshot.RelationName,
            FromReadModel: snapshot.FromReadModel,
            ToReadModel: snapshot.ToReadModel,
            FromKeyField: snapshot.FromKeyField,
            ToKeyField: snapshot.ToKeyField,
            UpdatedAtUtc: snapshot.UpdatedAtUtc), ct);

    public Task UpsertScriptReadModelDocumentAsync(ScriptReadModelDocumentSnapshot snapshot, CancellationToken ct = default)
        => _scriptReadModelDocuments.UpsertAsync(new DynamicRuntimeScriptReadModelDocumentReadModel(
            Id: BuildScriptReadModelDocumentId(snapshot.ServiceId, snapshot.ReadModelName, snapshot.DocumentId),
            ServiceId: snapshot.ServiceId,
            ReadModelName: snapshot.ReadModelName,
            DocumentId: snapshot.DocumentId,
            Document: snapshot.Document.Clone(),
            IndexValues: snapshot.IndexValues.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal),
            UpdatedAtUtc: snapshot.UpdatedAtUtc), ct);

    public async Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default)
    {
        var model = await _images.GetAsync(imageName, ct);
        return model == null
            ? null
            : new ImageSnapshot(model.ImageName, model.Tags, model.Digests);
    }

    public async Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default)
    {
        var model = await _stacks.GetAsync(stackId, ct);
        return model == null
            ? null
            : new StackSnapshot(
                model.StackId,
                model.ComposeSpecDigest,
                model.ComposeYaml,
                model.DesiredGeneration,
                model.ObservedGeneration,
                model.ReconcileStatus);
    }

    public async Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default)
    {
        var models = await _composeServices.ListAsync(10_000, ct);
        return models
            .Where(model => string.Equals(model.StackId, stackId, StringComparison.Ordinal))
            .OrderBy(model => model.ServiceName, StringComparer.Ordinal)
            .Select(model => new ComposeServiceSnapshot(
                model.StackId,
                model.ServiceName,
                model.ImageRef,
                model.ReplicasDesired,
                model.ReplicasReady,
                ParseServiceMode(model.ServiceMode),
                model.Generation,
                model.RolloutStatus))
            .ToArray();
    }

    public async Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default)
    {
        var models = await _composeEvents.ListAsync(10_000, ct);
        return models
            .Where(model => string.Equals(model.StackId, stackId, StringComparison.Ordinal))
            .OrderBy(model => model.OccurredAtUtc)
            .Select(model => new ComposeEventSnapshot(
                model.StackId,
                model.Generation,
                model.EventType,
                model.Details,
                model.OccurredAtUtc))
            .ToArray();
    }

    public async Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default)
    {
        var model = await _services.GetAsync(serviceId, ct);
        return model == null
            ? null
            : new ServiceDefinitionSnapshot(
                model.ServiceId,
                model.Version,
                System.Enum.TryParse<DynamicServiceStatus>(model.Status, true, out var status) ? status : DynamicServiceStatus.Inactive,
                model.ScriptCode,
                model.EntrypointType,
                ParseServiceMode(model.ServiceMode),
                model.PublicEndpoints,
                model.EventSubscriptions,
                model.CapabilitiesHash,
                model.UpdatedAtUtc,
                model.CustomState?.Clone());
    }

    public async Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default)
    {
        var model = await _containers.GetAsync(containerId, ct);
        return model == null
            ? null
            : new ContainerSnapshot(
                model.ContainerId,
                model.StackId,
                model.ServiceName,
                model.ServiceId,
                model.ImageDigest,
                model.Status,
                model.RoleActorId);
    }

    public async Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default)
    {
        var models = await _runs.ListAsync(10_000, ct);
        return models
            .Where(model => string.Equals(model.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(model => model.RunId, StringComparer.Ordinal)
            .Select(model => new RunSnapshot(
                model.RunId,
                model.ContainerId,
                model.ServiceId,
                model.Status,
                model.Result,
                model.Error,
                model.CancellationReason))
            .ToArray();
    }

    public async Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        var model = await _runs.GetAsync(runId, ct);
        return model == null
            ? null
            : new RunSnapshot(
                model.RunId,
                model.ContainerId,
                model.ServiceId,
                model.Status,
                model.Result,
                model.Error,
                model.CancellationReason);
    }

    public async Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default)
    {
        var model = await _buildJobs.GetAsync(buildJobId, ct);
        return model == null
            ? null
            : new BuildJobSnapshot(
                model.BuildJobId,
                model.StackId,
                model.ServiceName,
                model.SourceBundleDigest,
                model.BuildPlanDigest,
                model.PolicyDecision,
                model.ResultImageDigest,
                model.Status,
                model.RequiresManualApproval,
                model.RequestedByAgentId);
    }

    public async Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default)
    {
        var models = await _buildJobs.ListAsync(10_000, ct);
        return models
            .OrderBy(model => model.BuildJobId, StringComparer.Ordinal)
            .Select(model => new BuildJobSnapshot(
                model.BuildJobId,
                model.StackId,
                model.ServiceName,
                model.SourceBundleDigest,
                model.BuildPlanDigest,
                model.PolicyDecision,
                model.ResultImageDigest,
                model.Status,
                model.RequiresManualApproval,
                model.RequestedByAgentId))
            .ToArray();
    }

    public async Task<IReadOnlyList<ScriptReadModelDefinitionSnapshot>> GetScriptReadModelDefinitionsAsync(string serviceId, CancellationToken ct = default)
    {
        var models = await _scriptReadModelDefinitions.ListAsync(10_000, ct);
        return models
            .Where(model => string.Equals(model.ServiceId, serviceId, StringComparison.Ordinal))
            .OrderBy(model => model.ReadModelName, StringComparer.Ordinal)
            .Select(model => new ScriptReadModelDefinitionSnapshot(
                model.ServiceId,
                model.ReadModelName,
                model.KeyField,
                model.Fields,
                model.Indexes,
                model.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<IReadOnlyList<ScriptReadModelRelationSnapshot>> GetScriptReadModelRelationsAsync(string serviceId, CancellationToken ct = default)
    {
        var models = await _scriptReadModelRelations.ListAsync(10_000, ct);
        return models
            .Where(model => string.Equals(model.ServiceId, serviceId, StringComparison.Ordinal))
            .OrderBy(model => model.RelationName, StringComparer.Ordinal)
            .Select(model => new ScriptReadModelRelationSnapshot(
                model.ServiceId,
                model.RelationName,
                model.FromReadModel,
                model.ToReadModel,
                model.FromKeyField,
                model.ToKeyField,
                model.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<IReadOnlyList<ScriptReadModelDocumentSnapshot>> GetScriptReadModelDocumentsAsync(string serviceId, string readModelName, CancellationToken ct = default)
    {
        var models = await _scriptReadModelDocuments.ListAsync(10_000, ct);
        return models
            .Where(model =>
                string.Equals(model.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(model.ReadModelName, readModelName, StringComparison.Ordinal))
            .OrderBy(model => model.DocumentId, StringComparer.Ordinal)
            .Select(model => new ScriptReadModelDocumentSnapshot(
                model.ServiceId,
                model.ReadModelName,
                model.DocumentId,
                model.Document.Clone(),
                model.IndexValues.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal),
                model.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<ScriptReadModelDocumentSnapshot?> GetScriptReadModelDocumentAsync(string serviceId, string readModelName, string documentId, CancellationToken ct = default)
    {
        var model = await _scriptReadModelDocuments.GetAsync(BuildScriptReadModelDocumentId(serviceId, readModelName, documentId), ct);
        return model == null
            ? null
            : new ScriptReadModelDocumentSnapshot(
                model.ServiceId,
                model.ReadModelName,
                model.DocumentId,
                model.Document.Clone(),
                model.IndexValues.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal),
                model.UpdatedAtUtc);
    }

    private static string BuildComposeServiceId(string stackId, string serviceName) => $"{stackId}:{serviceName}";

    private static string BuildComposeEventId(ComposeEventSnapshot snapshot)
        => $"{snapshot.StackId}:{snapshot.Generation}:{snapshot.EventType}:{snapshot.OccurredAtUtc.Ticks}";

    private static string BuildScriptReadModelDefinitionId(string serviceId, string readModelName)
        => $"{serviceId}:{readModelName}";

    private static string BuildScriptReadModelRelationId(string serviceId, string relationName)
        => $"{serviceId}:{relationName}";

    private static string BuildScriptReadModelDocumentId(string serviceId, string readModelName, string documentId)
        => $"{serviceId}:{readModelName}:{documentId}";

    private static DynamicServiceMode ParseServiceMode(string? value)
    {
        if (string.Equals(value, nameof(DynamicServiceMode.Daemon), StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Daemon;
        if (string.Equals(value, nameof(DynamicServiceMode.Event), StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Event;
        return DynamicServiceMode.Hybrid;
    }
}
