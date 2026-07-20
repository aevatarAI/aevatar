using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.Schedules;

public sealed partial class ScheduledDispatchState
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

    public DateTimeOffset? LastOverdueFireAt
    {
        get => LastOverdueFireAtUtcValue == null ? null : LastOverdueFireAtUtcValue.ToDateTimeOffset();
        set => LastOverdueFireAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    public DateTimeOffset? DeletedAt
    {
        get => DeletedAtUtcValue == null ? null : DeletedAtUtcValue.ToDateTimeOffset();
        set => DeletedAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    public DateTimeOffset? OneShotFireAt
    {
        get => OneShotFireAtUtcValue == null ? null : OneShotFireAtUtcValue.ToDateTimeOffset();
        set => OneShotFireAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    public DateTimeOffset? CompletedAt
    {
        get => CompletedAtUtcValue == null ? null : CompletedAtUtcValue.ToDateTimeOffset();
        set => CompletedAtUtcValue = value.HasValue ? ToTimestamp(value.Value) : null;
    }

    private static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();
}

public static class ScheduledDispatchRuntimeCallbackLeaseStateCodec
{
    public static ScheduledDispatchRuntimeCallbackLeaseState? ToState(RuntimeCallbackLease? lease)
    {
        if (lease == null)
            return null;

        return new ScheduledDispatchRuntimeCallbackLeaseState
        {
            ActorId = lease.ActorId,
            CallbackId = lease.CallbackId,
            Generation = lease.Generation,
            Backend = lease.Backend == RuntimeCallbackBackend.Dedicated
                ? ScheduledDispatchRuntimeCallbackBackendState.Dedicated
                : ScheduledDispatchRuntimeCallbackBackendState.InMemory,
            SlotEpoch = lease.SlotEpoch,
        };
    }

    public static RuntimeCallbackLease? ToRuntime(ScheduledDispatchRuntimeCallbackLeaseState? state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.ActorId) || string.IsNullOrWhiteSpace(state.CallbackId))
            return null;

        return new RuntimeCallbackLease(
            state.ActorId,
            state.CallbackId,
            state.Generation,
            state.Backend == ScheduledDispatchRuntimeCallbackBackendState.Dedicated
                ? RuntimeCallbackBackend.Dedicated
                : RuntimeCallbackBackend.InMemory)
        {
            SlotEpoch = state.SlotEpoch,
        };
    }
}
