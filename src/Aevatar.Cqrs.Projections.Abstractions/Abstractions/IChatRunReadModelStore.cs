using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Read-model store for chat run projections.
/// </summary>
public interface IChatRunReadModelStore
    : IProjectionReadModelStore<ChatRunReport, string>;
