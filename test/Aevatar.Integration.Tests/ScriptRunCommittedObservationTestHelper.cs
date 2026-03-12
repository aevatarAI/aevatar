using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Scripting.Core;

namespace Aevatar.Integration.Tests;

internal static class ScriptRunCommittedObservationTestHelper
{
    public static async Task<ScriptRunCommittedObservation> WaitForCommittedAsync(
        IStreamProvider streams,
        string actorId,
        string runId,
        Func<Task> trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(trigger);

        var observed = new TaskCompletionSource<ScriptRunCommittedObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await streams
            .GetStream(actorId)
            .SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (!envelope.Route.IsObserve())
                    return Task.CompletedTask;

                if (envelope.Payload?.Is(ScriptRunDomainEventCommitted.Descriptor) != true)
                    return Task.CompletedTask;

                var committed = envelope.Payload.Unpack<ScriptRunDomainEventCommitted>();
                if (string.Equals(committed.RunId, runId, StringComparison.Ordinal))
                {
                    observed.TrySetResult(new ScriptRunCommittedObservation(
                        envelope,
                        committed));
                }

                return Task.CompletedTask;
            }, ct);

        await trigger();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await observed.Task.WaitAsync(timeout.Token);
    }
}

internal sealed record ScriptRunCommittedObservation(
    EventEnvelope Envelope,
    ScriptRunDomainEventCommitted Event);
