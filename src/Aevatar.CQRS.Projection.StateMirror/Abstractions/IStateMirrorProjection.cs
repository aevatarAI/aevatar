namespace Aevatar.CQRS.Projection.StateMirror.Abstractions;

public interface IStateMirrorProjection<TState, TReadModel>
    where TState : class
    where TReadModel : class
{
    TReadModel Project(TState state);
}
