using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Projects event envelopes into a run-scoped read model.
/// </summary>
public interface IChatRunProjector
{
    /// <summary>Order for projector execution; lower runs first.</summary>
    int Order { get; }

    /// <summary>Called once before run events are processed.</summary>
    ValueTask InitializeAsync(ChatProjectionContext context, CancellationToken ct = default);

    /// <summary>Projects a single event envelope.</summary>
    ValueTask ProjectAsync(ChatProjectionContext context, EventEnvelope envelope, CancellationToken ct = default);

    /// <summary>Called once after event processing ends.</summary>
    ValueTask CompleteAsync(
        ChatProjectionContext context,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default);
}
