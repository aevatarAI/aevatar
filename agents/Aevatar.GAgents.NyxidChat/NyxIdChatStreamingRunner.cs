using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatStreamingRunner
{
    internal sealed record ErrorMessages(
        string DispatchFailedBeforeCompletion,
        string Timeout,
        string UnhandledFailure);

    public static async Task RunAsync(
        HttpContext http,
        string actorId,
        string subscriptionActorId,
        IActorEventSubscriptionProvider subscriptionProvider,
        ILogger logger,
        Func<string, CancellationToken, Task> dispatchAsync,
        Func<EventEnvelope, string, NyxIdChatSseWriter, ValueTask<string?>> mapAndWriteEventAsync,
        ErrorMessages errorMessages,
        CancellationToken ct)
    {
        var writer = new NyxIdChatSseWriter(http.Response);

        try
        {
            await writer.StartAsync(ct);

            var messageId = Guid.NewGuid().ToString("N");
            await writer.WriteRunStartedAsync(actorId, ct);

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => completion.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                subscriptionActorId,
                async envelope =>
                {
                    try
                    {
                        var terminalFrame = await mapAndWriteEventAsync(envelope, messageId, writer);
                        if (!string.IsNullOrWhiteSpace(terminalFrame))
                            completion.TrySetResult(terminalFrame);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                },
                ct);

            await dispatchAsync(messageId, ct);

            var completedTask = await Task.WhenAny(completion.Task, Task.Delay(120_000, ct));
            if (completedTask == completion.Task)
            {
                if (completion.Task.IsFaulted)
                {
                    await writer.WriteRunErrorAsync(
                        errorMessages.DispatchFailedBeforeCompletion,
                        CancellationToken.None);
                }
                else if (string.Equals(completion.Task.Result, "TEXT_MESSAGE_END", StringComparison.Ordinal))
                {
                    await writer.WriteRunFinishedAsync(CancellationToken.None);
                }
            }
            else
            {
                await writer.WriteRunErrorAsync(errorMessages.Timeout, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID streaming request failed for actor {ActorId}", actorId);

            try
            {
                await writer.WriteRunErrorAsync(
                    errorMessages.UnhandledFailure,
                    CancellationToken.None);
            }
            catch
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }
}
