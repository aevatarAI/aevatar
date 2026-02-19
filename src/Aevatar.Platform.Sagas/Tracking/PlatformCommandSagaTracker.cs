using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;

namespace Aevatar.Platform.Sagas.Tracking;

public sealed class PlatformCommandSagaTracker : IPlatformCommandSagaTracker
{
    private readonly ISagaRuntime _runtime;

    public PlatformCommandSagaTracker(ISagaRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task TrackAsync(
        PlatformCommandStatus status,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var envelope = PlatformCommandSagaEnvelopeFactory.Create(status, correlationId);
        return _runtime.ObserveAsync(PlatformCommandSagaEnvelopeFactory.SagaActorId, envelope, ct);
    }
}
