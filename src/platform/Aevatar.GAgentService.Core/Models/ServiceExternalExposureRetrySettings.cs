namespace Aevatar.GAgentService.Core.Models;

public sealed record ServiceExternalExposureRetrySettings(
    int MaxAttempts,
    TimeSpan BaseDelay,
    TimeSpan MaxDelay)
{
    public static ServiceExternalExposureRetrySettings Default { get; } =
        new(5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

    public static ServiceExternalExposureRetrySettings Create(
        int maxAttempts,
        TimeSpan baseDelay,
        TimeSpan maxDelay)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be greater than zero.");
        if (baseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "baseDelay must be greater than zero.");
        if (maxDelay < baseDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be greater than or equal to baseDelay.");

        return new ServiceExternalExposureRetrySettings(maxAttempts, baseDelay, maxDelay);
    }

    public TimeSpan ComputeDelay(int attempt)
    {
        var safeAttempt = Math.Clamp(attempt, 1, 30);
        var shift = Math.Min(safeAttempt - 1, 20);
        var factor = 1L << shift;
        var delayTicks = BaseDelay.Ticks > TimeSpan.MaxValue.Ticks / factor
            ? TimeSpan.MaxValue.Ticks
            : BaseDelay.Ticks * factor;
        var delay = TimeSpan.FromTicks(delayTicks);
        return delay <= MaxDelay ? delay : MaxDelay;
    }
}
