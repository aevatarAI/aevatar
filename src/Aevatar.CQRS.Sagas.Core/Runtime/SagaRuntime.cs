using Aevatar.CQRS.Sagas.Abstractions.Actions;
using Aevatar.CQRS.Sagas.Abstractions.Configuration;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Abstractions.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Sagas.Core.Runtime;

public sealed class SagaRuntime : ISagaRuntime
{
    private readonly IReadOnlyList<ISaga> _sagas;
    private readonly ISagaRepository _repository;
    private readonly ISagaCorrelationResolver _correlationResolver;
    private readonly ISagaCommandEmitter _commandEmitter;
    private readonly ISagaTimeoutScheduler _timeoutScheduler;
    private readonly SagaRuntimeOptions _options;
    private readonly ILogger<SagaRuntime> _logger;

    public SagaRuntime(
        IEnumerable<ISaga> sagas,
        ISagaRepository repository,
        ISagaCorrelationResolver correlationResolver,
        ISagaCommandEmitter commandEmitter,
        ISagaTimeoutScheduler timeoutScheduler,
        IOptions<SagaRuntimeOptions> options,
        ILogger<SagaRuntime> logger)
    {
        _sagas = sagas?.ToList() ?? throw new ArgumentNullException(nameof(sagas));
        _repository = repository;
        _correlationResolver = correlationResolver;
        _commandEmitter = commandEmitter;
        _timeoutScheduler = timeoutScheduler;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ObserveAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return;

        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        foreach (var saga in _sagas)
        {
            if (!await saga.CanHandleAsync(envelope, ct))
                continue;

            var correlationId = _correlationResolver.ResolveCorrelationId(saga, envelope);
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                _logger.LogDebug(
                    "Skip saga '{SagaName}' for event '{EventId}' because correlation id is missing.",
                    saga.Name,
                    envelope.Id);
                continue;
            }

            await ProcessSagaWithRetryAsync(saga, correlationId, actorId, envelope, ct);
        }
    }

    private async Task ProcessSagaWithRetryAsync(
        ISaga saga,
        string correlationId,
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(_options.ConcurrencyRetryAttempts, 1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ProcessSagaOnceAsync(saga, correlationId, actorId, envelope, ct);
                return;
            }
            catch (SagaConcurrencyException ex) when (attempt < maxAttempts)
            {
                _logger.LogDebug(
                    ex,
                    "Saga optimistic concurrency conflict. saga={SagaName}, correlationId={CorrelationId}, attempt={Attempt}/{MaxAttempts}",
                    saga.Name,
                    correlationId,
                    attempt,
                    maxAttempts);
            }
        }

        throw new SagaConcurrencyException(
            $"Saga '{saga.Name}' failed after {maxAttempts} concurrency retries. correlationId={correlationId}");
    }

    private async Task ProcessSagaOnceAsync(
        ISaga saga,
        string correlationId,
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = await _repository.LoadAsync(saga.Name, correlationId, saga.StateType, ct);
        var expectedVersion = state?.Version ?? -1;
        if (state == null)
        {
            if (!await saga.CanStartAsync(envelope, ct))
                return;

            state = saga.CreateNewState(correlationId, envelope);
            _logger.LogDebug(
                "Created saga state. saga={SagaName}, correlationId={CorrelationId}, actorId={ActorId}",
                saga.Name,
                correlationId,
                actorId);
        }

        if (state.IsCompleted)
            return;

        var collector = new SagaActionCollector(_options.MaxActionsPerEvent);
        await saga.HandleAsync(state, envelope, collector, ct);

        if (collector.ShouldComplete)
            state.IsCompleted = true;

        if (string.IsNullOrWhiteSpace(state.CorrelationId))
            state.CorrelationId = correlationId;
        if (string.IsNullOrWhiteSpace(state.SagaId))
            state.SagaId = Guid.NewGuid().ToString("N");

        state.Version = expectedVersion < 0 ? 1 : expectedVersion + 1;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await _repository.SaveAsync(saga.Name, state, saga.StateType, expectedVersion, ct);
        await DispatchActionsAsync(saga.Name, correlationId, actorId, collector.Actions, ct);
    }

    private async Task DispatchActionsAsync(
        string sagaName,
        string correlationId,
        string actorId,
        IReadOnlyList<ISagaAction> actions,
        CancellationToken ct)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case SagaEnqueueCommandAction enqueue:
                    await _commandEmitter.EnqueueAsync(enqueue, ct);
                    break;

                case SagaScheduleCommandAction schedule:
                    await _commandEmitter.ScheduleAsync(schedule, ct);
                    break;

                case SagaScheduleTimeoutAction timeout:
                    await _timeoutScheduler.ScheduleAsync(
                        sagaName,
                        correlationId,
                        actorId,
                        timeout,
                        ct);
                    break;

                case SagaCompleteAction:
                    // state was marked completed before persistence.
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported saga action type '{action.GetType().FullName}'.");
            }
        }
    }
}
