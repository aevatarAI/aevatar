using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Projection.Reducers;

public sealed class TextMessageContentProjectionReducer<TReadModel, TContext>
    : ProjectionEventApplierReducerBase<TReadModel, TContext, TextMessageContentEvent>
{
    public TextMessageContentProjectionReducer(
        IEnumerable<IProjectionEventApplier<TReadModel, TContext, TextMessageContentEvent>> appliers)
        : base(appliers)
    {
    }
}
