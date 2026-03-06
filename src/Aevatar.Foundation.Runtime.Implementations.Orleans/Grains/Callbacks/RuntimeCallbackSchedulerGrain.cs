using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public sealed class RuntimeCallbackSchedulerGrain : Grain, IRuntimeCallbackSchedulerGrain, IRemindable
{
    private const string ReminderNamePrefix = "runtime-callback:";
    private static readonly TimeSpan OneShotReminderPeriod = TimeSpan.FromDays(36500);

    private readonly IPersistentState<RuntimeCallbackSchedulerGrainState> _state;
    private Aevatar.Foundation.Abstractions.IStreamProvider _streams = null!;

    public RuntimeCallbackSchedulerGrain(
        [PersistentState("runtime-callback-scheduler", OrleansRuntimeConstants.GrainStateStorageName)]
        IPersistentState<RuntimeCallbackSchedulerGrainState> state)
    {
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _streams = ServiceProvider.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        _state.State.ReminderCallbacks ??= [];
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<long> ScheduleTimeoutAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs)
    {
        ValidateScheduleRequest(callbackId, envelopeBytes, dueTimeMs);
        var dueTime = TimeSpan.FromMilliseconds(dueTimeMs);
        var nextGeneration = await ResetExistingCallbackAndGetNextGenerationAsync(callbackId);
        await UpsertReminderCallbackAsync(
            callbackId,
            nextGeneration,
            periodic: false,
            periodMs: 0,
            envelopeBytes,
            dueTime);
        return nextGeneration;
    }

    public async Task<long> ScheduleTimerAsync(
        string callbackId,
        byte[] envelopeBytes,
        int dueTimeMs,
        int periodMs)
    {
        ValidateScheduleRequest(callbackId, envelopeBytes, dueTimeMs);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(periodMs, 0);

        var dueTime = TimeSpan.FromMilliseconds(dueTimeMs);
        var nextGeneration = await ResetExistingCallbackAndGetNextGenerationAsync(callbackId);
        await UpsertReminderCallbackAsync(
            callbackId,
            nextGeneration,
            periodic: true,
            periodMs,
            envelopeBytes,
            dueTime);
        return nextGeneration;
    }

    public async Task CancelAsync(string callbackId, long expectedGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        if (!_state.State.ReminderCallbacks.TryGetValue(callbackId, out var reminderCallback))
            return;

        if (expectedGeneration > 0 && reminderCallback.Generation != expectedGeneration)
            return;

        _state.State.ReminderCallbacks.Remove(callbackId);
        await _state.WriteStateAsync();
        await TryUnregisterReminderAsync(callbackId);
    }

    private static void ValidateScheduleRequest(string callbackId, byte[] envelopeBytes, int dueTimeMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(envelopeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTimeMs, 0);
    }

    private async Task<long> ResetExistingCallbackAndGetNextGenerationAsync(string callbackId)
    {
        var generation = 0L;
        if (_state.State.ReminderCallbacks.TryGetValue(callbackId, out var reminderCallback))
        {
            generation = Math.Max(generation, reminderCallback.Generation);
            _state.State.ReminderCallbacks.Remove(callbackId);
            await _state.WriteStateAsync();
            await TryUnregisterReminderAsync(callbackId);
        }

        return generation + 1;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        _ = status;
        if (!TryParseReminderName(reminderName, out var callbackId))
            return;

        if (!_state.State.ReminderCallbacks.TryGetValue(callbackId, out var scheduled))
            return;

        var fireIndex = scheduled.FireIndex + 1;
        await PublishScheduledEnvelopeAsync(
            callbackId,
            scheduled.Generation,
            fireIndex,
            scheduled.EnvelopeBytes,
            CancellationToken.None);

        if (!scheduled.Periodic)
        {
            _state.State.ReminderCallbacks.Remove(callbackId);
            await _state.WriteStateAsync();
            var reminder = await this.GetReminder(reminderName);
            if (reminder != null)
                await this.UnregisterReminder(reminder);
            return;
        }

        scheduled.FireIndex = fireIndex;
        _state.State.ReminderCallbacks[callbackId] = scheduled;
        await _state.WriteStateAsync();
    }

    private async Task UpsertReminderCallbackAsync(
        string callbackId,
        long generation,
        bool periodic,
        int periodMs,
        byte[] envelopeBytes,
        TimeSpan dueTime)
    {
        var reminderName = BuildReminderName(callbackId);
        _state.State.ReminderCallbacks[callbackId] = new ReminderScheduledCallbackState
        {
            Generation = generation,
            Periodic = periodic,
            PeriodMs = periodMs,
            EnvelopeBytes = envelopeBytes,
            FireIndex = 0,
        };
        await _state.WriteStateAsync();

        var period = periodic
            ? TimeSpan.FromMilliseconds(periodMs)
            : OneShotReminderPeriod;
        try
        {
            await this.RegisterOrUpdateReminder(reminderName, dueTime, period);
        }
        catch
        {
            _state.State.ReminderCallbacks.Remove(callbackId);
            await _state.WriteStateAsync();
            throw;
        }
    }

    private async Task PublishScheduledEnvelopeAsync(
        string callbackId,
        long generation,
        int fireIndex,
        byte[] envelopeBytes,
        CancellationToken ct)
    {
        var envelope = RuntimeCallbackEnvelopeFactory.CreateFiredEnvelope(
            this.GetPrimaryKeyString(),
            callbackId,
            generation,
            fireIndex,
            EventEnvelope.Parser.ParseFrom(envelopeBytes));

        await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(envelope, ct);
    }

    private async Task TryUnregisterReminderAsync(string callbackId)
    {
        var reminderName = BuildReminderName(callbackId);
        var reminder = await this.GetReminder(reminderName);
        if (reminder != null)
            await this.UnregisterReminder(reminder);
    }

    private static string BuildReminderName(string callbackId) =>
        string.Concat(ReminderNamePrefix, callbackId);

    private static bool TryParseReminderName(string reminderName, out string callbackId)
    {
        callbackId = string.Empty;
        if (string.IsNullOrWhiteSpace(reminderName) ||
            !reminderName.StartsWith(ReminderNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        callbackId = reminderName[ReminderNamePrefix.Length..];
        return !string.IsNullOrWhiteSpace(callbackId);
    }
}
