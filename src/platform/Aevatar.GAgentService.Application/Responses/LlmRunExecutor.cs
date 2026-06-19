using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunExecutor(
    ILlmRunCore runCore,
    IActorDispatchPort dispatchPort,
    ILlmSessionObservationScopeLeasePreparationPort observationScopeLeasePreparationPort,
    ILlmSessionObservationProjectionPort observationProjectionPort,
    ILogger<LlmRunExecutor> logger,
    IOptions<ResponsesIngressOptions>? ingressOptions = null) : ILlmRunExecutor, ILlmRunExecutionService
{
    private const string PublisherId = "gagent-service.llm-run-executor";
    private const int SinkCapacity = 64;
    private static readonly TimeSpan DefaultRecordObservationTimeout = TimeSpan.FromSeconds(300);
    private readonly TimeSpan _recordObservationTimeout =
        ingressOptions?.Value?.ObservationTimeout ?? DefaultRecordObservationTimeout;

    public Task<DispatchAdmission> StartAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        ct.ThrowIfCancellationRequested();

        var sessionActorId = request.SessionActorId.Trim();
        var responseId = request.ResponseId.Trim();
        var command = request.Command.Clone();
        command.ResponseId = responseId;
        command.RunId = request.RunId.Trim();
        var startedAt = command.RequestedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var envelope = new EventEnvelope
        {
            Id = $"start-{responseId}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new RecordLlmRunStarted
            {
                Command = command,
                StartedAt = startedAt,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, sessionActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = responseId,
            },
        };
        return dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    public async Task ExecuteAsync(
        LlmRunExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        var executionRequest = new LlmRunExecutionRequest(
            request.SessionActorId.Trim(),
            request.ResponseId.Trim(),
            request.RunId.Trim(),
            request.Command.Clone(),
            request.OriginPlatform);

        try
        {
            await runCore.RunAsync(
                new LlmRunCoreRequest(
                    executionRequest.Command.Clone(),
                    executionRequest.RunId,
                    executionRequest.OriginPlatform),
                new DispatchingLlmRunSink(
                    executionRequest.SessionActorId,
                    executionRequest.ResponseId,
                    executionRequest.RunId,
                    (recordId, command, token) => DispatchCommandAsync(
                        executionRequest.SessionActorId,
                        recordId,
                        command,
                        token),
                    observationScopeLeasePreparationPort,
                    observationProjectionPort,
                    _recordObservationTimeout),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DispatchExecutorFailureAsync(executionRequest, ex).ConfigureAwait(false);
            logger.LogError(
                ex,
                "Off-turn LLM run executor failed for session actor {SessionActorId} run {RunId}.",
                executionRequest.SessionActorId,
                executionRequest.RunId);
        }
    }

    private async Task DispatchExecutorFailureAsync(
        LlmRunExecutionRequest request,
        Exception exception)
    {
        var recordId = $"{request.RunId}:executor-failed";
        try
        {
            await DispatchCommandAsync(
                request.SessionActorId,
                recordId,
                new RecordLlmRunFailed
                {
                    ResponseId = request.Command.ResponseId,
                    RunId = request.RunId,
                    RecordId = recordId,
                    FailureCode = "executor_failed",
                    FailureMessage = string.IsNullOrWhiteSpace(exception.Message)
                        ? "Off-turn LLM run executor failed."
                        : exception.Message,
                    FailedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception dispatchException)
        {
            logger.LogError(
                dispatchException,
                "Off-turn LLM run executor could not dispatch failure record for session actor {SessionActorId} run {RunId}.",
                request.SessionActorId,
                request.RunId);
        }
    }

    private Task DispatchCommandAsync(
        string sessionActorId,
        string recordId,
        IMessage command,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = recordId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, sessionActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = recordId,
            },
        };
        return dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    private sealed class DispatchingLlmRunSink(
        string sessionActorId,
        string responseId,
        string runId,
        Func<string, IMessage, CancellationToken, Task> dispatch,
        ILlmSessionObservationScopeLeasePreparationPort observationScopeLeasePreparationPort,
        ILlmSessionObservationProjectionPort observationProjectionPort,
        TimeSpan recordObservationTimeout) : ILlmRunSink
    {
        private long _recordIndex;

        public Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(observed);
            var recordId = NextRecordId("chunk");
            return DispatchAsync(
                recordId,
                new RecordLlmStreamChunkObserved
                {
                    ResponseId = observed.ResponseId,
                    RunId = ResolveRunId(observed.RunId),
                    RecordId = recordId,
                    Round = observed.Round,
                    DeltaText = observed.DeltaText ?? string.Empty,
                    ToolCallDelta = observed.ToolCallDelta?.Clone(),
                    Usage = observed.Usage?.Clone(),
                    ObservedAt = observed.ObservedAt?.Clone(),
                },
                ct);
        }

        public Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(observed);
            var recordId = NextRecordId("tool");
            return DispatchAsync(
                recordId,
                new RecordLlmToolCallObserved
                {
                    ResponseId = observed.ResponseId,
                    RunId = ResolveRunId(observed.RunId),
                    RecordId = recordId,
                    Round = observed.Round,
                    ToolCall = observed.ToolCall?.Clone(),
                    Forwarded = observed.Forwarded,
                    LocalResultJson = observed.LocalResultJson ?? string.Empty,
                    ObservedAt = observed.ObservedAt?.Clone(),
                    LocalResult = observed.LocalResult?.Clone(),
                },
                ct);
        }

        public Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(completed);
            var recordId = NextRecordId("completed");
            var command = new RecordLlmRunCompleted
            {
                ResponseId = completed.ResponseId,
                RunId = ResolveRunId(completed.RunId),
                RecordId = recordId,
                OutputText = completed.OutputText ?? string.Empty,
                Usage = completed.Usage?.Clone(),
                CompletedAt = completed.CompletedAt?.Clone(),
            };
            command.ForwardedToolCalls.AddRange(completed.ForwardedToolCalls.Select(static call => call.Clone()));
            command.ForwardedToolCallRecords.AddRange(completed.ForwardedToolCallRecords.Select(static call => call.Clone()));
            return DispatchAsync(recordId, command, ct);
        }

        public Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(failed);
            var recordId = NextRecordId("failed");
            return DispatchAsync(
                recordId,
                new RecordLlmRunFailed
                {
                    ResponseId = failed.ResponseId,
                    RunId = ResolveRunId(failed.RunId),
                    RecordId = recordId,
                    FailureCode = failed.FailureCode ?? string.Empty,
                    FailureMessage = failed.FailureMessage ?? string.Empty,
                    FailedAt = failed.FailedAt?.Clone(),
                },
                ct);
        }

        public Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(cancelled);
            var recordId = NextRecordId("cancelled");
            return DispatchAsync(
                recordId,
                new RecordLlmRunCancelled
                {
                    ResponseId = cancelled.ResponseId,
                    RunId = ResolveRunId(cancelled.RunId),
                    RecordId = recordId,
                    CancelledAt = cancelled.CancelledAt?.Clone(),
                },
                ct);
        }

        private async Task<LlmRunRecordDecision> DispatchAsync(string recordId, IMessage command, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (recordObservationTimeout > TimeSpan.Zero)
                timeoutCts.CancelAfter(recordObservationTimeout);
            var observeCt = timeoutCts.Token;
            LlmSessionObservationScopeLeasePreparation? preparation = null;
            await using var sink = new EventChannel<EventEnvelope>(SinkCapacity);
            EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>? attachment = null;

            try
            {
                preparation = await observationScopeLeasePreparationPort
                    .PrepareAsync(sessionActorId, responseId, observeCt)
                    .ConfigureAwait(false);
                if (preparation == null)
                    throw new InvalidOperationException(
                        $"LLM run record observation is unavailable for response '{responseId}'.");

                attachment = await observationProjectionPort
                    .AttachExistingResponseProjectionAsync(sessionActorId, responseId, sink, observeCt)
                    .ConfigureAwait(false);
                if (attachment == null)
                    throw new InvalidOperationException(
                        $"LLM run record observation attachment is unavailable for response '{responseId}'.");

                await dispatch(recordId, command, observeCt).ConfigureAwait(false);
                await foreach (var envelope in sink.ReadAllAsync(observeCt).ConfigureAwait(false))
                {
                    if (!TryGetObservedPayload(envelope, out var payload))
                        continue;

                    var decision = TryResolveDecision(payload, recordId);
                    if (decision.HasValue)
                    {
                        sink.Complete();
                        return decision.Value;
                    }
                }

                throw new InvalidOperationException(
                    $"LLM run record observation ended before record '{recordId}' was committed.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for LLM run record '{recordId}' to be committed.");
            }
            finally
            {
                if (attachment != null)
                {
                    await observationProjectionPort
                        .DetachLiveSinkAsync(attachment.LiveSinkLease, CancellationToken.None)
                        .ConfigureAwait(false);
                    await observationProjectionPort
                        .ReleaseActorProjectionAsync(attachment.ProjectionLease, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (preparation != null)
                {
                    await observationScopeLeasePreparationPort
                        .ReleaseAsync(preparation, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }

        private static bool TryGetObservedPayload(EventEnvelope envelope, out Any payload)
        {
            if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
            {
                var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
                if (published.StateEvent?.EventData != null)
                {
                    payload = published.StateEvent.EventData;
                    return true;
                }
            }

            if (envelope.Payload != null)
            {
                payload = envelope.Payload;
                return true;
            }

            payload = default!;
            return false;
        }

        private static LlmRunRecordDecision? TryResolveDecision(Any payload, string recordId)
        {
            if (payload.Is(LlmStreamChunkObserved.Descriptor))
                return RecordDecision(payload.Unpack<LlmStreamChunkObserved>().RecordId, recordId, false);
            if (payload.Is(LlmToolCallObserved.Descriptor))
                return RecordDecision(payload.Unpack<LlmToolCallObserved>().RecordId, recordId, false);
            if (payload.Is(LlmRunCompleted.Descriptor))
                return RecordDecision(payload.Unpack<LlmRunCompleted>().RecordId, recordId, true);
            if (payload.Is(LlmRunFailed.Descriptor))
                return RecordDecision(payload.Unpack<LlmRunFailed>().RecordId, recordId, true);
            if (payload.Is(LlmRunCancelled.Descriptor))
                return RecordDecision(payload.Unpack<LlmRunCancelled>().RecordId, recordId, true);

            return null;
        }

        private static LlmRunRecordDecision? RecordDecision(string payloadRecordId, string expectedRecordId, bool terminal) =>
            string.Equals(payloadRecordId, expectedRecordId, StringComparison.Ordinal)
                ? LlmRunRecordDecision.Continue with { StopDispatching = terminal }
                : terminal
                    ? LlmRunRecordDecision.Stop
                    : null;

        private string NextRecordId(string kind)
        {
            var index = Interlocked.Increment(ref _recordIndex);
            return $"{runId}:{kind}:{index}";
        }

        private string ResolveRunId(string? candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? runId : candidate.Trim();
    }
}
