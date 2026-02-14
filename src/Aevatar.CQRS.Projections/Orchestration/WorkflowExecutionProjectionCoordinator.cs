using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Default CQRS projection coordinator for chat runs.
/// </summary>
public sealed class WorkflowExecutionProjectionCoordinator : IWorkflowExecutionProjectionCoordinator
{
    private readonly IReadOnlyList<IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>> _projectors;

    public WorkflowExecutionProjectionCoordinator(
        IEnumerable<IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>> projectors) =>
        _projectors = projectors.OrderBy(x => x.Order).ToList();

    public async Task InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.InitializeAsync(context, ct);
    }

    public async Task ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.ProjectAsync(context, envelope, ct);
    }

    public async Task CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.CompleteAsync(context, topology, ct);
    }
}
