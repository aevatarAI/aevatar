using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Schedules;

internal sealed class ScheduledDispatchActorPort : IScheduledDispatchActorPort
{
    private const string PublisherId = "scheduled.dispatch.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ScheduledDispatchActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actorId = ScheduledDispatchActorId.Format(scheduleId);
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing.Id;

        var actor = await _runtime.CreateAsync<ScheduledDispatchGAgent>(actorId, ct);
        return actor.Id;
    }

    public async Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actorId = ScheduledDispatchActorId.Format(scheduleId);
        var existing = await _runtime.GetAsync(actorId);
        return existing?.Id;
    }

    public Task<DispatchAdmission> DispatchConfigureAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(configuration);

        var command = new ScheduledDispatchConfigureCommand
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            TargetActorId = configuration.TargetActorId,
            TriggerEnvelope = configuration.TriggerEnvelope.Clone(),
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            PayloadTypeUrl = configuration.PayloadTypeUrl,
        };
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;

        return DispatchAsync(actorId, command, ct);
    }

    public Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new ScheduledDispatchEnableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new ScheduledDispatchDisableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt.ToUniversalTime()),
            Manual = true,
        }, ct);
    }

    private Task<DispatchAdmission> DispatchAsync<TCommand>(
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
