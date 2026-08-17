namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStoreSchemaWaitSupport
{
    internal static async Task WaitAsync(
        Func<long, CancellationToken, Task<bool>> tryWaitCycleAsync,
        TimeSpan totalTimeout,
        long maxCycleSeconds,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tryWaitCycleAsync);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (totalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        if (maxCycleSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCycleSeconds));

        var startedAt = timeProvider.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = totalTimeout - timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Neo4j index did not become ONLINE within {totalTimeout.TotalSeconds:0} seconds.");

            var remainingSeconds = Math.Max(1L, (long)Math.Ceiling(remaining.TotalSeconds));
            var cycleSeconds = Math.Min(maxCycleSeconds, remainingSeconds);
            if (await tryWaitCycleAsync(cycleSeconds, ct))
                return;
        }
    }
}
