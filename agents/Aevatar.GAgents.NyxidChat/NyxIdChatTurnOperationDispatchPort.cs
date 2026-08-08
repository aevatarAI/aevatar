using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatTurnOperationDispatchPort
{
    INyxIdChatTurnOperationDispatchSession OpenSession();
}

public interface INyxIdChatTurnOperationDispatchSession
{
    Task DispatchExecutionAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        string correlationId,
        CancellationToken ct);

    Task DispatchReconciliationAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId,
        CancellationToken ct);
}

public interface INyxIdChatTurnOperationReconciliationPort
{
    Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct);
}

public sealed class UnavailableNyxIdChatTurnOperationReconciliationPort
    : INyxIdChatTurnOperationReconciliationPort
{
    internal const string OutcomeUncertainCode = "NYXID_CHAT_OPERATION_OUTCOME_UNCERTAIN";
    private const string OutcomeUncertainMessage =
        "The external operation may have changed state and could not be reconciled.";

    public Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new NyxIdChatOperationResultSignal
        {
            Key = input.Key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = OutcomeUncertainCode,
                SafeMessage = OutcomeUncertainMessage,
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        });
    }
}

public sealed class NyxIdChatTurnOperationDispatchPort
    : INyxIdChatTurnOperationDispatchPort
{
    private const string ExecutionFailedCode = "NYXID_CHAT_OPERATION_EXECUTION_FAILED";
    private const string ExecutionFailedMessage = "The operation could not be completed.";

    private readonly INyxIdChatTurnOperationExecutor _executor;
    private readonly INyxIdChatTurnOperationReconciliationPort _reconciliationPort;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdChatTurnOperationDispatchPort> _logger;

    public NyxIdChatTurnOperationDispatchPort(
        INyxIdChatTurnOperationExecutor executor,
        INyxIdChatTurnOperationReconciliationPort reconciliationPort,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider,
        ILogger<NyxIdChatTurnOperationDispatchPort> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _reconciliationPort = reconciliationPort ??
                              throw new ArgumentNullException(nameof(reconciliationPort));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public INyxIdChatTurnOperationDispatchSession OpenSession() => new Session(this);

    private Task DispatchExecutionAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnActorId);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();

        var frozenCommand = command.Clone();
        _ = Task.Run(
            () => ExecuteAndSignalAsync(
                turnActorId,
                frozenCommand,
                session,
                correlationId),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task DispatchReconciliationAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnActorId);
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var frozenInput = input.Clone();
        _ = Task.Run(
            () => ReconcileAndSignalAsync(turnActorId, frozenInput, correlationId),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ExecuteAndSignalAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        string correlationId)
    {
        NyxIdChatOperationResultSignal result;
        try
        {
            var execution = await _executor.ExecuteAsync(
                    command,
                    session,
                    (progress, token) => DispatchAsync(
                        turnActorId,
                        $"{command.Key.OperationId}:execution-progress:{progress.Sequence}",
                        new NyxIdChatTurnOperationExecutionProgressSignal
                        {
                            Progress = progress.Clone(),
                        },
                        correlationId,
                        token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            result = execution.Result?.Clone() ?? ExecutionFailure(command);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "NyxIdChat background operation failed: turnActor={TurnActorId} operation={OperationId}",
                turnActorId,
                command.Key.OperationId);
            result = ExecutionFailure(command);
        }

        await DispatchCompletionAsync(
                turnActorId,
                result,
                NyxIdChatTurnOperationCompletionSource.Execution,
                correlationId)
            .ConfigureAwait(false);
    }

    private async Task ReconcileAndSignalAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId)
    {
        NyxIdChatOperationResultSignal result;
        try
        {
            result = (await _reconciliationPort
                    .ReconcileAsync(input, CancellationToken.None)
                    .ConfigureAwait(false))?.Clone() ?? ReconciliationFailure(input);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "NyxIdChat operation reconciliation failed: turnActor={TurnActorId} operation={OperationId}",
                turnActorId,
                input.Key?.OperationId);
            result = ReconciliationFailure(input);
        }

        await DispatchCompletionAsync(
                turnActorId,
                result,
                NyxIdChatTurnOperationCompletionSource.Reconciliation,
                correlationId)
            .ConfigureAwait(false);
    }

    private Task DispatchCompletionAsync(
        string turnActorId,
        NyxIdChatOperationResultSignal result,
        NyxIdChatTurnOperationCompletionSource source,
        string correlationId) =>
        DispatchAsync(
            turnActorId,
            $"{result.Key?.OperationId}:operation-completion:{source}",
            new NyxIdChatTurnOperationExecutionCompletedSignal
            {
                Result = result.Clone(),
                Source = source,
            },
            correlationId,
            CancellationToken.None);

    private async Task DispatchAsync(
        string actorId,
        string envelopeId,
        IMessage payload,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            await _actorDispatchPort.DispatchAsync(
                    actorId,
                    new EventEnvelope
                    {
                        Id = envelopeId,
                        Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                        Payload = Any.Pack(payload),
                        Route = new EnvelopeRoute
                        {
                            Direct = new DirectRoute { TargetActorId = actorId },
                        },
                        Propagation = new EnvelopePropagation
                        {
                            CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                                ? payload.Descriptor.FullName
                                : correlationId,
                        },
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "NyxIdChat background signal dispatch failed: actor={ActorId} envelope={EnvelopeId}",
                actorId,
                envelopeId);
        }
    }

    private static NyxIdChatOperationResultSignal ExecutionFailure(
        NyxIdChatOperationDispatchCommand command) => new()
    {
        Key = command.Key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = ExecutionFailedCode,
            SafeMessage = ExecutionFailedMessage,
            ExternalEffect = MayDispatchExternalEffect(command)
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied,
        },
    };

    private static NyxIdChatOperationResultSignal ReconciliationFailure(
        NyxIdChatTurnOperationReconciliationInput input) => new()
    {
        Key = input.Key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode,
            SafeMessage = "The external operation may have changed state and could not be reconciled.",
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        },
    };

    internal static bool MayDispatchExternalEffect(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.MayChangeExternalState == true ||
        (command.ToolApprovalContinuation is
         {
             Approved: true,
             MayChangeExternalState: true,
         }) ||
        command.PlanGateContinuation?.MayChangeExternalState == true;

    private sealed class Session(NyxIdChatTurnOperationDispatchPort owner)
        : INyxIdChatTurnOperationDispatchSession
    {
        private readonly NyxIdChatTransientExecutionSession _executionSession = new();

        public Task DispatchExecutionAsync(
            string turnActorId,
            NyxIdChatOperationDispatchCommand command,
            string correlationId,
            CancellationToken ct) =>
            owner.DispatchExecutionAsync(
                turnActorId,
                command,
                _executionSession,
                correlationId,
                ct);

        public Task DispatchReconciliationAsync(
            string turnActorId,
            NyxIdChatTurnOperationReconciliationInput input,
            string correlationId,
            CancellationToken ct) =>
            owner.DispatchReconciliationAsync(
                turnActorId,
                input,
                correlationId,
                ct);
    }
}
