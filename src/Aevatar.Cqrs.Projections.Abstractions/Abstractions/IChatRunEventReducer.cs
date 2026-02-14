using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Reduces one event type into ChatRunReport mutations.
/// </summary>
public interface IChatRunEventReducer
{
    /// <summary>Order for reducer execution; lower runs first.</summary>
    int Order { get; }

    /// <summary>Protobuf Any type url this reducer handles.</summary>
    string EventTypeUrl { get; }

    /// <summary>Applies in-place mutation to the read model.</summary>
    void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        DateTimeOffset now);
}
