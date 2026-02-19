using Aevatar.Platform.Application.Abstractions.Commands;

namespace Aevatar.Platform.Application.Abstractions.Ports;

public interface IPlatformCommandSagaTracker
{
    Task TrackAsync(
        PlatformCommandStatus status,
        string correlationId,
        CancellationToken ct = default);
}
