using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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

    private static ScheduledDispatchScheduleModeState ToStateScheduleMode(ScheduledDispatchScheduleMode mode) =>
        mode == ScheduledDispatchScheduleMode.OneShotAtUtc
            ? ScheduledDispatchScheduleModeState.OneShotAtUtc
            : ScheduledDispatchScheduleModeState.RecurringCron;

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
        command.Target = CreateTargetState(dispatch.Descriptor, configuration.CredentialRequirementTargetKind);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        command.ScheduleMode = ToStateScheduleMode(configuration.ScheduleMode);
        command.OneShotFireAt = configuration.OneShotFireAt.HasValue
            ? Timestamp.FromDateTimeOffset(configuration.OneShotFireAt.Value.ToUniversalTime())
            : null;
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
        command.Target = CreateTargetState(dispatch.Descriptor, configuration.CredentialRequirementTargetKind);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        command.ScheduleMode = ToStateScheduleMode(configuration.ScheduleMode);
        command.OneShotFireAt = configuration.OneShotFireAt.HasValue
            ? Timestamp.FromDateTimeOffset(configuration.OneShotFireAt.Value.ToUniversalTime())
            : null;
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
        command.Target = CreateTargetState(dispatch.Descriptor, configuration.CredentialRequirementTargetKind);
        command.ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind);
        command.ScheduleMode = ToStateScheduleMode(configuration.ScheduleMode);
        command.OneShotFireAt = configuration.OneShotFireAt.HasValue
            ? Timestamp.FromDateTimeOffset(configuration.OneShotFireAt.Value.ToUniversalTime())
            : null;
        foreach (var (key, value) in configuration.Headers)
            command.Headers[key] = value;
    }

    private static ScheduledDispatchTargetState CreateTargetState(
        ScheduledDispatchTargetDescriptor descriptor,
        ScheduledDispatchCredentialRequirementTargetKind credentialRequirementTargetKind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.Kind switch
        {
            ScheduledDispatchTargetKind.ServiceInvocation => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = CreateServiceInvocationTarget(descriptor.ServiceInvocation),
                CredentialRequirementTargetKind = ToStateCredentialRequirementTargetKind(credentialRequirementTargetKind),
            },
            ScheduledDispatchTargetKind.Envelope => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = descriptor.ActorId ?? string.Empty,
                Envelope = descriptor.Envelope?.Clone(),
                CredentialRequirementTargetKind = ToStateCredentialRequirementTargetKind(credentialRequirementTargetKind),
            },
            _ => throw new ArgumentException($"Unsupported scheduled dispatch target kind '{descriptor.Kind}'.", nameof(descriptor)),
        };
    }

    private static ScheduledDispatchCredentialRequirementTargetKindState ToStateCredentialRequirementTargetKind(
        ScheduledDispatchCredentialRequirementTargetKind targetKind) =>
        targetKind switch
        {
            ScheduledDispatchCredentialRequirementTargetKind.Envelope =>
                ScheduledDispatchCredentialRequirementTargetKindState.Envelope,
            ScheduledDispatchCredentialRequirementTargetKind.StaticService =>
                ScheduledDispatchCredentialRequirementTargetKindState.StaticService,
            ScheduledDispatchCredentialRequirementTargetKind.ScriptingService =>
                ScheduledDispatchCredentialRequirementTargetKindState.ScriptingService,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService =>
                ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService,
            ScheduledDispatchCredentialRequirementTargetKind.Connector =>
                ScheduledDispatchCredentialRequirementTargetKindState.Connector,
            _ => ScheduledDispatchCredentialRequirementTargetKindState.Unspecified,
        };

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
            AuthorizationFact = CreateAuthorizationFactState(descriptor.AuthorizationFact),
        };
    }

    private static ScheduledInvocationAuthorizationFactState? CreateAuthorizationFactState(
        ScheduledInvocationAuthorizationFact? fact)
    {
        if (fact == null)
            return null;

        var state = new ScheduledInvocationAuthorizationFactState
        {
            PermissionDigest = fact.PermissionDigest,
            PolicyVersion = fact.PolicyVersion,
            Owner = new ScheduledInvocationAuthorizationOwnerState
            {
                Authority = fact.Owner.Authority,
                OwnerKind = fact.Owner.OwnerKind,
                OwnerSubject = fact.Owner.OwnerSubject,
            },
            Scopes = fact.Scopes,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(fact.ExpiresAt),
            ServiceGrantsNotRequired = fact.ServiceGrantsNotRequired,
            Disclosure = new ScheduledInvocationAuthorizationDisclosureState
            {
                DedicatedToSchedule = fact.Disclosure.DedicatedToSchedule,
                SecretManagedByAevatar = fact.Disclosure.SecretManagedByAevatar,
                BrowserReceivesRawKey = fact.Disclosure.BrowserReceivesRawKey,
                DeleteRevokesCredential = fact.Disclosure.DeleteRevokesCredential,
                PauseResumeRevokesCredential = fact.Disclosure.PauseResumeRevokesCredential,
            },
            Authority = new ScheduledInvocationAuthorizationAuthorityState
            {
                MemberStateVersion = fact.Authority.MemberStateVersion,
                WorkflowStateVersion = fact.Authority.WorkflowStateVersion,
                ConnectorStateVersion = fact.Authority.ConnectorStateVersion,
                OwnerLlmStateVersion = fact.Authority.OwnerLlmStateVersion,
                CatalogStateVersion = fact.Authority.CatalogStateVersion,
                CatalogObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(fact.Authority.CatalogObservedAt),
                CatalogFreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(fact.Authority.CatalogFreshUntil),
                CatalogExternalRevision = fact.Authority.CatalogExternalRevision,
                CatalogContentDigest = fact.Authority.CatalogContentDigest,
            },
        };
        state.ServiceGrants.Add(fact.ServiceGrants.Select(static grant =>
        {
            var item = new ScheduledInvocationAuthorizationServiceGrantState
            {
                ServiceId = grant.ServiceId,
                NodeGrantsNotRequired = grant.NodeGrantsNotRequired,
            };
            item.NodeIds.Add(grant.NodeIds);
            return item;
        }));
        return state;
    }

    private static ScheduledServiceInvocationAuthState? CreateAuthState(ScheduledServiceInvocationAuth? auth)
    {
        if (auth?.Source == null)
            return null;

        return auth.Source switch
        {
            ScheduledServiceInvocationNyxIdCredentialSource nyxId => new ScheduledServiceInvocationAuthState
            {
                NyxId = CreateNyxIdCredentialSourceState(nyxId),
            },
            ScheduledServiceInvocationDurableCredentialReference durable => new ScheduledServiceInvocationAuthState
            {
                Durable = new ScheduledServiceInvocationDurableCredentialReferenceState
                {
                    CredentialId = durable.CredentialId,
                    SecretReference = durable.SecretReference.Clone(),
                },
            },
            ScheduledInvocationAgentKeyCredentialReference agentKey => new ScheduledServiceInvocationAuthState
            {
                ScheduledInvocationAgentKey = CreateScheduledInvocationAgentKeyState(agentKey),
            },
            _ => throw new ArgumentException("Unsupported scheduled service invocation credential source.", nameof(auth)),
        };
    }

    private static ScheduledServiceInvocationNyxIdCredentialSourceState CreateNyxIdCredentialSourceState(
        ScheduledServiceInvocationNyxIdCredentialSource source) =>
        new()
        {
            Subject = CreateSubjectState(source.Subject),
            Scope = source.Scope,
            Role = ToStateRole(source.Role),
        };

    private static ScheduledServiceInvocationNyxIdCredentialRoleState ToStateRole(
        ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role switch
        {
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner =>
                ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner,
            _ => ScheduledServiceInvocationNyxIdCredentialRoleState.Sender,
        };

    private static ScheduledInvocationAgentKeyCredentialReferenceState CreateScheduledInvocationAgentKeyState(
        ScheduledInvocationAgentKeyCredentialReference source) =>
        new()
        {
            SecretReference = source.SecretReference.Clone(),
            ApiKeyId = source.ApiKeyId,
            KeyExpiresAtUnixMs = source.KeyExpiresAtUnixMs,
        };

    private static ScheduledServiceInvocationNyxIdSubjectRefState? CreateSubjectState(
        ScheduledServiceInvocationNyxIdSubjectRef? subject) =>
        subject == null
            ? null
            : new ScheduledServiceInvocationNyxIdSubjectRefState
            {
                Platform = subject.Platform,
                Tenant = subject.Tenant,
                ExternalUserId = subject.ExternalUserId,
            };
}
