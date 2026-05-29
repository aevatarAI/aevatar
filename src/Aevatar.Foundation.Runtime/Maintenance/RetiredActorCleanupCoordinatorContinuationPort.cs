using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Maintenance;

public sealed class RetiredActorCleanupCoordinatorContinuationPort : IRetiredActorCleanupCoordinatorContinuationPort
{
    private readonly IStreamProvider _streamProvider;

    public RetiredActorCleanupCoordinatorContinuationPort(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        Func<RetiredActorCleanupCoordinatorContinuation, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        return _streamProvider
            .GetStream(RetiredActorCleanupCoordinatorGAgent.ActorId)
            .SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (envelope.Payload == null)
                    return Task.CompletedTask;

                var continuation = ExtractContinuation(envelope);
                return continuation == null
                    ? Task.CompletedTask
                    : handler(continuation);
            }, ct);
    }

    private static RetiredActorCleanupCoordinatorContinuation? ExtractContinuation(EventEnvelope envelope)
    {
        if (envelope.Payload!.TryUnpack<RetiredActorCleanupAcquireLeaseContinuation>(out var acquire))
            return new RetiredActorCleanupCoordinatorContinuation { AcquireLease = acquire };

        if (envelope.Payload.TryUnpack<RetiredActorCleanupCheckLeaseContinuation>(out var check))
            return new RetiredActorCleanupCoordinatorContinuation { CheckLease = check };

        if (envelope.Payload.TryUnpack<RetiredActorCleanupReleaseLeaseContinuation>(out var release))
            return new RetiredActorCleanupCoordinatorContinuation { ReleaseLease = release };

        if (envelope.Payload.TryUnpack<RetiredActorCleanupRecordFailureContinuation>(out var failure))
            return new RetiredActorCleanupCoordinatorContinuation { RecordFailure = failure };

        return null;
    }
}
