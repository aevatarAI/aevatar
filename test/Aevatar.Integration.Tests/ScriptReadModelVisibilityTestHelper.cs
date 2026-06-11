using Aevatar.CQRS.Projection.Stores.Abstractions;
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
        return await WaitForSnapshotAsync(
            queryAsync,
            snapshot => snapshot.StateVersion >= minStateVersion,
            $"Timed out waiting for script read model snapshot. min_state_version={minStateVersion}",
            ct,
            timeoutOverride);
    }

    public static async Task<ScriptReadModelSnapshot> WaitForSnapshotAsync(
        Func<CancellationToken, Task<ScriptReadModelSnapshot?>> queryAsync,
        Func<ScriptReadModelSnapshot, bool> isReady,
        string timeoutMessage,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(queryAsync);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutMessage);

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
                catch (ProjectionIndexSchemaDriftException ex) when (IsTransientBareAliasVisibilityDrift(ex))
                {
                    lastTransientReadException = ex;
                    snapshot = null;
                }

                if (snapshot != null && isReady(snapshot))
                    return snapshot;

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(timeoutMessage, lastTransientReadException);
        }
    }

    private static bool IsTransientMissingReadIndex(InvalidOperationException ex)
    {
        var message = ex.Message;
        return IsTransientMissingReadIndexMessage(message) ||
               IsTransientRecoveringShardMessage(message);
    }

    private static bool IsTransientMissingReadIndexMessage(string message)
    {
        return message.Contains("Elasticsearch index", StringComparison.Ordinal)
               && message.Contains("was not found during 'get'", StringComparison.Ordinal)
               && message.Contains("read-model", StringComparison.Ordinal);
    }

    private static bool IsTransientRecoveringShardMessage(string message)
    {
        return message.Contains("Elasticsearch get failed: 503 Service Unavailable", StringComparison.Ordinal)
               && (message.Contains("No shard available", StringComparison.Ordinal)
                   || message.Contains("CurrentState[RECOVERING]", StringComparison.Ordinal));
    }

    private static bool IsTransientBareAliasVisibilityDrift(ProjectionIndexSchemaDriftException ex)
    {
        return string.Equals(ex.CurrentPhysicalIndex, ex.IndexAlias, StringComparison.Ordinal)
               && ex.ExpectedPhysicalIndex.StartsWith(
                   string.Concat(ex.IndexAlias, "-v"),
                   StringComparison.Ordinal);
    }
}
