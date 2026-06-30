using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Read-model INVENTORY query port. Enumerates the opt-in IProjectionReadModelDescriptor set (each
// registered next to its read-model's store) and assembles the inventory grouped by sink shape.
//
// Every per-read-model capture reads the materialized read-model store ONLY (through that read-model's
// document reader); it never replays event streams, rebuilds state, or touches IEventStore. A capture
// that fails for one read-model degrades to null metrics for that read-model rather than failing the
// whole inventory, so one unhealthy store does not blind the whole panel.
public sealed class ProjectionReadModelInventoryQueryPort : IProjectionReadModelInventoryQueryPort
{
    private readonly IReadOnlyList<IProjectionReadModelDescriptor> _descriptors;
    private readonly ILogger<ProjectionReadModelInventoryQueryPort> _logger;

    public ProjectionReadModelInventoryQueryPort(
        IEnumerable<IProjectionReadModelDescriptor> descriptors,
        ILogger<ProjectionReadModelInventoryQueryPort>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _descriptors = descriptors.ToList();
        _logger = logger ?? NullLogger<ProjectionReadModelInventoryQueryPort>.Instance;
    }

    public async Task<ProjectionReadModelInventory> GetInventoryAsync(CancellationToken ct = default)
    {
        var items = new List<(IProjectionReadModelDescriptor Descriptor, ProjectionReadModelInventoryItem Item)>(
            _descriptors.Count);

        foreach (var descriptor in _descriptors)
        {
            var snapshot = await CaptureAsync(descriptor, ct);
            items.Add((descriptor, new ProjectionReadModelInventoryItem(
                Name: descriptor.Name,
                Actor: descriptor.ActorKind,
                Version: snapshot.MaxStateVersion,
                Updated: snapshot.LatestUpdatedAt,
                Count: snapshot.Count)));
        }

        // Group by sink shape; within a shape keep the engine label of the first descriptor (descriptors
        // in the same shape share an engine in practice). Items sorted by name for a stable surface.
        var groups = items
            .GroupBy(static entry => entry.Descriptor.Shape)
            .OrderBy(static group => group.Key)
            .Select(static group => new ProjectionReadModelGroup(
                Shape: group.Key,
                Engine: group.Select(static entry => entry.Descriptor.Engine).First(),
                Items: group
                    .Select(static entry => entry.Item)
                    .OrderBy(static item => item.Name, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

        return new ProjectionReadModelInventory(groups);
    }

    private async Task<ProjectionReadModelInventorySnapshot> CaptureAsync(
        IProjectionReadModelDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            return await descriptor.CaptureAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unhealthy read-model store must not blind the whole inventory: degrade to null metrics
            // (honest "unknown") for this read-model and keep the rest of the inventory readable.
            _logger.LogWarning(
                ex,
                "Read-model inventory capture failed. name={Name} shape={Shape} engine={Engine}",
                descriptor.Name,
                descriptor.Shape,
                descriptor.Engine);
            return new ProjectionReadModelInventorySnapshot(Count: null, MaxStateVersion: null, LatestUpdatedAt: null);
        }
    }
}
