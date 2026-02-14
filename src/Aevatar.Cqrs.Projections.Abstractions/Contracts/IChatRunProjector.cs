using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Chat-run projector abstraction.
/// </summary>
public interface IChatRunProjector
    : IProjectionProjector<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>>;
