namespace Aevatar.Platform.Application.Abstractions.Commands;

public interface IPlatformCommandApplicationService
{
    Task<PlatformCommandEnqueueResult> EnqueueAsync(
        PlatformCommandRequest request,
        CancellationToken ct = default);
}
