using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter159/cluster-613-first):
//   Old pattern: NyxID bearer entered workflow durable + pending approval surface.
//   New principle: request bearer scrubbed at envelope/state/continuation; only durable model/route controls remain.
internal sealed class WorkflowRunCommittedStateRedactionHook : ICommittedStatePublicationHook
{
    public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.ActorType != typeof(WorkflowRunGAgent) ||
            context.Published.StateRoot?.Is(WorkflowRunState.Descriptor) != true)
        {
            return Task.CompletedTask;
        }

        if (context.Published.StateEvent?.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
        {
            var stateEvent = context.Published.StateEvent.EventData.Unpack<WorkflowExecutionStateUpsertedEvent>();
            if (stateEvent.State?.Is(SecureInputModuleState.Descriptor) == true)
            {
                var secureState = stateEvent.State.Unpack<SecureInputModuleState>() ?? new SecureInputModuleState();
                stateEvent.State = Any.Pack(RedactSecureInputState(secureState));
                context.Published.StateEvent.EventData = Any.Pack(stateEvent);
            }
            else if (stateEvent.State?.Is(ConnectorCallModuleState.Descriptor) == true)
            {
                var connectorState = stateEvent.State.Unpack<ConnectorCallModuleState>() ?? new ConnectorCallModuleState();
                stateEvent.State = Any.Pack(RedactConnectorCallState(connectorState));
                context.Published.StateEvent.EventData = Any.Pack(stateEvent);
            }
        }

        if (context.Published.StateEvent?.EventData?.Is(WorkflowRunExecutionStartedEvent.Descriptor) == true)
        {
            var startedEvent = context.Published.StateEvent.EventData.Unpack<WorkflowRunExecutionStartedEvent>();
            RedactExecutionContextDelta(startedEvent.ExecutionContextDelta);
            context.Published.StateEvent.EventData = Any.Pack(startedEvent);
        }

        if (context.Published.StateEvent?.EventData?.Is(WorkflowRunExecutionContextUpdatedEvent.Descriptor) == true)
        {
            var updateEvent = context.Published.StateEvent.EventData.Unpack<WorkflowRunExecutionContextUpdatedEvent>();
            RedactExecutionContextDelta(updateEvent.ExecutionContextDelta);
            context.Published.StateEvent.EventData = Any.Pack(updateEvent);
        }

        var state = context.Published.StateRoot.Unpack<WorkflowRunState>() ?? new WorkflowRunState();
        state.ExecutionContext = WorkflowRunExecutionContextStateAccess.RedactedClone(state.ExecutionContext);
        RedactSecureInputExecutionState(state);
        RedactConnectorCallExecutionState(state);

        context.Published.StateRoot = Any.Pack(state);
        return Task.CompletedTask;
    }

    private static void RedactSecureInputExecutionState(WorkflowRunState state)
    {
        foreach (var pair in state.ExecutionStates.ToList())
        {
            if (!pair.Value.Is(SecureInputModuleState.Descriptor))
                continue;

            var secureState = pair.Value.Unpack<SecureInputModuleState>() ?? new SecureInputModuleState();
            state.ExecutionStates[pair.Key] = Any.Pack(RedactSecureInputState(secureState));
        }
    }

    private static SecureInputModuleState RedactSecureInputState(SecureInputModuleState source)
    {
        var redacted = source.Clone();
        foreach (var captured in redacted.Captured.Values)
            captured.Value = string.Empty;
        return redacted;
    }

    private static void RedactConnectorCallExecutionState(WorkflowRunState state)
    {
        foreach (var pair in state.ExecutionStates.ToList())
        {
            if (!pair.Value.Is(ConnectorCallModuleState.Descriptor))
                continue;

            var connectorState = pair.Value.Unpack<ConnectorCallModuleState>() ?? new ConnectorCallModuleState();
            state.ExecutionStates[pair.Key] = Any.Pack(RedactConnectorCallState(connectorState));
        }
    }

    private static ConnectorCallModuleState RedactConnectorCallState(ConnectorCallModuleState source)
    {
        var redacted = source.Clone();
        foreach (var coordination in redacted.ApprovalsByActionId.Values)
        {
            coordination.MaterialReference = null;
            coordination.CompletionReference = null;
        }
        return redacted;
    }

    private static void RedactExecutionContextDelta(WorkflowRunExecutionContextDelta? delta)
    {
        if (delta == null)
            return;

        if (!string.IsNullOrWhiteSpace(delta.CallerCredential?.BearerToken))
            delta.CallerCredential.BearerToken = string.Empty;
        if (!string.IsNullOrWhiteSpace(delta.CallerCredential?.SourceReadableUserBearerToken))
            delta.CallerCredential.SourceReadableUserBearerToken = string.Empty;
        if (delta.CallerCredential?.DurableCallerCredential != null)
            delta.CallerCredential.DurableCallerCredential = null;
        if (delta.CallerCredential?.NyxIdAuthority != null)
            delta.CallerCredential.NyxIdAuthority = null;
    }
}
