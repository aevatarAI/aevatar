using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowScheduleState
{
    public DateTimeOffset CreatedAt
    {
        get => ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset? NextFireAt
    {
        get => NextFireAtUtcValue == null ? null : NextFireAtUtcValue.ToDateTimeOffset();
        set => NextFireAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    public DateTimeOffset? LastFireAt
    {
        get => LastFireAtUtcValue == null ? null : LastFireAtUtcValue.ToDateTimeOffset();
        set => LastFireAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    private static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();
}

internal static class WorkflowScheduleRuntimeCallbackLeaseStateCodec
{
    public static WorkflowScheduleRuntimeCallbackLeaseState? ToState(RuntimeCallbackLease? lease)
    {
        if (lease == null)
            return null;

        return new WorkflowScheduleRuntimeCallbackLeaseState
        {
            ActorId = lease.ActorId,
            CallbackId = lease.CallbackId,
            Generation = lease.Generation,
            Backend = lease.Backend == RuntimeCallbackBackend.Dedicated
                ? WorkflowScheduleRuntimeCallbackBackendState.Dedicated
                : WorkflowScheduleRuntimeCallbackBackendState.InMemory,
        };
    }

    public static RuntimeCallbackLease? ToRuntime(WorkflowScheduleRuntimeCallbackLeaseState? state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.ActorId) || string.IsNullOrWhiteSpace(state.CallbackId))
            return null;

        return new RuntimeCallbackLease(
            state.ActorId,
            state.CallbackId,
            state.Generation,
            state.Backend == WorkflowScheduleRuntimeCallbackBackendState.Dedicated
                ? RuntimeCallbackBackend.Dedicated
                : RuntimeCallbackBackend.InMemory);
    }
}
