namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioBackendRequestAuthSnapshotProvider
{
    Task<StudioBackendRequestAuthSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed record StudioBackendRequestAuthSnapshot(
    string? LocalOrigin,
    string? BearerToken = null,
    string? InternalAuthHeaderName = null,
    string? InternalAuthToken = null);
