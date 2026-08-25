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
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var command = CreateUpdateCommand(configuration, dispatch, expectedTarget);
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
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchEnableCommand
        {
            Reason = reason ?? string.Empty,
            ExpectedServiceTarget = CreateExpectedServiceTargetState(expectedTarget),
        }, ct);
    }

    public async Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchDisableCommand
        {
            Reason = reason ?? string.Empty,
            ExpectedServiceTarget = CreateExpectedServiceTargetState(expectedTarget),
        }, ct);
    }

    public async Task<DispatchAdmission> DispatchDeleteAsync(
        string actorId,
        string reason,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchDeleteCommand
        {
            Reason = reason ?? string.Empty,
            ExpectedServiceTarget = CreateExpectedServiceTargetState(expectedTarget),
        }, ct);
    }

    public async Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return await DispatchAsync(actorId, new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt.ToUniversalTime()),
            Manual = true,
            IdempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey(actorId, scheduledFireAt),
            ExpectedServiceTarget = CreateExpectedServiceTargetState(expectedTarget),
        }, ct);
    }

    public Task<DispatchAdmission> DispatchBeginTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamAutomationCredentialOperation operation,
        string observationRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(operation);
        return DispatchAsync(actorId, new BeginTeamAutomationCredentialOperationCommand
        {
            ScheduleId = operation.ScheduleId,
            Owner = CreateTeamOwnerState(operation.Owner),
            OperationId = operation.OperationId,
            IdempotencyKey = operation.IdempotencyKey,
            PermissionDigest = operation.PermissionDigest,
            PolicyVersion = operation.PolicyVersion,
            OperationKind = ToStateOperationKind(operation.Kind),
            CredentialEffectLocator = new ScheduledCredentialEffectLocatorState
            {
                CredentialName = operation.CredentialEffectLocator.CredentialName,
                RequestedSecretReference = operation.CredentialEffectLocator.RequestedSecretReference,
                SecretPurpose = operation.CredentialEffectLocator.SecretPurpose,
                SecretOwnerScopeKey = operation.CredentialEffectLocator.SecretOwnerScopeKey,
                CredentialOwner = CreateAuthorizationOwnerState(
                    operation.CredentialEffectLocator.CredentialOwner),
            },
            ActivationDecision = CreateActivationDecisionState(operation.ActivationDecision),
            MutationDigest = operation.MutationDigest,
            ObservationRequestId = observationRequestId,
        }, ct);
    }

    public Task<DispatchAdmission> DispatchRetryTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string observationRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return DispatchAsync(actorId, new RetryTeamAutomationCredentialOperationCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            ObservationRequestId = observationRequestId,
        }, ct);
    }

    private static TeamAutomationActivationDecisionState CreateActivationDecisionState(
        TeamAutomationActivationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var state = new TeamAutomationActivationDecisionState
        {
            ScheduleId = decision.ScheduleId,
            DisplayName = decision.DisplayName,
            Owner = CreateTeamOwnerState(decision.Owner),
            ServiceIdentity = decision.ServiceIdentity.Clone(),
            EndpointId = decision.EndpointId,
            Payload = decision.Payload.Clone(),
            CallerAuthority = decision.CallerAuthority.Clone(),
            AuthorizationFact = CreateAuthorizationFactState(decision.AuthorizationFact),
            CronExpression = decision.CronExpression,
            Timezone = decision.Timezone,
            Enabled = decision.Enabled,
            ScheduleKind = ToStateScheduleKind(decision.ScheduleKind),
            ScheduleMode = ToStateScheduleMode(decision.ScheduleMode),
            OneShotFireAt = decision.OneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(decision.OneShotFireAt.Value.ToUniversalTime())
                : null,
            CredentialRequirementTargetKind =
                ToStateCredentialRequirementTargetKind(decision.CredentialRequirementTargetKind),
            RevisionId = decision.RevisionId,
            Caller = decision.Caller?.Clone(),
        };
        foreach (var (key, value) in decision.Headers)
            state.Headers[key] = value;
        return state;
    }

    public Task<DispatchAdmission> DispatchRecordTeamAutomationCredentialCandidateAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner credentialOwner,
        string observationRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(credentialOwner);
        return DispatchAsync(actorId, new RecordTeamAutomationCredentialCandidateCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            EffectAttemptId = effectAttemptId,
            Credential = CreateScheduledInvocationAgentKeyState(credential),
            CredentialOwner = CreateAuthorizationOwnerState(credentialOwner),
            ObservationRequestId = observationRequestId,
        }, ct);
    }

    public Task<DispatchAdmission> DispatchCompleteTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        string observationRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(credential);
        return DispatchAsync(actorId, new CompleteTeamAutomationCredentialOperationCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            EffectAttemptId = effectAttemptId,
            Credential = CreateScheduledInvocationAgentKeyState(credential),
            Configuration = CreateConfiguredEvent(configuration, dispatch),
            ObservationRequestId = observationRequestId,
        }, ct);
    }

    public Task<DispatchAdmission> DispatchFailTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        string observationRequestId,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new FailTeamAutomationCredentialOperationCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            EffectAttemptId = effectAttemptId,
            ErrorCode = errorCode,
            ObservationRequestId = observationRequestId,
        }, ct);

    public Task<DispatchAdmission> DispatchEnableTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new ScheduledDispatchEnableCommand
        {
            Reason = reason ?? string.Empty,
            TeamAutomationOwner = CreateTeamOwnerState(owner),
        }, ct);

    public Task<DispatchAdmission> DispatchDisableTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new ScheduledDispatchDisableCommand
        {
            Reason = reason ?? string.Empty,
            TeamAutomationOwner = CreateTeamOwnerState(owner),
        }, ct);

    public Task<DispatchAdmission> DispatchDeleteTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new ScheduledDispatchDeleteCommand
        {
            Reason = reason ?? string.Empty,
            TeamAutomationOwner = CreateTeamOwnerState(owner),
        }, ct);

    public Task<DispatchAdmission> DispatchDeleteTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string reason,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        string observationRequestId,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new ScheduledDispatchDeleteCommand
        {
            Reason = reason ?? string.Empty,
            TeamAutomationOwner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            AuthenticatedCredentialOwner = CreateAuthorizationOwnerState(authenticatedCredentialOwner),
            ObservationRequestId = observationRequestId,
        }, ct);

    public Task<DispatchAdmission> DispatchRetryTeamAutomationRevocationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        string observationRequestId,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new RetryTeamAutomationRevocationCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            AuthenticatedCredentialOwner = CreateAuthorizationOwnerState(authenticatedCredentialOwner),
            ObservationRequestId = observationRequestId,
        }, ct);

    public Task<DispatchAdmission> DispatchCompleteTeamAutomationRevocationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        bool nyxIdRevoked,
        bool vaultRevoked,
        string errorCode,
        string observationRequestId,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new CompleteTeamAutomationRevocationCommand
        {
            Owner = CreateTeamOwnerState(owner),
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            EffectAttemptId = effectAttemptId,
            NyxidRevoked = nyxIdRevoked,
            VaultRevoked = vaultRevoked,
            ErrorCode = errorCode?.Trim() ?? string.Empty,
            ObservationRequestId = observationRequestId,
        }, ct);

    public Task<DispatchAdmission> DispatchRunTeamAutomationNowAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        DateTimeOffset scheduledFireAt,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        DispatchAsync(actorId, new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt.ToUniversalTime()),
            Manual = true,
            TeamAutomationOwner = CreateTeamOwnerState(owner),
            OperationId = operationId?.Trim() ?? string.Empty,
            IdempotencyKey = idempotencyKey?.Trim() ?? string.Empty,
        }, ct);

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
        PreparedScheduledDispatchTarget dispatch,
        ScheduledDispatchExpectedServiceTarget? expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);

        var command = new ScheduledDispatchUpdateCommand();
        PopulateConfigureCommand(command, configuration, dispatch);
        command.ExpectedServiceTarget = CreateExpectedServiceTargetState(expectedTarget);
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

    private static ScheduledDispatchConfiguredEvent CreateConfiguredEvent(
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dispatch);
        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            TargetActorId = dispatch.TargetActorId ?? string.Empty,
            TriggerEnvelope = dispatch.TriggerEnvelope.Clone(),
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            PayloadTypeUrl = dispatch.PayloadTypeUrl,
            Target = CreateTargetState(dispatch.Descriptor, configuration.CredentialRequirementTargetKind),
            ScheduleKind = ToStateScheduleKind(configuration.ScheduleKind),
            ScheduleMode = ToStateScheduleMode(configuration.ScheduleMode),
            OneShotFireAt = configuration.OneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(configuration.OneShotFireAt.Value.ToUniversalTime())
                : null,
            TeamAutomationOwner = CreateTeamOwnerState(configuration.TeamAutomationOwner),
        };
        foreach (var (key, value) in configuration.Headers)
            configured.Headers[key] = value;
        return configured;
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
        command.TeamAutomationOwner = CreateTeamOwnerState(configuration.TeamAutomationOwner);
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
        command.TeamAutomationOwner = CreateTeamOwnerState(configuration.TeamAutomationOwner);
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
        command.TeamAutomationOwner = CreateTeamOwnerState(configuration.TeamAutomationOwner);
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

    private static ScheduledDispatchExpectedServiceTargetState? CreateExpectedServiceTargetState(
        ScheduledDispatchExpectedServiceTarget? target)
    {
        if (target == null)
            return null;

        return new ScheduledDispatchExpectedServiceTargetState
        {
            ScheduleKind = ToStateScheduleKind(target.ScheduleKind),
            TargetKind = target.TargetKind == ScheduledDispatchTargetKind.ServiceInvocation
                ? ScheduledDispatchTargetKindState.ServiceInvocation
                : ScheduledDispatchTargetKindState.Envelope,
            ServiceIdentity = target.ServiceIdentity.Clone(),
            ServiceEndpointId = target.ServiceEndpointId,
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
                CatalogContentDigest = fact.Authority.CatalogContentDigest,
                CatalogContractVersion = fact.Authority.CatalogContractVersion,
                CatalogPolicyVersion = fact.Authority.CatalogPolicyVersion,
                CatalogEvaluatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(fact.Authority.CatalogEvaluatedAt),
            },
            OwnerLlmSelection = fact.OwnerLLMSelection?.Clone(),
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

    private static ScheduledInvocationAuthorizationOwnerState CreateAuthorizationOwnerState(
        ScheduledInvocationAuthorizationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ScheduledInvocationAuthorizationOwnerState
        {
            Authority = owner.Authority,
            OwnerKind = owner.OwnerKind,
            OwnerSubject = owner.OwnerSubject,
        };
    }

    private static ScheduledServiceInvocationAuthState? CreateAuthState(ScheduledServiceInvocationAuth? auth)
    {
        if (auth?.Source == null)
            return null;

        var state = auth.Source switch
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
        state.CallerAuthority = auth.CallerAuthority?.Clone();
        return state;
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

    private static TeamMemberAutomationOwnerState? CreateTeamOwnerState(TeamMemberAutomationOwner? owner) =>
        owner == null
            ? null
            : new TeamMemberAutomationOwnerState
            {
                ScopeId = owner.ScopeId,
                MemberId = owner.MemberId,
                TeamId = owner.TeamId,
            };

    private static TeamAutomationOperationKindState ToStateOperationKind(TeamAutomationOperationKind kind) =>
        kind switch
        {
            TeamAutomationOperationKind.Create => TeamAutomationOperationKindState.Create,
            TeamAutomationOperationKind.Reauthorize => TeamAutomationOperationKindState.Reauthorize,
            TeamAutomationOperationKind.Delete => TeamAutomationOperationKindState.Delete,
            _ => TeamAutomationOperationKindState.Unspecified,
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
