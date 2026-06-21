using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunExecutor(
    ILlmRunCore runCore,
    IActorDispatchPort dispatchPort,
    ILogger<LlmRunExecutor> logger) : ILlmRunExecutor, ILlmRunExecutionService
{
    private const string PublisherId = "gagent-service.llm-run-executor";

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
                    executionRequest.RunId,
                    (recordId, command, token) => DispatchCommandAsync(
                        executionRequest.SessionActorId,
                        executionRequest.ResponseId,
                        recordId,
                        command,
                        token)),
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
                request.ResponseId,
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
        string responseId,
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
                // CorrelationId MUST be the response/session id, not the per-record id. The
                // session-observation projector only fans a committed event out to the client
                // observation hub when its CorrelationId equals the session id
                // (LlmSessionObservationSessionEventProjector); the committed event inherits
                // this inbound CorrelationId (GAgentBase publishes with ActiveInboundEnvelope as
                // source). Using recordId here filtered every chunk/tool/terminal out of the hub,
                // so the facade observation delivered nothing to the client SSE and the request
                // timed out at the gateway. Id stays recordId for dispatch idempotency.
                CorrelationId = responseId,
            },
        };
        return dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    // Dispatch-only sink (mirrors the workflow producer pattern: WorkflowRunGAgent commits
    // run facts and does NOT observe its own committed records). Each Record* command is
    // dispatched to the session actor's FIFO inbox; ordering + idempotency are owned by the
    // session actor, and the client SSE is served by the facade's single long-lived
    // LlmSessionRunObservationService.ObserveAsync. The previous per-record
    // attach/dispatch/ReadAllAsync round-trip is removed: it subscribed an Orleans-stream
    // hub just before dispatching and missed the publish off-grain, blocking the run at the
    // first chunk. See task 06-21-executor-dispatch-only-sink.
    private sealed class DispatchingLlmRunSink(
        string runId,
        Func<string, IMessage, CancellationToken, Task> dispatch) : ILlmRunSink
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

        // Dispatch the record to the session actor's FIFO inbox and continue. We await only
        // the dispatch admission (the actor accepted the command), NOT a projection round-trip:
        // the session actor commits the record idempotently and the facade observation streams
        // it to the client. Returning Continue is correct — RunLlmLoopAsync returns on its own
        // right after dispatching a terminal record, and post-terminal/cancel records are
        // idempotent no-ops on the actor. An executor exception (e.g. dispatch failure) is
        // turned into a terminal RecordLlmRunFailed by ExecuteAsync's catch.
        private async Task<LlmRunRecordDecision> DispatchAsync(string recordId, IMessage command, CancellationToken ct)
        {
            await dispatch(recordId, command, ct).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        private string NextRecordId(string kind)
        {
            var index = Interlocked.Increment(ref _recordIndex);
            return $"{runId}:{kind}:{index}";
        }

        private string ResolveRunId(string? candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? runId : candidate.Trim();
    }
}
