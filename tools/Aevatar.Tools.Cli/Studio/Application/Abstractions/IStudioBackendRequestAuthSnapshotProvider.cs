namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IStudioBackendRequestAuthSnapshotProvider
{
    Task<StudioBackendRequestAuthSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed record StudioBackendRequestAuthSnapshot(
    string? LocalOrigin,
    string? BearerToken = null,
    string? InternalAuthHeaderName = null,
    string? InternalAuthToken = null);
