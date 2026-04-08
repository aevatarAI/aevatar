using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ScriptStorage;

/// <summary>
/// Singleton actor that stores uploaded script source artifacts.
/// Replaces the chrono-storage backed <c>ChronoStorageScriptStoragePort</c>.
///
/// Actor ID: <c>script-storage</c> (cluster-scoped singleton).
///
/// After each state change, publishes <see cref="ScriptStorageStateSnapshotEvent"/>
/// so readmodel subscribers can maintain an up-to-date projection without
/// reading write-model internal state.
/// </summary>
public sealed class ScriptStorageGAgent : GAgentBase<ScriptStorageState>
{
    [EventHandler(EndpointName = "uploadScript")]
    public async Task HandleScriptUploaded(ScriptUploadedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ScriptId) || string.IsNullOrWhiteSpace(evt.SourceText))
            return;

        await PersistDomainEventAsync(evt);
        await PublishStateSnapshotAsync();
    }

    /// <summary>
    /// On activation (after event replay), publish the current state so
    /// any subscriber that activates the actor can receive the initial snapshot.
    /// </summary>
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishStateSnapshotAsync();
    }

    protected override ScriptStorageState TransitionState(
        ScriptStorageState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptUploadedEvent>(ApplyScriptUploaded)
            .OrCurrent();
    }

    private static ScriptStorageState ApplyScriptUploaded(
        ScriptStorageState state, ScriptUploadedEvent evt)
    {
        var next = state.Clone();
        next.Scripts[evt.ScriptId] = evt.SourceText;
        return next;
    }

    private async Task PublishStateSnapshotAsync()
    {
        var snapshot = new ScriptStorageStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
