using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Streaming;

public sealed class RuntimeStreamRequestReplyClient : IStreamRequestReplyClient
{
    private const string ReplyStreamReadyProbePrefix = "__aevatar.reply.ready__:";
    private readonly IStreamLifecycleManager _streamLifecycleManager;

    public RuntimeStreamRequestReplyClient(
        IStreamLifecycleManager? streamLifecycleManager = null)
    {
        _streamLifecycleManager = streamLifecycleManager ?? NullStreamLifecycleManager.Instance;
    }

    public async Task<TResponse> QueryAsync<TResponse>(
        IStreamProvider streams,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, Task> dispatchAsync,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct = default)
        where TResponse : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamPrefix);
        ArgumentNullException.ThrowIfNull(dispatchAsync);
        ArgumentNullException.ThrowIfNull(isMatch);
        ArgumentNullException.ThrowIfNull(timeoutMessageFactory);

        var requestId = Guid.NewGuid().ToString("N");
        var replyStreamId = $"{replyStreamPrefix}:{requestId}";
        var replyStream = streams.GetStream(replyStreamId);
        var responseTaskSource = new TaskCompletionSource<TResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTaskSource = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responseDescriptor = typeof(TResponse) == typeof(EventEnvelope)
            ? null
            : new TResponse().Descriptor;

        try
        {
            await using var subscription = await replyStream
                .SubscribeAsync<EventEnvelope>(envelope =>
                {
                    if (TryHandleReadyProbe(envelope, requestId))
                    {
                        readyTaskSource.TrySetResult(true);
                        return Task.CompletedTask;
                    }

                    if (TryReadResponse(envelope, responseDescriptor, out TResponse? response) &&
                        response is not null &&
                        isMatch(response, requestId))
                    {
                        responseTaskSource.TrySetResult(response);
                    }

                    return Task.CompletedTask;
                }, ct);

            await replyStream.ProduceAsync(new StringValue { Value = BuildReadyProbeToken(requestId) }, ct);
            var remainingTimeout = await WaitForReadyAsync(
                readyTaskSource.Task,
                timeout,
                requestId,
                ct);
            await dispatchAsync(requestId, replyStreamId);
            return await WaitForResponseAsync(
                responseTaskSource.Task,
                remainingTimeout,
                requestId,
                timeoutMessageFactory,
                ct);
        }
        finally
        {
            _streamLifecycleManager.RemoveStream(replyStreamId);
        }
    }

    public Task<TResponse> QueryActorAsync<TResponse>(
        IStreamProvider streams,
        IActor actor,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct = default)
        where TResponse : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(envelopeFactory);

        return QueryAsync<TResponse>(
            streams,
            replyStreamPrefix,
            timeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                envelopeFactory(requestId, replyStreamId),
                ct),
            isMatch,
            timeoutMessageFactory,
            ct);
    }

    private static bool TryHandleReadyProbe(EventEnvelope envelope, string requestId)
    {
        if (envelope.Payload == null || !envelope.Payload.Is(StringValue.Descriptor))
            return false;

        var token = envelope.Payload.Unpack<StringValue>().Value ?? string.Empty;
        return string.Equals(token, BuildReadyProbeToken(requestId), StringComparison.Ordinal);
    }

    private static bool TryReadResponse<TResponse>(
        EventEnvelope envelope,
        MessageDescriptor? responseDescriptor,
        out TResponse? response)
        where TResponse : IMessage, new()
    {
        response = default;

        if (typeof(TResponse) == typeof(EventEnvelope))
        {
            response = (TResponse)(IMessage)envelope;
            return true;
        }

        if (envelope.Payload == null || responseDescriptor == null || !envelope.Payload.Is(responseDescriptor))
            return false;

        response = envelope.Payload.Unpack<TResponse>();
        return response != null;
    }

    private static async Task<TimeSpan> WaitForReadyAsync(
        Task readyTask,
        TimeSpan timeout,
        string requestId,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        await WaitForResponseAsync(
            WaitForReadySignalAsync(readyTask),
            timeout,
            requestId,
            static id => $"Reply stream subscription timed out before dispatch. request_id={id}",
            ct);

        var elapsed = DateTime.UtcNow - startedAt;
        var remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"Reply stream readiness exhausted timeout budget. request_id={requestId}");

        return remaining;
    }

    private static async Task<ReadySignal> WaitForReadySignalAsync(Task readyTask)
    {
        await readyTask;
        return ReadySignal.Instance;
    }

    private static string BuildReadyProbeToken(string requestId) =>
        $"{ReplyStreamReadyProbePrefix}{requestId}";

    private static async Task<TResponse> WaitForResponseAsync<TResponse>(
        Task<TResponse> responseTask,
        TimeSpan timeout,
        string requestId,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (ReferenceEquals(completed, responseTask))
        {
            timeoutCts.Cancel();
            return await responseTask;
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(timeoutMessageFactory(requestId));
    }

    private sealed class ReadySignal
    {
        public static ReadySignal Instance { get; } = new();
    }

    private sealed class NullStreamLifecycleManager : IStreamLifecycleManager
    {
        public static NullStreamLifecycleManager Instance { get; } = new();

        public void RemoveStream(string actorId)
        {
            _ = actorId;
        }
    }
}
