namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic reducer contract for projecting one event envelope into one read model.
/// </summary>
public interface IProjectionEventReducer<in TReadModel, in TContext>
{
    string EventTypeUrl { get; }

    bool Reduce(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        DateTimeOffset now);
}
