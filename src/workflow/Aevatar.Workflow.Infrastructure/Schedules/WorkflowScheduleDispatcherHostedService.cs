using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Schedules;

internal sealed class WorkflowScheduleDispatcherHostedService : BackgroundService
{
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);

    private readonly IWorkflowScheduleStore _store;
    private readonly IWorkflowScheduleApplicationService _schedules;
    private readonly WorkflowScheduleStoreOptions _storeOptions;
    private readonly WorkflowScheduleWakeupOptions _wakeupOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkflowScheduleDispatcherHostedService> _logger;

    public WorkflowScheduleDispatcherHostedService(
        IWorkflowScheduleStore store,
        IWorkflowScheduleApplicationService schedules,
        IOptions<WorkflowScheduleStoreOptions> storeOptions,
        IOptions<WorkflowScheduleWakeupOptions> wakeupOptions,
        ILogger<WorkflowScheduleDispatcherHostedService> logger,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _storeOptions = storeOptions?.Value ?? new WorkflowScheduleStoreOptions();
        _wakeupOptions = wakeupOptions?.Value ?? new WorkflowScheduleWakeupOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_storeOptions.EnableDispatcher || _wakeupOptions.UseOrleansReminders)
            return;

        await DispatchDueSchedulesAsync(stoppingToken);

        using var timer = new PeriodicTimer(GetPollInterval());
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await DispatchDueSchedulesAsync(stoppingToken);
        }
    }

    internal async Task DispatchDueSchedulesAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().ToUniversalTime();
        var maxDueSchedules = Math.Max(1, _storeOptions.MaxDueSchedulesPerTick);
        var definitions = await _store.ListAsync(ct);
        var dueSchedules = definitions
            .Where(x =>
                x.Status == WorkflowScheduleStatus.Enabled &&
                x.NextFireAtUtc != null &&
                x.NextFireAtUtc.Value.ToUniversalTime() <= now)
            .OrderBy(x => x.NextFireAtUtc)
            .ThenBy(x => x.ScheduleId, StringComparer.Ordinal)
            .Take(maxDueSchedules)
            .ToList();

        foreach (var definition in dueSchedules)
        {
            ct.ThrowIfCancellationRequested();
            var scheduledFireAtUtc = definition.NextFireAtUtc!.Value.ToUniversalTime();
            try
            {
                var result = await _schedules.RunNowAsync(
                    new WorkflowScheduleFireRequest(
                        definition.ScheduleId,
                        scheduledFireAtUtc,
                        Force: false,
                        AdvanceSchedule: true),
                    ct);
                if (!result.Succeeded)
                {
                    _logger.LogWarning(
                        "Workflow schedule fire was rejected. scheduleId={ScheduleId} scheduledFireAtUtc={ScheduledFireAtUtc} errorCode={ErrorCode}",
                        definition.ScheduleId,
                        scheduledFireAtUtc,
                        result.Error.Code);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Workflow schedule fire failed. scheduleId={ScheduleId} scheduledFireAtUtc={ScheduledFireAtUtc}",
                    definition.ScheduleId,
                    scheduledFireAtUtc);
            }
        }
    }

    private TimeSpan GetPollInterval() =>
        _storeOptions.DispatcherPollInterval < MinimumPollInterval
            ? MinimumPollInterval
            : _storeOptions.DispatcherPollInterval;
}
