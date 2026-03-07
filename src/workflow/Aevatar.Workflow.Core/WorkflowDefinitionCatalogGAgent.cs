using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowDefinitionCatalogGAgent : GAgentBase<WorkflowDefinitionCatalogState>
{
    public WorkflowDefinitionCatalogGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleUpsertWorkflowDefinitionRequested(UpsertWorkflowDefinitionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new InvalidOperationException("WorkflowName is required.");
        if (string.IsNullOrWhiteSpace(evt.WorkflowYaml))
            throw new InvalidOperationException($"WorkflowYaml is required for workflow `{workflowName}`.");

        await PersistDomainEventAsync(new WorkflowDefinitionCatalogEntryUpsertedEvent
        {
            WorkflowName = workflowName,
            WorkflowYaml = evt.WorkflowYaml,
        });
    }

    [EventHandler]
    public async Task HandleQueryWorkflowDefinitionRequested(QueryWorkflowDefinitionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new WorkflowDefinitionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                FailureReason = "WorkflowName is required.",
            });
            return;
        }

        if (!State.Entries.TryGetValue(workflowName, out var entry))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new WorkflowDefinitionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                WorkflowName = workflowName,
                FailureReason = $"Workflow `{workflowName}` not found in definition catalog.",
            });
            return;
        }

        await SendQueryResponseAsync(evt.ReplyStreamId, new WorkflowDefinitionRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = true,
            WorkflowName = entry.WorkflowName ?? workflowName,
            WorkflowYaml = entry.WorkflowYaml ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleQueryWorkflowDefinitionNamesRequested(QueryWorkflowDefinitionNamesRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        var response = new WorkflowDefinitionNamesRespondedEvent
        {
            RequestId = evt.RequestId,
        };
        response.WorkflowNames.Add(
            State.Entries.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        await SendQueryResponseAsync(evt.ReplyStreamId, response);
    }

    protected override WorkflowDefinitionCatalogState TransitionState(WorkflowDefinitionCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowDefinitionCatalogEntryUpsertedEvent>(ApplyUpserted)
            .OrCurrent();

    private Task SendQueryResponseAsync(
        string replyStreamId,
        IMessage response,
        CancellationToken ct = default) =>
        EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);

    private static WorkflowDefinitionCatalogState ApplyUpserted(
        WorkflowDefinitionCatalogState state,
        WorkflowDefinitionCatalogEntryUpsertedEvent evt)
    {
        var next = state.Clone();
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            return next;

        if (!next.Entries.TryGetValue(workflowName, out var entry))
        {
            entry = new WorkflowDefinitionCatalogEntryState
            {
                WorkflowName = workflowName,
                Version = 0,
            };
            next.Entries[workflowName] = entry;
        }

        entry.WorkflowName = workflowName;
        entry.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        entry.Version += 1;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{workflowName}:{entry.Version}:upserted";
        return next;
    }
}
