using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmSessionRunObservationService(
    ILlmSessionObservationScopeLeasePreparationPort observationScopeLeasePreparationPort,
    ILlmSessionObservationProjectionPort observationProjectionPort) : ILlmSessionRunObservationService
{
    private const int SinkCapacity = 64;

    public async Task<LlmSessionRunObservedResult> ObserveAsync(
        LlmSessionRunObservationRequest request,
        Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DispatchAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        using var observationTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout > TimeSpan.Zero)
            observationTimeoutCts.CancelAfter(request.Timeout);
        var observeCt = observationTimeoutCts.Token;

        LlmSessionObservationScopeLeasePreparation? preparation = null;
        await using var sink = new EventChannel<EventEnvelope>(SinkCapacity);
        EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>? attachment = null;

        try
        {
            preparation = await observationScopeLeasePreparationPort
                .PrepareAsync(request.ActorId, request.ResponseId, observeCt)
                .ConfigureAwait(false);
            if (preparation == null)
            {
                return Error(
                    LlmSessionRunObservedTerminalKind.ObservationUnavailable,
                    503,
                    "observation_unavailable",
                    "LLM run observation is unavailable.");
            }

            attachment = await observationProjectionPort
                .AttachExistingResponseProjectionAsync(request.ActorId, request.ResponseId, sink, observeCt)
                .ConfigureAwait(false);
            if (attachment == null)
            {
                return Error(
                    LlmSessionRunObservedTerminalKind.ObservationUnavailable,
                    503,
                    "observation_unavailable",
                    "LLM run observation attachment is unavailable.");
            }

            var admission = await request.DispatchAsync(observeCt).ConfigureAwait(false);
            var accumulator = new LlmSessionRunObservationAccumulator(request.ResponseId);
            await foreach (var envelope in sink.ReadAllAsync(observeCt).ConfigureAwait(false))
            {
                if (!TryGetObservedPayload(envelope, out var payload))
                    continue;

                if (payload.Is(LlmRunStartedEvent.Descriptor))
                {
                    continue;
                }

                if (payload.Is(LlmStreamChunkObserved.Descriptor))
                {
                    var delta = accumulator.ObserveChunk(payload.Unpack<LlmStreamChunkObserved>());
                    if (delta != null && onDelta != null)
                        await onDelta(delta, ct).ConfigureAwait(false);
                    continue;
                }

                if (payload.Is(LlmToolCallObserved.Descriptor))
                {
                    accumulator.ObserveToolCall(payload.Unpack<LlmToolCallObserved>());
                    continue;
                }

                if (payload.Is(LlmRunCompleted.Descriptor))
                {
                    accumulator.ObserveCompleted(payload.Unpack<LlmRunCompleted>());
                    sink.Complete();
                    return new LlmSessionRunObservedResult(admission, accumulator.BuildCompletion(), null);
                }

                if (payload.Is(LlmRunFailed.Descriptor))
                {
                    var failed = payload.Unpack<LlmRunFailed>();
                    sink.Complete();
                    return Error(
                        LlmSessionRunObservedTerminalKind.Failed,
                        500,
                        string.IsNullOrWhiteSpace(failed.FailureCode) ? "llm_run_failed" : failed.FailureCode,
                        string.IsNullOrWhiteSpace(failed.FailureMessage) ? "LLM run failed." : failed.FailureMessage,
                        admission);
                }

                if (payload.Is(LlmRunCancelled.Descriptor))
                {
                    sink.Complete();
                    return Error(
                        LlmSessionRunObservedTerminalKind.Cancelled,
                        409,
                        "run_cancelled",
                        "LLM run was cancelled.",
                        admission);
                }
            }

            return Error(
                LlmSessionRunObservedTerminalKind.ObservationUnavailable,
                503,
                "observation_unavailable",
                "LLM run observation ended without a terminal event.",
                admission);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && observationTimeoutCts.IsCancellationRequested)
        {
            sink.Complete();
            return Error(
                LlmSessionRunObservedTerminalKind.TimedOut,
                504,
                "response_timeout",
                $"Timed out waiting {FormatTimeout(request.Timeout)} for the LLM run to emit a terminal event.");
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

    private static bool TryGetObservedPayload(EventEnvelope envelope, out Google.Protobuf.WellKnownTypes.Any payload)
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

    private static LlmSessionRunObservedResult Error(
        LlmSessionRunObservedTerminalKind kind,
        int statusCode,
        string code,
        string message,
        DispatchAdmission? admission = null) =>
        new(admission, null, new LlmSessionRunObservedError(kind, statusCode, code, message));

    private static string FormatTimeout(TimeSpan timeout)
    {
        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero;
        return effectiveTimeout >= TimeSpan.FromSeconds(1)
            ? $"{effectiveTimeout.TotalSeconds:0.#} seconds"
            : $"{effectiveTimeout.TotalMilliseconds:0} ms";
    }
}
