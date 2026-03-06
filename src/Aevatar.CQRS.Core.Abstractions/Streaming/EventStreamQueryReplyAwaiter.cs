using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.CQRS.Core.Abstractions.Streaming;

/// <summary>
/// Generic request/reply helper for actor query events over stream subscriptions.
/// </summary>
public static class EventStreamQueryReplyAwaiter
{
    public static async Task<TResponse> QueryAsync<TResponse>(
        IStreamProvider streams,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, Task> dispatchAsync,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
        where TResponse : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamPrefix);
        ArgumentNullException.ThrowIfNull(dispatchAsync);
        ArgumentNullException.ThrowIfNull(isMatch);
        ArgumentNullException.ThrowIfNull(timeoutMessageFactory);

        var requestId = Guid.NewGuid().ToString("N");
        var replyStreamId = $"{replyStreamPrefix}:{requestId}";
        var responseTaskSource = new TaskCompletionSource<TResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await streams
            .GetStream(replyStreamId)
            .SubscribeAsync<TResponse>(response =>
            {
                if (isMatch(response, requestId))
                    responseTaskSource.TrySetResult(response);

                return Task.CompletedTask;
            }, ct);

        await dispatchAsync(requestId, replyStreamId);
        return await WaitForResponseAsync(
            responseTaskSource.Task,
            timeout,
            requestId,
            timeoutMessageFactory,
            ct);
    }

    public static Task<TResponse> QueryActorAsync<TResponse>(
        IStreamProvider streams,
        IActor actor,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
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

    private static async Task<TResponse> WaitForResponseAsync<TResponse>(
        Task<TResponse> responseTask,
        TimeSpan timeout,
        string requestId,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            throw new TimeoutException(timeoutMessageFactory(requestId));

        timeoutCts.Cancel();
        return await responseTask;
    }
}
