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

        try
        {
            while (true)
            {
                var snapshot = await queryAsync(timeout.Token);
                if (snapshot != null && snapshot.StateVersion >= minStateVersion)
                    return snapshot;

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for script read model snapshot. min_state_version={minStateVersion}");
        }
    }
}
