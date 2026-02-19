using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Projection.Reducers;

public sealed class ToolResultProjectionReducer<TReadModel, TContext>
    : ProjectionEventApplierReducerBase<TReadModel, TContext, ToolResultEvent>
{
    public ToolResultProjectionReducer(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, ToolResultEvent>> appliers)
        : base(appliers)
    {
    }
}
