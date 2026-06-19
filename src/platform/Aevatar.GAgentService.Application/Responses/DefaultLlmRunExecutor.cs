using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Internal;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class DefaultLlmRunExecutor(
    ILlmRunCore runCore,
    IActorDispatchPort dispatchPort,
    ILlmRunExecutorClock clock,
    ILogger<DefaultLlmRunExecutor> logger) : ILlmRunExecutor
{
    private const int StreamChunkBatchSize = 16;
    private static readonly TimeSpan StreamChunkBatchWindow = TimeSpan.FromMilliseconds(100);
    private readonly ILlmRunExecutorClock _clock = clock;

    public Task<DispatchAdmission> StartAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);
        return DispatchAsync(
            request,
            new RecordRunStartedRequested
            {
                ResponseId = request.ResponseId,
                RunId = request.RunId,
                StartedAt = request.Command.RequestedAt ?? Timestamp.FromDateTimeOffset(_clock.UtcNow),
            },
            ct);
    }

    public async Task ExecuteAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);

        await Task.Yield();
        var sink = new DispatchingLlmRunSink(this, request);
        try
        {
            await runCore.RunAsync(
                    new LlmRunCoreRequest(request.Command, request.RunId, request.OriginPlatform),
                    sink,
                    ct)
                .ConfigureAwait(false);
            await sink.FlushPendingChunksAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await sink.FlushPendingChunksAsync(CancellationToken.None).ConfigureAwait(false);
            await DispatchAsync(
                    request,
                    new RecordRunCancelledRequested
                    {
                        ResponseId = request.ResponseId,
                        RunId = request.RunId,
                        Cancelled = new LlmRunCancelled
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            CancelledAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                        },
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await sink.FlushPendingChunksAsync(CancellationToken.None).ConfigureAwait(false);
            await DispatchAsync(
                    request,
                    new RecordRunFailedRequested
                    {
                        ResponseId = request.ResponseId,
                        RunId = request.RunId,
                        Failed = new LlmRunFailed
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            FailureCode = "execution_failed",
                            FailureMessage = string.IsNullOrWhiteSpace(ex.Message)
                                ? "LLM run failed."
                                : ex.Message,
                            FailedAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                        },
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogError(ex, "Off-actor LLM run executor failed for response {ResponseId}", request.ResponseId);
        }
    }

    private Task<DispatchAdmission> DispatchAsync(
        LlmRunExecutorRequest request,
        IMessage command,
        CancellationToken ct)
    {
        var envelope = ServiceCommandEnvelopeFactory.Create(
            request.SessionActorId,
            command,
            request.ResponseId);
        return dispatchPort.DispatchAsync(request.SessionActorId, envelope, ct);
    }

    private sealed class DispatchingLlmRunSink(
        DefaultLlmRunExecutor executor,
        LlmRunExecutorRequest request) : ILlmRunSink
    {
        private readonly List<LlmStreamChunkObserved> _pendingChunks = [];
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task? _timerTask;
        private CancellationTokenSource? _timerCts;

        public async Task RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(observed);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _pendingChunks.Add(observed.Clone());
                EnsureFlushTimer();
                if (_pendingChunks.Count >= StreamChunkBatchSize)
                    await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(observed);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
                await executor.DispatchAsync(
                        request,
                        new RecordToolCallObservedRequested
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            Observed = observed.Clone(),
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordForwardedToolCallEmittedAsync(
            LlmSessionForwardedToolCallEmittedEvent emitted,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emitted);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
                await executor.DispatchAsync(
                        request,
                        new RecordForwardedToolCallRequested
                        {
                            ResponseId = request.ResponseId,
                            Call = emitted.Call?.Clone(),
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(completed);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
                await executor.DispatchAsync(
                        request,
                        new RecordRunCompletedRequested
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            Completed = completed.Clone(),
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(failed);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
                await executor.DispatchAsync(
                        request,
                        new RecordRunFailedRequested
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            Failed = failed.Clone(),
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(cancelled);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
                await executor.DispatchAsync(
                        request,
                        new RecordRunCancelledRequested
                        {
                            ResponseId = request.ResponseId,
                            RunId = request.RunId,
                            Cancelled = cancelled.Clone(),
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FlushPendingChunksAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FlushPendingChunksLockedAsync(ct, cancelTimer: true).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task FlushPendingChunksLockedAsync(CancellationToken ct, bool cancelTimer)
        {
            if (_pendingChunks.Count == 0)
                return;

            if (cancelTimer)
                CancelFlushTimer();
            var command = new RecordStreamChunksObservedRequested
            {
                ResponseId = request.ResponseId,
                RunId = request.RunId,
            };
            command.Chunks.AddRange(_pendingChunks.Select(static chunk => chunk.Clone()));
            _pendingChunks.Clear();
            await executor.DispatchAsync(request, command, ct).ConfigureAwait(false);
        }

        private void EnsureFlushTimer()
        {
            if (_timerTask is { IsCompleted: false })
                return;

            _timerCts?.Dispose();
            var cts = new CancellationTokenSource();
            _timerCts = cts;
            _timerTask = FlushAfterWindowAsync(cts);
        }

        private async Task FlushAfterWindowAsync(CancellationTokenSource cts)
        {
            try
            {
                var ct = cts.Token;
                await executor._clock.DelayAsync(StreamChunkBatchWindow, ct).ConfigureAwait(false);
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await FlushPendingChunksLockedAsync(CancellationToken.None, cancelTimer: false)
                        .ConfigureAwait(false);
                    if (ReferenceEquals(_timerCts, cts))
                    {
                        _timerCts.Dispose();
                        _timerCts = null;
                        _timerTask = null;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }

        private void CancelFlushTimer()
        {
            if (_timerCts is null)
                return;

            _timerCts.Cancel();
            _timerCts.Dispose();
            _timerCts = null;
            _timerTask = null;
        }
    }
}
