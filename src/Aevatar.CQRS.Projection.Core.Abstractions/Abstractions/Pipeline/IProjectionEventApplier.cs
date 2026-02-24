namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Applies one strongly-typed event onto one read model.
/// </summary>
public interface IProjectionEventApplier<in TReadModel, in TContext, in TEvent>
{
    bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
