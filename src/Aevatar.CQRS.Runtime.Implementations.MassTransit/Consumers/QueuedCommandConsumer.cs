using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using MassTransit;

namespace Aevatar.CQRS.Runtime.Implementations.MassTransit.Consumers;

internal sealed class QueuedCommandConsumer : IConsumer<QueuedCommandMessage>
{
    private readonly IQueuedCommandExecutor _executor;

    public QueuedCommandConsumer(IQueuedCommandExecutor executor)
    {
        _executor = executor;
    }

    public Task Consume(ConsumeContext<QueuedCommandMessage> context)
    {
        return _executor.ExecuteAsync(context.Message, context.CancellationToken);
    }
}
