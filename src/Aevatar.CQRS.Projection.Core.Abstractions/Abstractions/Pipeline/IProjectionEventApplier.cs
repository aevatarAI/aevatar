using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Applies one strongly-typed event onto one read model.
/// </summary>
public interface IProjectionEventApplier<in TReadModel, in TContext, in TEvent>
    where TReadModel : class
    where TContext : IProjectionMaterializationContext
    where TEvent : class, IMessage
{
    bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        TEvent evt,
        DateTimeOffset now);
}
