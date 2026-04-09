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
/// Write-only: no readmodel needed since the only port method is
/// <c>UploadWorkflowYamlAsync</c>.
/// </summary>
public sealed class WorkflowStorageGAgent : GAgentBase<WorkflowStorageState>
{
    [EventHandler(EndpointName = "uploadWorkflowYaml")]
    public async Task HandleWorkflowYamlUploaded(WorkflowYamlUploadedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.WorkflowId) || string.IsNullOrWhiteSpace(evt.Yaml))
            return;

        await PersistDomainEventAsync(evt);
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
}
