using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.WorkflowStorage;

/// <summary>
/// Singleton actor that stores workflow YAML artifacts keyed by workflow ID.
/// Replaces the chrono-storage backed <c>ChronoStorageWorkflowStoragePort</c>.
///
/// Actor ID: <c>workflow-storage</c> (cluster-scoped singleton).
///
/// After each state change, publishes <see cref="WorkflowStorageStateSnapshotEvent"/>
/// so readmodel subscribers can maintain an up-to-date projection without
/// reading write-model internal state.
/// </summary>
public sealed class WorkflowStorageGAgent : GAgentBase<WorkflowStorageState>
{
    [EventHandler(EndpointName = "uploadWorkflowYaml")]
    public async Task HandleWorkflowYamlUploaded(WorkflowYamlUploadedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.WorkflowId) || string.IsNullOrWhiteSpace(evt.Yaml))
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

    protected override WorkflowStorageState TransitionState(
        WorkflowStorageState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowYamlUploadedEvent>(ApplyYamlUploaded)
            .OrCurrent();
    }

    private static WorkflowStorageState ApplyYamlUploaded(
        WorkflowStorageState state, WorkflowYamlUploadedEvent evt)
    {
        var next = state.Clone();
        next.Workflows[evt.WorkflowId] = new WorkflowEntry
        {
            WorkflowName = evt.WorkflowName,
            Yaml = evt.Yaml,
        };
        return next;
    }

    private async Task PublishStateSnapshotAsync()
    {
        var snapshot = new WorkflowStorageStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
