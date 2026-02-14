using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Chat-run reducer abstraction.
/// </summary>
public interface IChatRunEventReducer
    : IProjectionEventReducer<ChatRunReport, ChatProjectionContext>;
