using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledDispatchActorPort : IScheduledDispatchActorPort
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
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var command = CreateCreateCommand(configuration, dispatch);
        return await DispatchAsync(actorId, command, ct);
    }

    public async Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var command = CreateUpdateCommand(configuration, dispatch);
        return await DispatchAsync(actorId, command, ct);
    }

    public async Task<DispatchAdmission> DispatchEnsureAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var command = CreateEnsureCommand(configuration, dispatch);
        return await DispatchAsync(actorId, command, ct);
    }

    public async Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchEnableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchDisableCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task<DispatchAdmission> DispatchDeleteAsync(
        string actorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchDeleteCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
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

    private static ScheduledDispatchScheduleKindState ToStateScheduleKind(ScheduledDispatchScheduleKind kind) =>
        kind switch
        {
            ScheduledDispatchScheduleKind.Workflow => ScheduledDispatchScheduleKindState.Workflow,
            ScheduledDispatchScheduleKind.SkillRunner => ScheduledDispatchScheduleKindState.SkillRunner,
            _ => ScheduledDispatchScheduleKindState.Generic,
        };

    private static ScheduledDispatchCreateCommand CreateCreateCommand(
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        var command = new ScheduledDispatchCreateCommand();
        PopulateConfigureCommand(command, configuration, dispatch);
        return command;
    }

    private static ScheduledDispatchUpdateCommand CreateUpdateCommand(
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        var command = new ScheduledDispatchUpdateCommand();
        PopulateConfigureCommand(command, configuration, dispatch);
        return command;
    }

    private static ScheduledDispatchEnsureCommand CreateEnsureCommand(
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        var command = new ScheduledDispatchEnsureCommand();
        PopulateConfigureCommand(command, configuration, dispatch);
        return command;
    }

    private static void PopulateConfigureCommand(
        ScheduledDispatchCreateCommand command,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        command.ScheduleId = configuration.ScheduleId;
        command.DisplayName = configuration.DisplayName;
        command.TargetActorId = dispatch.TargetActorId ?? string.Empty;
        command.TriggerEnvelope = dispatch.TriggerEnvelope.Clone();
        command.CronExpression = configuration.CronExpression;
        command.Timezone = configuration.Timezone;
        command.Enabled = configuration.Enabled;
        command.PayloadTypeUrl = dispatch.PayloadTypeUrl;
        command.Target = CreateTargetState(dispatch.Descriptor);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;
    }

    private static void PopulateConfigureCommand(
        ScheduledDispatchUpdateCommand command,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        command.ScheduleId = configuration.ScheduleId;
        command.DisplayName = configuration.DisplayName;
        command.TargetActorId = dispatch.TargetActorId ?? string.Empty;
        command.TriggerEnvelope = dispatch.TriggerEnvelope.Clone();
        command.CronExpression = configuration.CronExpression;
        command.Timezone = configuration.Timezone;
        command.Enabled = configuration.Enabled;
        command.PayloadTypeUrl = dispatch.PayloadTypeUrl;
        command.Target = CreateTargetState(dispatch.Descriptor);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;
    }

    private static void PopulateConfigureCommand(
        ScheduledDispatchEnsureCommand command,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        command.ScheduleId = configuration.ScheduleId;
        command.DisplayName = configuration.DisplayName;
        command.TargetActorId = dispatch.TargetActorId ?? string.Empty;
        command.TriggerEnvelope = dispatch.TriggerEnvelope.Clone();
        command.CronExpression = configuration.CronExpression;
        command.Timezone = configuration.Timezone;
        command.Enabled = configuration.Enabled;
        command.PayloadTypeUrl = dispatch.PayloadTypeUrl;
        command.Target = CreateTargetState(dispatch.Descriptor);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;
    }

    private static ScheduledDispatchTargetState CreateTargetState(ScheduledDispatchTargetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.Kind switch
        {
            ScheduledDispatchTargetKind.ServiceInvocation => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = CreateServiceInvocationTarget(descriptor.ServiceInvocation),
            },
            ScheduledDispatchTargetKind.Envelope => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = descriptor.ActorId ?? string.Empty,
                Envelope = descriptor.Envelope?.Clone(),
            },
            _ => throw new ArgumentException($"Unsupported scheduled dispatch target kind '{descriptor.Kind}'.", nameof(descriptor)),
        };
    }

    private static ScheduledServiceInvocationTargetState CreateServiceInvocationTarget(
        ScheduledServiceInvocationTargetDescriptor? descriptor)
    {
        if (descriptor == null)
            return new ScheduledServiceInvocationTargetState();

        return new ScheduledServiceInvocationTargetState
        {
            Identity = descriptor.Identity.Clone(),
            EndpointId = descriptor.EndpointId ?? string.Empty,
            Payload = descriptor.Payload.Clone(),
            RevisionId = descriptor.RevisionId ?? string.Empty,
            Caller = descriptor.Caller?.Clone(),
            Auth = CreateAuthState(descriptor.Auth),
        };
    }

    private static ScheduledServiceInvocationAuthState? CreateAuthState(ScheduledServiceInvocationAuth? auth)
    {
        if (auth == null)
            return null;
        if (auth.ScopeOwnerNyxId != null)
        {
            return new ScheduledServiceInvocationAuthState
            {
                ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState
                {
                    Scope = auth.ScopeOwnerNyxId.Scope,
                },
            };
        }
        if (auth.SenderNyxId == null)
            return null;

        return new ScheduledServiceInvocationAuthState
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                {
                    Platform = auth.SenderNyxId.Subject.Platform,
                    Tenant = auth.SenderNyxId.Subject.Tenant,
                    ExternalUserId = auth.SenderNyxId.Subject.ExternalUserId,
                },
                Scope = auth.SenderNyxId.Scope,
            },
        };
    }
}
