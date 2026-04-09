using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Shared utility for reading a snapshot from a ReadModel GAgent via per-request
/// temporary subscription. Eliminates duplicated <c>ReadFromReadModelAsync</c>
/// across all ActorBacked stores.
///
/// Pattern: subscribe → activate readmodel actor → wait for snapshot → unsubscribe.
/// Method-local TaskCompletionSource only. No service-level state.
/// </summary>
internal static class ReadModelSnapshotReader
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Subscribe to a ReadModel GAgent, activate it (triggering OnActivateAsync → snapshot
    /// publish), wait for the snapshot event, and return the unpacked state.
    /// </summary>
    /// <typeparam name="TState">The protobuf state type (e.g., GAgentRegistryState).</typeparam>
    /// <typeparam name="TSnapshotEvent">The snapshot event type (e.g., GAgentRegistryStateSnapshotEvent).</typeparam>
    /// <param name="subscriptions">Subscription provider for actor event streams.</param>
    /// <param name="runtime">Actor runtime for activating actors.</param>
    /// <param name="readModelActorId">The readmodel actor ID to subscribe to.</param>
    /// <param name="readModelActorType">The readmodel GAgent type (for CreateAsync if not yet activated).</param>
    /// <param name="snapshotDescriptor">Protobuf descriptor for the snapshot event.</param>
    /// <param name="unpackSnapshot">Function to unpack the snapshot state from the event.</param>
    /// <param name="logger">Logger for timeout warnings.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<TState?> ReadAsync<TState, TSnapshotEvent>(
        IActorEventSubscriptionProvider subscriptions,
        IActorRuntime runtime,
        string readModelActorId,
        Type readModelActorType,
        MessageDescriptor snapshotDescriptor,
        Func<TSnapshotEvent, TState?> unpackSnapshot,
        ILogger logger,
        CancellationToken ct)
        where TState : class, IMessage
        where TSnapshotEvent : class, IMessage, new()
    {
        var tcs = new TaskCompletionSource<TState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await subscriptions.SubscribeAsync<EventEnvelope>(
            readModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(snapshotDescriptor) == true)
                {
                    var snapshotEvent = envelope.Payload.Unpack<TSnapshotEvent>();
                    tcs.TrySetResult(unpackSnapshot(snapshotEvent));
                }
                return Task.CompletedTask;
            },
            ct);

        // Activate readmodel actor (triggers OnActivateAsync → PublishAsync snapshot)
        if (await runtime.GetAsync(readModelActorId) is null)
            await runtime.CreateAsync(readModelActorType, readModelActorId, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Timeout waiting for readmodel snapshot from {ActorId}", readModelActorId);
            return null;
        }
    }
}
