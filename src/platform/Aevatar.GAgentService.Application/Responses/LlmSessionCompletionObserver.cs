using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Responses;

internal static class LlmSessionCompletionObserver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static Task<LlmSessionCompletionSnapshot?> WaitForCompletionAsync(
        ILlmSessionQueryPort sessionQueryPort,
        string responseId,
        CancellationToken ct) =>
        WaitForCompletionAsync(sessionQueryPort, responseId, DefaultTimeout, ct);

    internal static async Task<LlmSessionCompletionSnapshot?> WaitForCompletionAsync(
        ILlmSessionQueryPort sessionQueryPort,
        string responseId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionQueryPort);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseId);

        var startedAt = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var snapshot = await sessionQueryPort.GetByResponseIdAsync(responseId, ct);
            if (snapshot?.Completion is not null)
                return snapshot.Completion;

            if (TimeProvider.System.GetElapsedTime(startedAt) >= timeout)
                return null;

            await Task.Delay(PollInterval, ct);
        }
    }
}
