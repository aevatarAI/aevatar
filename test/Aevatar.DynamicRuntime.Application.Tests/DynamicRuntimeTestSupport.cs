using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Application.Tests;

internal sealed class TestStateStore<TState> : IStateStore<TState> where TState : class
{
    private readonly ConcurrentDictionary<string, TState> _states = new(StringComparer.Ordinal);

    public Task<TState?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states.TryGetValue(agentId, out var state);
        return Task.FromResult(CloneIfPossible(state));
    }

    public Task SaveAsync(string agentId, TState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states[agentId] = CloneIfPossible(state)!;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states.TryRemove(agentId, out _);
        return Task.CompletedTask;
    }

    private static TState? CloneIfPossible(TState? state)
    {
        if (state == null)
            return null;

        if (state is IDeepCloneable<TState> typedCloneable)
            return typedCloneable.Clone();

        return state;
    }
}

internal sealed class PassthroughEventDeduplicator : IEventDeduplicator
{
    public Task<bool> TryRecordAsync(string eventId)
    {
        _ = eventId;
        return Task.FromResult(true);
    }
}

internal sealed class InMemoryDynamicRuntimeReadStore : IDynamicRuntimeReadStore
{
    private readonly ConcurrentDictionary<string, ImageSnapshot> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StackSnapshot> _stacks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ComposeServiceSnapshot>> _composeServices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ComposeEventSnapshot>> _composeEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServiceDefinitionSnapshot> _services = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerSnapshot> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunSnapshot> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BuildJobSnapshot> _buildJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptReadModelDefinitionSnapshot> _scriptReadModelDefinitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptReadModelRelationSnapshot> _scriptReadModelRelations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptReadModelDocumentSnapshot> _scriptReadModelDocuments = new(StringComparer.Ordinal);

    public Task UpsertImageAsync(ImageSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _images[snapshot.ImageName] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertStackAsync(StackSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _stacks[snapshot.StackId] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertComposeServiceAsync(ComposeServiceSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var services = _composeServices.GetOrAdd(snapshot.StackId, _ => new ConcurrentDictionary<string, ComposeServiceSnapshot>(StringComparer.Ordinal));
        services[snapshot.ServiceName] = snapshot;
        return Task.CompletedTask;
    }

    public Task AppendComposeEventAsync(ComposeEventSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var events = _composeEvents.GetOrAdd(snapshot.StackId, _ => new ConcurrentQueue<ComposeEventSnapshot>());
        events.Enqueue(snapshot);
        return Task.CompletedTask;
    }

    public Task UpsertServiceDefinitionAsync(ServiceDefinitionSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _services[snapshot.ServiceId] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertContainerAsync(ContainerSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _containers[snapshot.ContainerId] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertRunAsync(RunSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _runs[snapshot.RunId] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertBuildJobAsync(BuildJobSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _buildJobs[snapshot.BuildJobId] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertScriptReadModelDefinitionAsync(ScriptReadModelDefinitionSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _scriptReadModelDefinitions[BuildReadModelDefinitionKey(snapshot.ServiceId, snapshot.ReadModelName)] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertScriptReadModelRelationAsync(ScriptReadModelRelationSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _scriptReadModelRelations[BuildReadModelRelationKey(snapshot.ServiceId, snapshot.RelationName)] = snapshot;
        return Task.CompletedTask;
    }

    public Task UpsertScriptReadModelDocumentAsync(ScriptReadModelDocumentSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _scriptReadModelDocuments[BuildReadModelDocumentKey(snapshot.ServiceId, snapshot.ReadModelName, snapshot.DocumentId)] = snapshot;
        return Task.CompletedTask;
    }

    public Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _images.TryGetValue(imageName, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _stacks.TryGetValue(stackId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _services.TryGetValue(serviceId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_composeServices.TryGetValue(stackId, out var snapshots))
            return Task.FromResult<IReadOnlyList<ComposeServiceSnapshot>>([]);

        return Task.FromResult<IReadOnlyList<ComposeServiceSnapshot>>(snapshots.Values.OrderBy(item => item.ServiceName, StringComparer.Ordinal).ToArray());
    }

    public Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_composeEvents.TryGetValue(stackId, out var events))
            return Task.FromResult<IReadOnlyList<ComposeEventSnapshot>>([]);

        return Task.FromResult<IReadOnlyList<ComposeEventSnapshot>>(events.ToArray());
    }

    public Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _containers.TryGetValue(containerId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<ContainerSnapshot>> GetServiceContainersAsync(string stackId, string serviceName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = _containers.Values
            .Where(item =>
                string.Equals(item.StackId, stackId, StringComparison.Ordinal) &&
                string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal))
            .OrderBy(item => item.ContainerId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ContainerSnapshot>>(snapshots);
    }

    public Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _runs.TryGetValue(runId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var runs = _runs.Values.Where(item => string.Equals(item.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(item => item.RunId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<RunSnapshot>>(runs);
    }

    public Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _buildJobs.TryGetValue(buildJobId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BuildJobSnapshot>>(_buildJobs.Values.OrderBy(item => item.BuildJobId, StringComparer.Ordinal).ToArray());
    }

    public Task<IReadOnlyList<ScriptReadModelDefinitionSnapshot>> GetScriptReadModelDefinitionsAsync(string serviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = _scriptReadModelDefinitions.Values
            .Where(item => string.Equals(item.ServiceId, serviceId, StringComparison.Ordinal))
            .OrderBy(item => item.ReadModelName, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ScriptReadModelDefinitionSnapshot>>(snapshots);
    }

    public Task<IReadOnlyList<ScriptReadModelRelationSnapshot>> GetScriptReadModelRelationsAsync(string serviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = _scriptReadModelRelations.Values
            .Where(item => string.Equals(item.ServiceId, serviceId, StringComparison.Ordinal))
            .OrderBy(item => item.RelationName, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ScriptReadModelRelationSnapshot>>(snapshots);
    }

    public Task<IReadOnlyList<ScriptReadModelDocumentSnapshot>> GetScriptReadModelDocumentsAsync(string serviceId, string readModelName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = _scriptReadModelDocuments.Values
            .Where(item =>
                string.Equals(item.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(item.ReadModelName, readModelName, StringComparison.Ordinal))
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ScriptReadModelDocumentSnapshot>>(snapshots);
    }

    public Task<ScriptReadModelDocumentSnapshot?> GetScriptReadModelDocumentAsync(string serviceId, string readModelName, string documentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _scriptReadModelDocuments.TryGetValue(BuildReadModelDocumentKey(serviceId, readModelName, documentId), out var snapshot);
        return Task.FromResult(snapshot);
    }

    private static string BuildReadModelDefinitionKey(string serviceId, string readModelName)
        => $"{serviceId}:{readModelName}";

    private static string BuildReadModelRelationKey(string serviceId, string relationName)
        => $"{serviceId}:{relationName}";

    private static string BuildReadModelDocumentKey(string serviceId, string readModelName, string documentId)
        => $"{serviceId}:{readModelName}:{documentId}";
}
