using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Infrastructure.Schedules;

internal sealed class WorkflowScheduleActorPort : IWorkflowScheduleActorPort
{
    private readonly IScheduledDispatchActorPort _scheduledDispatchActorPort;

    public WorkflowScheduleActorPort(IScheduledDispatchActorPort scheduledDispatchActorPort)
    {
        _scheduledDispatchActorPort = scheduledDispatchActorPort ?? throw new ArgumentNullException(nameof(scheduledDispatchActorPort));
    }

    public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default) =>
        _scheduledDispatchActorPort.EnsureScheduleActorAsync(scheduleId, ct);

    public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default) =>
        _scheduledDispatchActorPort.ResolveScheduleActorAsync(scheduleId, ct);

    public Task<DispatchAdmission> DispatchCreateAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        ScheduledDispatchPreparation dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        return _scheduledDispatchActorPort.DispatchCreateAsync(
            actorId,
            CreateScheduledDispatchConfiguration(configuration, dispatch),
            ct);
    }

    public Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        ScheduledDispatchPreparation dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        return _scheduledDispatchActorPort.DispatchUpdateAsync(
            actorId,
            CreateScheduledDispatchConfiguration(configuration, dispatch),
            ct);
    }

    public Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default) =>
        _scheduledDispatchActorPort.DispatchEnableAsync(actorId, reason, ct);

    public Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default) =>
        _scheduledDispatchActorPort.DispatchDisableAsync(actorId, reason, ct);

    public Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default) =>
        _scheduledDispatchActorPort.DispatchRunNowAsync(actorId, scheduledFireAt, ct);

    private static ScheduledDispatchConfiguration CreateScheduledDispatchConfiguration(
        WorkflowScheduleConfiguration configuration,
        ScheduledDispatchPreparation dispatch) =>
        new(
            configuration.ScheduleId,
            configuration.DisplayName,
            dispatch.TargetActorId,
            dispatch.TriggerEnvelope,
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            configuration.Headers,
            dispatch.PayloadTypeUrl,
            dispatch.WorkflowTarget ?? new WorkflowScheduleTargetDescriptor(
                configuration.WorkflowName,
                configuration.Prompt,
                configuration.ScopeId ?? string.Empty,
                configuration.SourceActorId ?? string.Empty));
}
