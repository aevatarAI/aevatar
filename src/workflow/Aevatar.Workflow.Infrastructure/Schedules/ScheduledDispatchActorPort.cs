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

    public async Task<DispatchAdmission> DispatchCreateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(configuration);
        ct.ThrowIfCancellationRequested();
        var state = await GetScheduleStateAsync(actorId);
        if (IsConfigured(state))
            throw new WorkflowScheduleConflictException(
                configuration.ScheduleId,
                $"Workflow schedule '{configuration.ScheduleId}' already exists.");

        var command = new ScheduledDispatchCreateCommand
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            TargetActorId = configuration.TargetActorId ?? string.Empty,
            TriggerEnvelope = configuration.TriggerEnvelope.Clone(),
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            PayloadTypeUrl = configuration.PayloadTypeUrl,
            WorkflowTarget = CreateWorkflowTarget(configuration.WorkflowTarget),
        };
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;

        return await DispatchAsync(actorId, command, ct);
    }

    public async Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(configuration);
        ct.ThrowIfCancellationRequested();
        var state = await GetScheduleStateAsync(actorId);
        if (!IsConfigured(state))
            throw new WorkflowScheduleNotFoundException(configuration.ScheduleId);

        var command = new ScheduledDispatchUpdateCommand
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            TargetActorId = configuration.TargetActorId ?? string.Empty,
            TriggerEnvelope = configuration.TriggerEnvelope.Clone(),
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            PayloadTypeUrl = configuration.PayloadTypeUrl,
            WorkflowTarget = CreateWorkflowTarget(configuration.WorkflowTarget),
        };
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;

        return await DispatchAsync(actorId, command, ct);
    }

    public async Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await EnsureConfiguredAsync(actorId, ct);
        return await DispatchAsync(actorId, new ScheduledDispatchEnableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await EnsureConfiguredAsync(actorId, ct);
        return await DispatchAsync(actorId, new ScheduledDispatchDisableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await EnsureConfiguredAsync(actorId, ct);
        return await DispatchAsync(actorId, new ScheduledDispatchFireCommand
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

    private async Task EnsureConfiguredAsync(string actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = await GetScheduleStateAsync(actorId);
        if (!IsConfigured(state))
            throw new WorkflowScheduleNotFoundException(ScheduledDispatchActorId.Unformat(actorId));
    }

    private async Task<ScheduledDispatchState?> GetScheduleStateAsync(string actorId)
    {
        var actor = await _runtime.GetAsync(actorId);
        if (actor == null)
            return null;

        return actor.Agent is IAgent<ScheduledDispatchState> typed
            ? typed.State
            : null;
    }

    private static bool IsConfigured(ScheduledDispatchState? state) =>
        state != null &&
        !string.IsNullOrWhiteSpace(state.ScheduleId) &&
        !string.IsNullOrWhiteSpace(state.CronExpression) &&
        state.TriggerEnvelope?.Payload != null;

    private static WorkflowScheduleTargetState CreateWorkflowTarget(WorkflowScheduleTargetDescriptor? descriptor)
    {
        if (descriptor == null)
            return new WorkflowScheduleTargetState();

        return new WorkflowScheduleTargetState
        {
            WorkflowName = descriptor.WorkflowName ?? string.Empty,
            Prompt = descriptor.Prompt ?? string.Empty,
            ScopeId = descriptor.ScopeId ?? string.Empty,
            SourceActorId = descriptor.SourceActorId ?? string.Empty,
        };
    }
}
