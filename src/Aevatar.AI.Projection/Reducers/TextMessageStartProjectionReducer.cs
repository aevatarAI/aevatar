using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Projection.Reducers;

public sealed class TextMessageStartProjectionReducer<TReadModel, TContext>
    : ProjectionEventApplierReducerBase<TReadModel, TContext, TextMessageStartEvent>
{
    public TextMessageStartProjectionReducer(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, TextMessageStartEvent>> appliers)
        : base(appliers)
    {
    }
}
