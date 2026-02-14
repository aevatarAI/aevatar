using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Orchestration;

/// <summary>
/// Default CQRS projection coordinator for chat runs.
/// </summary>
public sealed class ChatProjectionCoordinator : IChatProjectionCoordinator
{
    private readonly IReadOnlyList<IChatRunProjector> _projectors;

    public ChatProjectionCoordinator(IEnumerable<IChatRunProjector> projectors) =>
        _projectors = projectors.OrderBy(x => x.Order).ToList();

    public async Task InitializeAsync(ChatProjectionContext context, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.InitializeAsync(context, ct);
    }

    public async Task ProjectAsync(ChatProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.ProjectAsync(context, envelope, ct);
    }

    public async Task CompleteAsync(
        ChatProjectionContext context,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.CompleteAsync(context, topology, ct);
    }
}
