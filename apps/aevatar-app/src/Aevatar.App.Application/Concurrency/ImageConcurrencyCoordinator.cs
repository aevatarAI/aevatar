namespace Aevatar.App.Application.Concurrency;

public sealed class ImageConcurrencyCoordinator : IImageConcurrencyCoordinator
{
    private const int QueuePollIntervalMs = 50;

    private readonly object _lock = new();
    private readonly int _maxTotal;
    private readonly int _maxQueueSize;
    private readonly int _queueTimeoutMs;

    private int _activeGenerates;
    private int _activeUploads;
    private int _queuedGenerates;
    private int _queuedUploads;

    public ImageConcurrencyCoordinator(
        int maxTotal = 20,
        int maxQueueSize = 100,
        int queueTimeoutMs = 30_000)
    {
        _maxTotal = maxTotal;
        _maxQueueSize = maxQueueSize;
        _queueTimeoutMs = queueTimeoutMs;
    }

    public Task<AcquireAttemptResult> TryAcquireGenerateAsync(CancellationToken ct = default)
    {
        bool acquired;
        lock (_lock)
        {
            if (AvailableSlots() > 0 && _queuedUploads == 0)
            {
                _activeGenerates++;
                acquired = true;
            }
            else if (_queuedGenerates + _queuedUploads >= _maxQueueSize)
            {
                return Task.FromResult(
                    AcquireAttemptResult.QueueFullResult("Too many concurrent image requests. Please try again shortly."));
            }
            else
            {
                _queuedGenerates++;
                acquired = false;
            }
        }

        return acquired
            ? Task.FromResult(AcquireAttemptResult.AcquiredResult())
            : WaitForPromoteGenerateAsync(ct);
    }

    public Task ReleaseGenerateAsync()
    {
        lock (_lock)
        {
            if (_activeGenerates > 0) _activeGenerates--;
        }
        return Task.CompletedTask;
    }

    public Task<AcquireAttemptResult> TryAcquireUploadAsync(CancellationToken ct = default)
    {
        bool acquired;
        lock (_lock)
        {
            if (AvailableSlots() > 0)
            {
                _activeUploads++;
                acquired = true;
            }
            else if (_queuedGenerates + _queuedUploads >= _maxQueueSize)
            {
                return Task.FromResult(
                    AcquireAttemptResult.QueueFullResult("Too many concurrent upload requests. Please try again shortly."));
            }
            else
            {
                _queuedUploads++;
                acquired = false;
            }
        }

        return acquired
            ? Task.FromResult(AcquireAttemptResult.AcquiredResult())
            : WaitForPromoteUploadAsync(ct);
    }

    public Task ReleaseUploadAsync()
    {
        lock (_lock)
        {
            if (_activeUploads > 0) _activeUploads--;
        }
        return Task.CompletedTask;
    }

    public Task<ConcurrencyStateResult> GetStatsAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(new ConcurrencyStateResult(
                _activeGenerates,
                _activeUploads,
                _queuedGenerates + _queuedUploads,
                AvailableSlots(),
                _maxTotal));
        }
    }

    private async Task<AcquireAttemptResult> WaitForPromoteGenerateAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_queueTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(QueuePollIntervalMs, ct);

            lock (_lock)
            {
                if (_queuedUploads > 0 || AvailableSlots() <= 0)
                    continue;
                if (_queuedGenerates <= 0)
                    continue;

                _queuedGenerates--;
                _activeGenerates++;
                return AcquireAttemptResult.AcquiredResult();
            }
        }

        lock (_lock)
        {
            if (_queuedGenerates > 0) _queuedGenerates--;
        }
        return AcquireAttemptResult.TimeoutResult(
            $"Image generation queue timeout ({_queueTimeoutMs}ms). Please try again.");
    }

    private async Task<AcquireAttemptResult> WaitForPromoteUploadAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_queueTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(QueuePollIntervalMs, ct);

            lock (_lock)
            {
                if (AvailableSlots() <= 0)
                    continue;
                if (_queuedUploads <= 0)
                    continue;

                _queuedUploads--;
                _activeUploads++;
                return AcquireAttemptResult.AcquiredResult();
            }
        }

        lock (_lock)
        {
            if (_queuedUploads > 0) _queuedUploads--;
        }
        return AcquireAttemptResult.TimeoutResult(
            $"Image upload queue timeout ({_queueTimeoutMs}ms). Please try again.");
    }

    private int AvailableSlots() =>
        Math.Max(0, _maxTotal - _activeGenerates - _activeUploads);
}

public enum AcquireFailureReason
{
    None = 0,
    Overloaded = 1,
    RateLimit = 2
}

public sealed record AcquireAttemptResult(bool Acquired, AcquireFailureReason Reason, string Message)
{
    public static AcquireAttemptResult AcquiredResult() =>
        new(true, AcquireFailureReason.None, string.Empty);

    public static AcquireAttemptResult QueueFullResult(string message) =>
        new(false, AcquireFailureReason.Overloaded, message);

    public static AcquireAttemptResult TimeoutResult(string message) =>
        new(false, AcquireFailureReason.Overloaded, message);

    public static AcquireAttemptResult RateLimitedResult(string message) =>
        new(false, AcquireFailureReason.RateLimit, message);
}

public sealed record ConcurrencyStateResult(
    int ActiveGenerates,
    int ActiveUploads,
    int QueueLength,
    int AvailableSlots,
    int MaxTotal);
