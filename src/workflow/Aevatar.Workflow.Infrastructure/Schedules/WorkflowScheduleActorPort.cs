using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Schedules;

internal sealed class WorkflowScheduleActorPort : IWorkflowScheduleActorPort
{
    private const string PublisherId = "workflow.schedule.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public WorkflowScheduleActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
    {
        var actorId = WorkflowScheduleActorId.Format(scheduleId);
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing.Id;

        var actor = await _runtime.CreateAsync<WorkflowScheduleGAgent>(actorId, ct);
        return actor.Id;
    }

    public async Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actorId = WorkflowScheduleActorId.Format(scheduleId);
        var existing = await _runtime.GetAsync(actorId);
        return existing?.Id;
    }

    public Task DispatchConfigureAsync(
        string actorId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(configuration);

        var command = new WorkflowScheduleConfigureCommand
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            WorkflowName = configuration.WorkflowName,
            Prompt = configuration.Prompt,
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            ScopeId = configuration.ScopeId ?? string.Empty,
            ActorId = configuration.ActorId ?? string.Empty,
        };
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;

        return DispatchAsync(actorId, command, ct);
    }

    public Task DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new WorkflowScheduleEnableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public Task DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new WorkflowScheduleDisableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public Task DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new WorkflowScheduleFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt.ToUniversalTime()),
            Manual = true,
        }, ct);
    }

    private Task DispatchAsync<TCommand>(
        string actorId,
        TCommand command,
        CancellationToken ct)
        where TCommand : Google.Protobuf.IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
        return _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }
}
