using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Projection.Reducers;

public sealed class ToolCallProjectionReducer<TReadModel, TContext>
    : ProjectionEventApplierReducerBase<TReadModel, TContext, ToolCallEvent>
{
    public ToolCallProjectionReducer(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, ToolCallEvent>> appliers)
        : base(appliers)
    {
    }
}
