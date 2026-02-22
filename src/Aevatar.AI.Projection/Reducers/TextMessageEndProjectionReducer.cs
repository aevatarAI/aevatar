using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Projection.Reducers;

public sealed class TextMessageEndProjectionReducer<TReadModel, TContext>
    : ProjectionEventApplierReducerBase<TReadModel, TContext, TextMessageEndEvent>
{
    public TextMessageEndProjectionReducer(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, TextMessageEndEvent>> appliers)
        : base(appliers)
    {
    }
}
