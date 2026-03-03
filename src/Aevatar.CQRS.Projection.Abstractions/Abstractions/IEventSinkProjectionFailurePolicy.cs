using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Event-sink specialization of sink failure policy contract.
/// </summary>
public interface IEventSinkProjectionFailurePolicy<TLease, TEvent>
    : IProjectionPortSinkFailurePolicy<TLease, IEventSink<TEvent>, TEvent>
{
}
