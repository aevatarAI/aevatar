using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Runtime.FileSystem.Dispatch;

internal sealed class LoggingOutboxMessageDispatcher : IOutboxMessageDispatcher
{
    private readonly ILogger<LoggingOutboxMessageDispatcher> _logger;

    public LoggingOutboxMessageDispatcher(ILogger<LoggingOutboxMessageDispatcher> logger)
    {
        _logger = logger;
    }

    public Task DispatchAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Outbox dispatched. messageId={MessageId}, commandId={CommandId}, type={MessageType}",
            message.MessageId,
            message.CommandId,
            message.MessageType);

        return Task.CompletedTask;
    }
}
