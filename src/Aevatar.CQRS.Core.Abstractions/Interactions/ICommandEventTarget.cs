using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandEventTarget<TEvent> : ICommandDispatchTarget
{
    IEventSink<TEvent> RequireLiveSink();
}
