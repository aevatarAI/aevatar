using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Chat-run projection coordinator abstraction.
/// </summary>
public interface IChatProjectionCoordinator
    : IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>>;
