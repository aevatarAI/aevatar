using Aevatar.CQRS.Sagas.Abstractions.Configuration;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Timeouts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Hosting;

internal sealed class SagaTimeoutDispatchHostedService : BackgroundService
{
    private readonly FileSystemSagaTimeoutStore _store;
    private readonly ISagaRuntime _runtime;
    private readonly SagaRuntimeOptions _options;
    private readonly ILogger<SagaTimeoutDispatchHostedService> _logger;

    public SagaTimeoutDispatchHostedService(
        FileSystemSagaTimeoutStore store,
        ISagaRuntime runtime,
        IOptions<SagaRuntimeOptions> options,
        ILogger<SagaTimeoutDispatchHostedService> logger)
    {
        _store = store;
        _runtime = runtime;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var intervalMs = Math.Max(_options.TimeoutDispatchIntervalMs, 100);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueTimeoutsAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saga timeout dispatch loop failed.");
            }
        }
    }

    private async Task DispatchDueTimeoutsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = _store.ListPendingPaths(_options.TimeoutDispatchBatchSize);
        foreach (var pendingPath in pending)
        {
            if (!_store.IsDueByFileName(pendingPath, now))
                break;

            if (!_store.TryClaim(pendingPath, out var processingPath))
                continue;

            SagaTimeoutScheduleRecord? record = null;
            try
            {
                record = await _store.ReadAsync(processingPath, ct);
                if (record == null)
                {
                    await _store.CompleteAsync(processingPath, ct);
                    continue;
                }

                if (record.DueAt > DateTimeOffset.UtcNow)
                {
                    await _store.RequeueAsync(processingPath, record, record.DueAt, ct);
                    continue;
                }

                var actorId = ResolveActorId(record);
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    _logger.LogWarning(
                        "Saga timeout dropped because actor id is missing. saga={SagaName}, correlationId={CorrelationId}, timeout={TimeoutName}",
                        record.SagaName,
                        record.CorrelationId,
                        record.TimeoutName);
                    await _store.CompleteAsync(processingPath, ct);
                    continue;
                }

                var envelope = SagaTimeoutEnvelope.Create(
                    record.SagaName,
                    record.CorrelationId,
                    record.TimeoutName,
                    actorId,
                    record.Metadata);

                await _runtime.ObserveAsync(actorId, envelope, ct);
                await _store.CompleteAsync(processingPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Saga timeout dispatch failed. path={Path}",
                    processingPath);

                if (record == null)
                    continue;

                var retryAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
                await _store.RequeueAsync(processingPath, record, retryAt, ct);
            }
        }
    }

    private static string ResolveActorId(SagaTimeoutScheduleRecord record)
    {
        return record.ActorId;
    }
}
