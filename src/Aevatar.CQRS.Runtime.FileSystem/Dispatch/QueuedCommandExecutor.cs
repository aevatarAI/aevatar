using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.Abstractions.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Runtime.FileSystem.Dispatch;

internal sealed class QueuedCommandExecutor : IQueuedCommandExecutor
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICommandPayloadSerializer _serializer;
    private readonly ICommandStateStore _commandStates;
    private readonly IInboxStore _inbox;
    private readonly IOutboxStore _outbox;
    private readonly IDeadLetterStore _deadLetters;
    private readonly CqrsRuntimeOptions _options;
    private readonly ILogger<QueuedCommandExecutor> _logger;

    public QueuedCommandExecutor(
        ICommandDispatcher dispatcher,
        ICommandPayloadSerializer serializer,
        ICommandStateStore commandStates,
        IInboxStore inbox,
        IOutboxStore outbox,
        IDeadLetterStore deadLetters,
        IOptions<CqrsRuntimeOptions> options,
        ILogger<QueuedCommandExecutor> logger)
    {
        _dispatcher = dispatcher;
        _serializer = serializer;
        _commandStates = commandStates;
        _inbox = inbox;
        _outbox = outbox;
        _deadLetters = deadLetters;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(QueuedCommandMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        var commandId = message.Envelope.CommandId;
        if (!await _inbox.TryAcquireAsync(commandId, ct))
        {
            await UpsertStateAsync(message, CommandExecutionStatus.DuplicateIgnored, message.Attempt, string.Empty, ct);
            return;
        }

        var maxRetryAttempts = Math.Max(_options.MaxRetryAttempts, 0);
        var retryBaseDelayMs = Math.Max(_options.RetryBaseDelayMs, 50);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= maxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var currentStatus = attempt == 0 ? CommandExecutionStatus.Running : CommandExecutionStatus.Retrying;
                await UpsertStateAsync(message, currentStatus, attempt, string.Empty, ct);

                var commandType = Type.GetType(message.CommandType, throwOnError: false);
                if (commandType == null)
                    throw new InvalidOperationException($"Command type '{message.CommandType}' is not resolvable.");

                var command = _serializer.Deserialize(message.PayloadJson, commandType);
                await _dispatcher.DispatchAsync(message.Envelope, command, ct);

                await _inbox.MarkCompletedAsync(commandId, ct);
                await UpsertStateAsync(message, CommandExecutionStatus.Succeeded, attempt, string.Empty, ct, completed: true);
                await _outbox.AppendAsync(new OutboxMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    CommandId = commandId,
                    MessageType = "command.completed",
                    PayloadJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);
                return;
            }
            catch (Exception ex) when (attempt < maxRetryAttempts)
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "CQRS command retry scheduled. commandId={CommandId}, attempt={Attempt}/{MaxAttempts}",
                    commandId,
                    attempt + 1,
                    maxRetryAttempts + 1);

                var delayMs = retryBaseDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        var finalError = lastError?.Message ?? "Unknown command processing error.";
        await _inbox.MarkFailedAsync(commandId, finalError, ct);
        await UpsertStateAsync(message, CommandExecutionStatus.DeadLettered, maxRetryAttempts, finalError, ct, completed: true);
        await _deadLetters.AppendAsync(new DeadLetterMessage
        {
            DeadLetterId = Guid.NewGuid().ToString("N"),
            CommandId = commandId,
            CommandType = message.CommandType,
            PayloadJson = message.PayloadJson,
            Attempt = maxRetryAttempts,
            Error = finalError,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private async Task UpsertStateAsync(
        QueuedCommandMessage message,
        CommandExecutionStatus status,
        int attempt,
        string error,
        CancellationToken ct,
        bool completed = false)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _commandStates.GetAsync(message.Envelope.CommandId, ct);
        var acceptedAt = existing?.AcceptedAt ?? message.Envelope.EnqueuedAt;

        await _commandStates.UpsertAsync(new CommandExecutionState
        {
            CommandId = message.Envelope.CommandId,
            CorrelationId = message.Envelope.CorrelationId,
            Target = message.Envelope.Target,
            CommandType = message.CommandType,
            Status = status,
            Attempt = attempt,
            AcceptedAt = acceptedAt,
            UpdatedAt = now,
            CompletedAt = completed ? now : null,
            Error = error,
            Metadata = new Dictionary<string, string>(message.Envelope.Metadata, StringComparer.Ordinal),
        }, ct);
    }
}
