namespace Aevatar.App.Application.Concurrency;

public interface IImageConcurrencyCoordinator
{
    Task<AcquireAttemptResult> TryAcquireGenerateAsync(CancellationToken ct = default);
    Task ReleaseGenerateAsync();
    Task<AcquireAttemptResult> TryAcquireUploadAsync(CancellationToken ct = default);
    Task ReleaseUploadAsync();
    Task<ConcurrencyStateResult> GetStatsAsync();
}
