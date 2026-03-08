using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRuntimeCallbackLeaseStateCodec
{
    public static WorkflowRuntimeCallbackLeaseState? ToState(RuntimeCallbackLease? lease)
    {
        if (lease == null)
            return null;

        return new WorkflowRuntimeCallbackLeaseState
        {
            ActorId = lease.ActorId,
            CallbackId = lease.CallbackId,
            Generation = lease.Generation,
            Backend = lease.Backend switch
            {
                RuntimeCallbackBackend.Dedicated => WorkflowRuntimeCallbackBackendState.Dedicated,
                _ => WorkflowRuntimeCallbackBackendState.InMemory,
            },
        };
    }

    public static RuntimeCallbackLease? ToRuntime(WorkflowRuntimeCallbackLeaseState? state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.ActorId) || string.IsNullOrWhiteSpace(state.CallbackId))
            return null;

        return new RuntimeCallbackLease(
            state.ActorId,
            state.CallbackId,
            state.Generation,
            state.Backend == WorkflowRuntimeCallbackBackendState.Dedicated
                ? RuntimeCallbackBackend.Dedicated
                : RuntimeCallbackBackend.InMemory);
    }
}
