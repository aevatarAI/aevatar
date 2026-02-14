using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Coordinates run-scoped CQRS projection pipeline.
/// </summary>
public interface IChatProjectionCoordinator
{
    Task InitializeAsync(ChatProjectionContext context, CancellationToken ct = default);

    Task ProjectAsync(ChatProjectionContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task CompleteAsync(
        ChatProjectionContext context,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default);
}
