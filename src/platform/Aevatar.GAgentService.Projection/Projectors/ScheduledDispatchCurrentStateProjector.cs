using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ScheduledDispatchCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ScheduledDispatchProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ScheduledDispatchDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScheduledDispatchCurrentStateProjector(
        IProjectionWriteDispatcher<ScheduledDispatchDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ScheduledDispatchProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ScheduledDispatchState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = CreateDocument(context, envelope, stateEvent, state);

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private ScheduledDispatchDocument CreateDocument(
        ScheduledDispatchProjectionContext context,
        EventEnvelope envelope,
        StateEvent stateEvent,
        ScheduledDispatchState state)
    {
        var target = state.Target ?? new ScheduledDispatchTargetState();
        var serviceIdentity = target.ServiceInvocation?.Identity;
        var authorizationFact = state.ActiveTeamAuthorizationFact ?? target.ServiceInvocation?.AuthorizationFact;
        var ownerLLMSelection = authorizationFact?.OwnerLlmSelection;
        var scheduleId = string.IsNullOrWhiteSpace(state.ScheduleId) ? context.RootActorId : state.ScheduleId;
        var document = new ScheduledDispatchDocument
        {
            Id = scheduleId,
            ActorId = context.RootActorId,
            ScheduleActorId = context.RootActorId,
            ScheduleId = scheduleId,
            DisplayName = state.DisplayName ?? string.Empty,
            TargetKind = ToApplicationTargetKind(target.Kind).ToString(),
            ScheduleKind = ToApplicationScheduleKind(state.ScheduleKind).ToString(),
            CredentialRequirementTargetKind = ToApplicationCredentialRequirementTargetKind(
                target,
                state.ScheduleKind).ToString(),
            CredentialSourceKind = ToApplicationCredentialSourceKind(target.ServiceInvocation?.Auth).ToString(),
            ScheduleMode = ToApplicationScheduleMode(state.ScheduleMode).ToString(),
            PayloadTypeUrl = state.PayloadTypeUrl ?? string.Empty,
            CronExpression = state.CronExpression ?? string.Empty,
            Timezone = state.Timezone ?? string.Empty,
            Enabled = state.Enabled,
            LastTargetActorId = state.LastTargetActorId ?? string.Empty,
            LastCommandId = state.LastCommandId ?? string.Empty,
            LastCorrelationId = state.LastCorrelationId ?? string.Empty,
            LastError = state.LastError ?? string.Empty,
            LastErrorCode = state.LastErrorCode ?? string.Empty,
            FireCount = state.FireCount,
            FailureCount = state.FailureCount,
            OverdueFireDetectedCount = state.OverdueFireDetectedCount,
            ServiceKey = BuildServiceKey(serviceIdentity),
            ServiceId = serviceIdentity?.ServiceId ?? string.Empty,
            ServiceEndpointId = target.ServiceInvocation?.EndpointId ?? string.Empty,
            Prompt = ExtractPrompt(target.ServiceInvocation?.Payload, state.TriggerEnvelope),
            TargetActorId = state.TargetActorId ?? string.Empty,
            Deleted = state.Deleted,
            Completed = state.Completed,
            TeamOwned = state.TeamAutomationOwner != null,
            TeamId = state.TeamAutomationOwner?.TeamId ?? string.Empty,
            TeamAutomationOwner = state.TeamAutomationOwner == null
                ? null
                : new TeamMemberAutomationOwnerDocument
                {
                    ScopeId = state.TeamAutomationOwner.ScopeId ?? string.Empty,
                    MemberId = state.TeamAutomationOwner.MemberId ?? string.Empty,
                },
            TeamAutomationLifecycleStatus = ToProjectionLifecycleStatus(
                state.TeamAutomationLifecycleStatus),
            TeamAutomationOperationId = state.TeamAutomationOperationId ?? string.Empty,
            TeamAutomationIdempotencyKey = state.TeamAutomationIdempotencyKey ?? string.Empty,
            ActiveCredentialOwner = state.ActiveTeamCredentialOwner == null
                ? null
                : new ScheduledInvocationAuthorizationOwnerDocument
                {
                    Authority = state.ActiveTeamCredentialOwner.Authority ?? string.Empty,
                    OwnerKind = state.ActiveTeamCredentialOwner.OwnerKind ?? string.Empty,
                    OwnerSubject = state.ActiveTeamCredentialOwner.OwnerSubject ?? string.Empty,
                },
            CredentialGeneration = state.TeamCredentialGeneration,
            NyxidRevocationStatus = state.NyxidRevocationStatus.ToString(),
            VaultRevocationStatus = state.VaultRevocationStatus.ToString(),
            RevocationPending = state.PendingRevocationTeamCredential != null ||
                state.NyxidRevocationStatus == TeamAutomationEffectTrackStatusState.Pending ||
                state.VaultRevocationStatus == TeamAutomationEffectTrackStatusState.Pending,
            LastAuthorizationErrorCode = state.LastAuthorizationErrorCode ?? string.Empty,
            PermissionDigest = state.TeamAutomationPermissionDigest ?? string.Empty,
            PolicyVersion = state.TeamAutomationPolicyVersion ?? string.Empty,
            OwnerLlmRouteKind = ToOwnerLLMRouteKindName(ownerLLMSelection?.RouteKind ??
                LLMRouteKind.Unspecified),
            OwnerLlmRoute = ownerLLMSelection?.RouteValue ?? string.Empty,
            OwnerLlmUserServiceId = ownerLLMSelection?.NyxIdUserServiceId ?? string.Empty,
            OwnerLlmServiceSlug = ownerLLMSelection?.ServiceSlugSnapshot ?? string.Empty,
            OwnerLlmModel = ownerLLMSelection?.Model ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };
        document.CreatedAt = state.CreatedAt == default
            ? CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)
            : state.CreatedAt;
        document.UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        document.NextFireAt = state.NextFireAt;
        document.LastFireAt = state.LastFireAt;
        document.LastOverdueFireAt = state.LastOverdueFireAt;
        document.DeletedAt = state.DeletedAt;
        document.OneShotFireAt = state.OneShotFireAt;
        document.CompletedAt = state.CompletedAt;
        document.CredentialExpiresAt = state.TeamCredentialExpiresAt?.ToDateTimeOffset();
        document.Headers = state.Headers
            .Where(static item =>
                !ScheduledServiceInvocationPayloadPolicy.IsConnectorHttpAuthorizationKey(item.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        document.FireRecords.Add(CreateFireRecords(state));
        return document;
    }

    private static string ExtractPrompt(Any? serviceInvocationPayload, EventEnvelope? triggerEnvelope)
    {
        var prompt = ExtractPrompt(serviceInvocationPayload);
        return string.IsNullOrEmpty(prompt)
            ? ExtractPromptFromTriggerEnvelope(triggerEnvelope)
            : prompt;
    }

    private static string ExtractPromptFromTriggerEnvelope(EventEnvelope? triggerEnvelope)
    {
        if (triggerEnvelope?.Payload == null || !triggerEnvelope.Payload.Is(ServiceInvocationRequest.Descriptor))
            return string.Empty;

        return ExtractPrompt(triggerEnvelope.Payload.Unpack<ServiceInvocationRequest>().Payload);
    }

    private static string ExtractPrompt(Any? payload)
    {
        if (payload == null || !payload.Is(ChatRequestEvent.Descriptor))
            return string.Empty;

        return payload.Unpack<ChatRequestEvent>().Prompt ?? string.Empty;
    }

    private static string BuildServiceKey(ServiceIdentity? serviceIdentity)
    {
        if (serviceIdentity == null ||
            string.IsNullOrWhiteSpace(serviceIdentity.TenantId) ||
            string.IsNullOrWhiteSpace(serviceIdentity.AppId) ||
            string.IsNullOrWhiteSpace(serviceIdentity.Namespace) ||
            string.IsNullOrWhiteSpace(serviceIdentity.ServiceId))
        {
            return string.Empty;
        }

        return ServiceKeys.Build(serviceIdentity);
    }

    private static ScheduledDispatchFireRecordDocument[] CreateFireRecords(ScheduledDispatchState state) =>
        state.FireRecords.Values
            .OrderByDescending(static x => ResolveTimestampSeconds(x.CompletedAt))
            .ThenByDescending(static x => ResolveTimestampNanos(x.CompletedAt))
            .ThenByDescending(static x => x.IdempotencyKey ?? string.Empty, StringComparer.Ordinal)
            .Select(static x => new ScheduledDispatchFireRecordDocument
            {
                ScheduledFireAtUtcValue = x.ScheduledFireAt?.Clone(),
                CompletedAtUtcValue = x.CompletedAt?.Clone(),
                IdempotencyKey = x.IdempotencyKey ?? string.Empty,
                TargetActorId = x.TargetActorId ?? string.Empty,
                CommandId = x.CommandId ?? string.Empty,
                CorrelationId = x.CorrelationId ?? string.Empty,
                Error = x.Error ?? string.Empty,
                ErrorCode = x.ErrorCode ?? string.Empty,
                Manual = x.Manual,
            })
            .ToArray();

    private static ScheduledDispatchTargetKind ToApplicationTargetKind(ScheduledDispatchTargetKindState stateKind) =>
        stateKind switch
        {
            ScheduledDispatchTargetKindState.ServiceInvocation => ScheduledDispatchTargetKind.ServiceInvocation,
            _ => ScheduledDispatchTargetKind.Envelope,
        };

    private static string ToOwnerLLMRouteKindName(LLMRouteKind routeKind) => routeKind switch
    {
        LLMRouteKind.Unspecified => "unspecified",
        LLMRouteKind.Gateway => "gateway",
        LLMRouteKind.NyxIdUserService => "nyx_id_user_service",
        _ => throw new InvalidOperationException($"Unknown owner LLM route kind value '{(int)routeKind}'."),
    };

    private static ScheduledDispatchScheduleKind ToApplicationScheduleKind(ScheduledDispatchScheduleKindState stateKind) =>
        stateKind switch
        {
            ScheduledDispatchScheduleKindState.Workflow => ScheduledDispatchScheduleKind.Workflow,
            _ => ScheduledDispatchScheduleKind.Generic,
        };

    private static ScheduledDispatchCredentialRequirementTargetKind ToApplicationCredentialRequirementTargetKind(
        ScheduledDispatchTargetState target,
        ScheduledDispatchScheduleKindState scheduleKind)
    {
        var configuredKind = target.CredentialRequirementTargetKind switch
        {
            ScheduledDispatchCredentialRequirementTargetKindState.Envelope =>
                ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            ScheduledDispatchCredentialRequirementTargetKindState.StaticService =>
                ScheduledDispatchCredentialRequirementTargetKind.StaticService,
            ScheduledDispatchCredentialRequirementTargetKindState.ScriptingService =>
                ScheduledDispatchCredentialRequirementTargetKind.ScriptingService,
            ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService =>
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            ScheduledDispatchCredentialRequirementTargetKindState.Connector =>
                ScheduledDispatchCredentialRequirementTargetKind.Connector,
            _ => ScheduledDispatchCredentialRequirementTargetKind.Unspecified,
        };
        if (configuredKind != ScheduledDispatchCredentialRequirementTargetKind.Unspecified)
            return configuredKind;

        if (target.Kind == ScheduledDispatchTargetKindState.Envelope)
            return ScheduledDispatchCredentialRequirementTargetKind.Envelope;

        return target.Kind == ScheduledDispatchTargetKindState.ServiceInvocation &&
               scheduleKind == ScheduledDispatchScheduleKindState.Workflow
            ? ScheduledDispatchCredentialRequirementTargetKind.WorkflowService
            : ScheduledDispatchCredentialRequirementTargetKind.Unspecified;
    }

    private static ScheduledDispatchCredentialSourceKind ToApplicationCredentialSourceKind(
        ScheduledServiceInvocationAuthState? auth)
    {
        if (auth == null)
            return ScheduledDispatchCredentialSourceKind.None;
        if (auth.LegacyDurableSenderBearerBlocked ||
            !string.IsNullOrWhiteSpace(auth.DurableSenderBearerToken))
        {
            return ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer;
        }

        var sourceCount = 0;
        var sourceKind = ScheduledDispatchCredentialSourceKind.None;
        AddCredentialSourceKind(ResolveOneofCredentialSourceKind(auth), ref sourceCount, ref sourceKind);
        if (auth.SenderNyxId != null)
        {
            AddCredentialSourceKind(ScheduledDispatchCredentialSourceKind.SenderNyxId, ref sourceCount, ref sourceKind);
        }

        if (auth.ScopeOwnerNyxId != null)
        {
            AddCredentialSourceKind(ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId, ref sourceCount, ref sourceKind);
        }

        return sourceCount switch
        {
            0 => ScheduledDispatchCredentialSourceKind.None,
            1 => sourceKind,
            _ => ScheduledDispatchCredentialSourceKind.Multiple,
        };
    }

    private static ScheduledDispatchCredentialSourceKind ResolveOneofCredentialSourceKind(
        ScheduledServiceInvocationAuthState auth) =>
        auth.SourceCase switch
        {
            ScheduledServiceInvocationAuthState.SourceOneofCase.NyxId =>
                auth.NyxId?.Role == ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner
                    ? ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId
                    : ScheduledDispatchCredentialSourceKind.SenderNyxId,
            ScheduledServiceInvocationAuthState.SourceOneofCase.Durable =>
                ScheduledDispatchCredentialSourceKind.DurableCredentialReference,
            ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey =>
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
            _ => ScheduledDispatchCredentialSourceKind.None,
        };

    private static void AddCredentialSourceKind(
        ScheduledDispatchCredentialSourceKind candidate,
        ref int sourceCount,
        ref ScheduledDispatchCredentialSourceKind sourceKind)
    {
        if (candidate == ScheduledDispatchCredentialSourceKind.None)
            return;

        sourceCount++;
        sourceKind = candidate;
    }

    private static ScheduledDispatchScheduleMode ToApplicationScheduleMode(ScheduledDispatchScheduleModeState stateMode) =>
        stateMode == ScheduledDispatchScheduleModeState.OneShotAtUtc
            ? ScheduledDispatchScheduleMode.OneShotAtUtc
            : ScheduledDispatchScheduleMode.RecurringCron;

    private static TeamAutomationLifecycleStatusDocument ToProjectionLifecycleStatus(
        TeamAutomationLifecycleStatusState status) =>
        status switch
        {
            TeamAutomationLifecycleStatusState.ProvisioningPending =>
                TeamAutomationLifecycleStatusDocument.ProvisioningPending,
            TeamAutomationLifecycleStatusState.Active => TeamAutomationLifecycleStatusDocument.Active,
            TeamAutomationLifecycleStatusState.NeedsAuthorization =>
                TeamAutomationLifecycleStatusDocument.NeedsAuthorization,
            TeamAutomationLifecycleStatusState.ReplacementPending =>
                TeamAutomationLifecycleStatusDocument.ReplacementPending,
            TeamAutomationLifecycleStatusState.Deleting => TeamAutomationLifecycleStatusDocument.Deleting,
            TeamAutomationLifecycleStatusState.RevocationPending =>
                TeamAutomationLifecycleStatusDocument.RevocationPending,
            TeamAutomationLifecycleStatusState.Failed => TeamAutomationLifecycleStatusDocument.Failed,
            _ => TeamAutomationLifecycleStatusDocument.Unspecified,
        };

    private static long ResolveTimestampSeconds(Timestamp? timestamp) =>
        timestamp?.Seconds ?? 0;

    private static int ResolveTimestampNanos(Timestamp? timestamp) =>
        timestamp?.Nanos ?? 0;
}
