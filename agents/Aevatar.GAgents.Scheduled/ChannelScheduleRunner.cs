using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Encapsulates cron-driven self-scheduling duplicated across SkillRunnerGAgent / WorkflowAgentGAgent.
/// Owns the durable-callback lease; bound via delegates to the hosting GAgent's protected schedule /
/// cancel / persist helpers so we stay on the composition side of the "ISchedulable + extensions" RFC.
/// </summary>
internal sealed class ChannelScheduleRunner
{
    private readonly string _callbackId;
    private readonly Func<ISchedulable> _schedulableSource;
    private readonly Func<IMessage> _triggerFactory;
    private readonly Func<DateTimeOffset, Task> _persistNextRunEventAsync;
    private readonly Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> _scheduleTimeoutAsync;
    private readonly Func<RuntimeCallbackLease, CancellationToken, Task> _cancelCallbackAsync;
    private readonly ILogger _logger;
    private readonly string _ownerDescription;

    private RuntimeCallbackLease? _lease;

    public ChannelScheduleRunner(
        string callbackId,
        Func<ISchedulable> schedulableSource,
        Func<IMessage> triggerFactory,
        Func<DateTimeOffset, Task> persistNextRunEventAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleTimeoutAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync,
        ILogger logger,
        string ownerDescription)
    {
        _callbackId = callbackId ?? throw new ArgumentNullException(nameof(callbackId));
        _schedulableSource = schedulableSource ?? throw new ArgumentNullException(nameof(schedulableSource));
        _triggerFactory = triggerFactory ?? throw new ArgumentNullException(nameof(triggerFactory));
        _persistNextRunEventAsync = persistNextRunEventAsync ?? throw new ArgumentNullException(nameof(persistNextRunEventAsync));
        _scheduleTimeoutAsync = scheduleTimeoutAsync ?? throw new ArgumentNullException(nameof(scheduleTimeoutAsync));
        _cancelCallbackAsync = cancelCallbackAsync ?? throw new ArgumentNullException(nameof(cancelCallbackAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownerDescription = ownerDescription ?? string.Empty;
    }

    /// <summary>Revives the schedule after actor activation when the previous run window has lapsed.</summary>
    public Task BootstrapOnActivateAsync(CancellationToken ct)
    {
        var schedule = _schedulableSource().Schedule;
        if (!schedule.Enabled || string.IsNullOrWhiteSpace(schedule.Cron))
            return Task.CompletedTask;

        var nextRun = schedule.NextRunAt;
        if (nextRun != null && nextRun.ToDateTimeOffset() > DateTimeOffset.UtcNow)
            return Task.CompletedTask;

        return ScheduleNextRunAsync(DateTimeOffset.UtcNow, ct);
    }

    /// <summary>Computes the next cron occurrence and (re)places the durable callback lease.</summary>
    public async Task ScheduleNextRunAsync(DateTimeOffset fromUtc, CancellationToken ct)
    {
        var schedule = _schedulableSource().Schedule;
        if (!schedule.Enabled || string.IsNullOrWhiteSpace(schedule.Cron))
            return;

        if (!ChannelScheduleCalculator.TryGetNextOccurrence(
                schedule.Cron,
                schedule.Timezone,
                fromUtc,
                out var nextRunAtUtc,
                out var error))
        {
            _logger.LogWarning("{Owner} could not compute next run: {Error}", _ownerDescription, error);
            return;
        }

        var dueTime = ChannelScheduleCalculator.ComputeDueTime(nextRunAtUtc, DateTimeOffset.UtcNow);

        if (_lease != null)
            await _cancelCallbackAsync(_lease, ct);

        _lease = await _scheduleTimeoutAsync(_callbackId, dueTime, _triggerFactory(), ct);
        await _persistNextRunEventAsync(nextRunAtUtc);
    }

    /// <summary>Cancels any outstanding next-run lease.</summary>
    public async Task CancelAsync(CancellationToken ct)
    {
        if (_lease == null)
            return;

        await _cancelCallbackAsync(_lease, ct);
        _lease = null;
    }
}
