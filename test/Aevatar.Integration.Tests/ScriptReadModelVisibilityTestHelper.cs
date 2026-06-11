using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Integration.Tests;

internal static class ScriptReadModelVisibilityTestHelper
{
    public static async Task<ScriptReadModelSnapshot> WaitForSnapshotAsync(
        Func<CancellationToken, Task<ScriptReadModelSnapshot?>> queryAsync,
        long minStateVersion,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(queryAsync);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutOverride ?? TimeSpan.FromSeconds(10));

        Exception? lastTransientReadException = null;
        try
        {
            while (true)
            {
                ScriptReadModelSnapshot? snapshot;
                try
                {
                    snapshot = await queryAsync(timeout.Token);
                    lastTransientReadException = null;
                }
                catch (InvalidOperationException ex) when (IsTransientMissingReadIndex(ex))
                {
                    lastTransientReadException = ex;
                    snapshot = null;
                }

                if (snapshot != null && snapshot.StateVersion >= minStateVersion)
                    return snapshot;

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for script read model snapshot. min_state_version={minStateVersion}",
                lastTransientReadException);
        }
    }

    private static bool IsTransientMissingReadIndex(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Elasticsearch index", StringComparison.Ordinal)
               && message.Contains("was not found during 'get'", StringComparison.Ordinal)
               && message.Contains("read-model", StringComparison.Ordinal);
    }
}
