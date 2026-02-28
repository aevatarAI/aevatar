using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryDynamicRuntimeReadStore : IDynamicRuntimeReadStore
{
    private readonly ConcurrentDictionary<string, ImageSnapshot> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StackSnapshot> _stacks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerSnapshot> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunSnapshot> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BuildJobSnapshot> _buildJobs = new(StringComparer.Ordinal);

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

    public Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _containers.TryGetValue(containerId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _runs.TryGetValue(runId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _buildJobs.TryGetValue(buildJobId, out var snapshot);
        return Task.FromResult(snapshot);
    }
}
