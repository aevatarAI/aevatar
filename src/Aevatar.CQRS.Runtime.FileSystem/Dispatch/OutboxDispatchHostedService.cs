using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Runtime.FileSystem.Dispatch;

internal sealed class OutboxDispatchHostedService : BackgroundService
{
    private readonly IOutboxStore _outboxStore;
    private readonly IOutboxMessageDispatcher _dispatcher;
    private readonly CqrsRuntimeOptions _options;
    private readonly ILogger<OutboxDispatchHostedService> _logger;

    public OutboxDispatchHostedService(
        IOutboxStore outboxStore,
        IOutboxMessageDispatcher dispatcher,
        IOptions<CqrsRuntimeOptions> options,
        ILogger<OutboxDispatchHostedService> logger)
    {
        _outboxStore = outboxStore;
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMs = Math.Max(_options.OutboxDispatchIntervalMs, 100);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox dispatcher loop failed.");
            }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken ct)
    {
        var batchSize = Math.Max(_options.OutboxDispatchBatchSize, 1);
        var pending = await _outboxStore.ListPendingAsync(batchSize, ct);
        foreach (var message in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _dispatcher.DispatchAsync(message, ct);
                await _outboxStore.MarkDispatchedAsync(message.MessageId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Outbox message dispatch failed. messageId={MessageId}, commandId={CommandId}",
                    message.MessageId,
                    message.CommandId);
            }
        }
    }
}
