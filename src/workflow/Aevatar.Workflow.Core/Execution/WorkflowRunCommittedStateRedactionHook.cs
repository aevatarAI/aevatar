using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Core.Modules;
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
            else if (stateEvent.State?.Is(ToolCallModuleState.Descriptor) == true)
            {
                var toolCallState = stateEvent.State.Unpack<ToolCallModuleState>() ?? new ToolCallModuleState();
                stateEvent.State = Any.Pack(RedactToolCallState(toolCallState));
                context.Published.StateEvent.EventData = Any.Pack(stateEvent);
            }
            else if (stateEvent.State?.Is(WorkflowExecutionKernelState.Descriptor) == true)
            {
                var kernelState = stateEvent.State.Unpack<WorkflowExecutionKernelState>() ??
                                  new WorkflowExecutionKernelState();
                stateEvent.State = Any.Pack(RedactKernelState(kernelState));
                context.Published.StateEvent.EventData = Any.Pack(stateEvent);
            }
        }

        if (context.Published.StateEvent?.EventData?.Is(WorkflowRunExecutionStartedEvent.Descriptor) == true)
        {
            var startedEvent = context.Published.StateEvent.EventData.Unpack<WorkflowRunExecutionStartedEvent>();
            RedactExecutionContextDelta(startedEvent.ExecutionContextDelta);
            startedEvent.PendingStartWorkflow = null;
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
        if (state.Initiator != null)
            state.Initiator.BindingId = string.Empty;
        state.PendingStartWorkflow = null;
        RedactSecureInputExecutionState(state);
        RedactConnectorCallExecutionState(state);
        RedactToolCallExecutionState(state);
        RedactKernelExecutionState(state);

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

    private static void RedactToolCallExecutionState(WorkflowRunState state)
    {
        foreach (var pair in state.ExecutionStates.ToList())
        {
            if (!pair.Value.Is(ToolCallModuleState.Descriptor))
                continue;

            var toolCallState = pair.Value.Unpack<ToolCallModuleState>() ?? new ToolCallModuleState();
            state.ExecutionStates[pair.Key] = Any.Pack(RedactToolCallState(toolCallState));
        }
    }

    private static ToolCallModuleState RedactToolCallState(ToolCallModuleState source)
    {
        var redacted = source.Clone();
        ToolCallModule.ScrubLegacyPayloadFields(redacted);
        foreach (var pending in redacted.PendingApprovals.Values)
        {
            pending.ProtectedMaterialReference = null;
            pending.ProtectedMaterialDigestSha256 = string.Empty;
        }

        foreach (var pending in redacted.PendingExecutions.Values)
        {
            pending.ProtectedMaterialReference = null;
            pending.ProtectedMaterialDigestSha256 = string.Empty;
        }

        foreach (var completion in redacted.Completions)
            completion.ProtectedMaterialReference = null;

        return redacted;
    }

    private static void RedactKernelExecutionState(WorkflowRunState state)
    {
        foreach (var pair in state.ExecutionStates.ToList())
        {
            if (!pair.Value.Is(WorkflowExecutionKernelState.Descriptor))
                continue;

            var kernelState = pair.Value.Unpack<WorkflowExecutionKernelState>() ??
                              new WorkflowExecutionKernelState();
            state.ExecutionStates[pair.Key] = Any.Pack(RedactKernelState(kernelState));
        }
    }

    private static WorkflowExecutionKernelState RedactKernelState(
        WorkflowExecutionKernelState source)
    {
        var redacted = source.Clone();
        var normalized = redacted.NormalizedValues;
        if (normalized == null)
            return redacted;

        foreach (var canonical in normalized.CanonicalValues.Values)
        {
            if (canonical.Released == null)
                continue;
            canonical.Value = string.Empty;
            canonical.Released.Digest = RedactedDigest();
        }
        foreach (var name in normalized.ReleasedBindings.Keys.ToArray())
        {
            var released = normalized.ReleasedBindings[name].Clone();
            released.Digest = RedactedDigest();
            normalized.ReleasedBindings[name] = released;
        }
        foreach (var completed in normalized.CompletedSteps.Values)
            RedactCompletionDigests(completed);
        foreach (var completed in normalized.AcceptedCompletions.Values)
            RedactCompletionDigests(completed);
        foreach (var completed in normalized.InheritedCompletions.Values)
            RedactCompletionDigests(completed);
        return redacted;
    }

    private static WorkflowValueDigest RedactedDigest() => new() { Redacted = true };

    private static void RedactCompletionDigests(WorkflowCompletedStepState completed)
    {
        completed.OutputDigest = null;
        completed.AssignedValueDigest = null;
        completed.AssignedMirrorDigest = null;
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
        delta.UnattendedEffectAuthorization = null;
        delta.ClearUnattendedEffectAuthorization = true;
    }
}
