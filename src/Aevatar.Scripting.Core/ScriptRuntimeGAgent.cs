using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    private const string RunFailedEventType = "script.run.failed";
    private const string DefinitionQueryTimeoutCallbackPrefix = "script-definition-query-timeout";
    private static readonly TimeSpan PendingRunTimeout = TimeSpan.FromSeconds(45);

    private readonly IScriptRuntimeExecutionOrchestrator _orchestrator;
    private readonly IScriptDefinitionSnapshotPort _snapshotPort;

    // Activation-local timeout leases; durable query facts live in ScriptRuntimeState.PendingDefinitionQueries.
    private readonly Dictionary<string, PendingRunRuntimeContext> _pendingDefinitionQueryLeases = new(StringComparer.Ordinal);

    public ScriptRuntimeGAgent(
        IScriptRuntimeExecutionOrchestrator orchestrator,
        IScriptDefinitionSnapshotPort snapshotPort)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _snapshotPort = snapshotPort ?? throw new ArgumentNullException(nameof(snapshotPort));
        InitializeId();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        _pendingDefinitionQueryLeases.Clear();

        foreach (var pending in State.PendingDefinitionQueries.Values.OrderBy(x => x.QueuedAtUnixTimeMs))
            await RecoverPendingDefinitionQueryAsync(pending, ct);
    }

    protected override Task OnDeactivateAsync(CancellationToken ct)
    {
        _pendingDefinitionQueryLeases.Clear();
        return Task.CompletedTask;
    }

    private static string BuildDefinitionQueryTimeoutCallbackId(string requestId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            DefinitionQueryTimeoutCallbackPrefix,
            requestId);

    private PendingScriptDefinitionQueryState GetRequiredPendingState(string requestId)
    {
        if (State.PendingDefinitionQueries.TryGetValue(requestId, out var pending))
            return pending.Clone();

        throw new InvalidOperationException($"Missing pending script definition query state for request_id={requestId}.");
    }

    private bool TryGetPendingRunContext(string requestId, out PendingRunRuntimeContext pending)
    {
        pending = new PendingRunRuntimeContext(
            new PendingScriptDefinitionQueryState(),
            null);
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        if (_pendingDefinitionQueryLeases.TryGetValue(requestId, out var runtimePending))
        {
            pending = runtimePending;
            return true;
        }

        if (!State.PendingDefinitionQueries.TryGetValue(requestId, out var persisted))
            return false;

        pending = new PendingRunRuntimeContext(persisted.Clone(), null);
        return true;
    }

    private async Task ClearPendingDefinitionQueryRuntimeAsync(
        PendingRunRuntimeContext pending,
        string reason,
        CancellationToken ct)
    {
        if (_pendingDefinitionQueryLeases.Remove(pending.Pending.RequestId, out var runtimePending))
            pending = runtimePending;

        if (pending.TimeoutLease == null)
            return;

        try
        {
            await CancelDurableCallbackAsync(pending.TimeoutLease, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to cancel script definition query timeout. runtime_actor_id={RuntimeActorId} request_id={RequestId} reason={Reason}",
                Id,
                pending.Pending.RequestId,
                reason);
        }
    }

    private async Task<ScriptDefinitionSnapshot> LoadDefinitionSnapshotAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        return await _snapshotPort.GetRequiredAsync(definitionActorId, requestedRevision, ct);
    }

    private static IReadOnlyDictionary<string, Any> ClonePayloads(
        MapField<string, Any> payloads)
    {
        if (payloads.Count == 0)
            return new Dictionary<string, Any>(StringComparer.Ordinal);

        var clone = new Dictionary<string, Any>(
            payloads.Count,
            StringComparer.Ordinal);
        foreach (var (key, value) in payloads)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            clone[key] = value.Clone();
        }

        return clone;
    }

    private static void CopyPayloads(
        MapField<string, Any> source,
        MapField<string, Any> target)
    {
        target.Clear();
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            target[key] = value.Clone();
        }
    }

    private sealed record PendingRunRuntimeContext(
        PendingScriptDefinitionQueryState Pending,
        RuntimeCallbackLease? TimeoutLease);
}
