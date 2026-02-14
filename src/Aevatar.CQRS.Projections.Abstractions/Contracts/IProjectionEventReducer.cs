namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Generic reducer contract for projecting one event envelope into one read model.
/// </summary>
public interface IProjectionEventReducer<in TReadModel, in TContext>
{
    int Order { get; }

    string EventTypeUrl { get; }

    void Reduce(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        DateTimeOffset now);
}
