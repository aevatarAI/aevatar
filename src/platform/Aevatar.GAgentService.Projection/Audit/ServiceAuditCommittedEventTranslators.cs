using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Audit;

public sealed class ServiceRegistrationSucceededAuditTranslator
    : AuditTranslatorBase<ServiceRegistrationSucceededEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRegistrationSucceededEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRegistrationSucceededEvent evt) =>
        ServiceSeed(
            "service.registration.succeeded",
            evt.Identity,
            evt.Identity?.ServiceId ?? string.Empty,
            evt.CredentialKid,
            $"Service registration committed for {evt.Identity?.ServiceId ?? string.Empty}.");
}

public sealed class ServiceRegistrationFailedAuditTranslator
    : AuditTranslatorBase<ServiceRegistrationFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRegistrationFailedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRegistrationFailedEvent evt) =>
        ServiceSeed(
            "service.registration.failed",
            evt.Identity,
            evt.Identity?.ServiceId ?? string.Empty,
            evt.CredentialKid,
            $"Service registration failed for {evt.Identity?.ServiceId ?? string.Empty}.",
            sensitivityLevel: AuditSensitivityLevel.Confidential,
            terminalOutcome: AuditTerminalOutcome.Failed,
            failure: Failure(
                "service_registration_failed",
                AuditFailureCategory.Dependency,
                AuditLifecyclePhase.Running));
}

public sealed class ServiceRegistrationRetiredAuditTranslator
    : AuditTranslatorBase<ServiceRegistrationRetiredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRegistrationRetiredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRegistrationRetiredEvent evt) =>
        ServiceSeed(
            "service.registration.retired",
            evt.Identity,
            evt.Identity?.ServiceId ?? string.Empty,
            "",
            $"Service registration retired for {evt.Identity?.ServiceId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ServiceRevisionPublishedAuditTranslator
    : AuditTranslatorBase<ServiceRevisionPublishedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRevisionPublishedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRevisionPublishedEvent evt) =>
        ServiceSeed(
            "service.revision.published",
            evt.Identity,
            evt.RevisionId,
            "",
            $"Service revision published: {evt.RevisionId}.");
}

public sealed class DefaultServingRevisionChangedAuditTranslator
    : AuditTranslatorBase<DefaultServingRevisionChangedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(DefaultServingRevisionChangedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        DefaultServingRevisionChangedEvent evt) =>
        ServiceSeed(
            "service.default-serving.changed",
            evt.Identity,
            evt.RevisionId,
            "",
            $"Default serving revision changed to {evt.RevisionId}.");
}

public sealed class ServiceDeploymentActivatedAuditTranslator
    : AuditTranslatorBase<ServiceDeploymentActivatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceDeploymentActivatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceDeploymentActivatedEvent evt) =>
        ServiceSeed(
            "service.deployment.activated",
            evt.Identity,
            evt.DeploymentId,
            "",
            $"Service deployment activated: {evt.DeploymentId}.");
}

public sealed class ServiceDeploymentDeactivatedAuditTranslator
    : AuditTranslatorBase<ServiceDeploymentDeactivatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceDeploymentDeactivatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceDeploymentDeactivatedEvent evt) =>
        ServiceSeed(
            "service.deployment.deactivated",
            evt.Identity,
            evt.DeploymentId,
            "",
            $"Service deployment deactivated: {evt.DeploymentId}.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ScheduledDispatchConfiguredAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchConfiguredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchConfiguredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchConfiguredEvent evt) =>
        ScheduleSeed(
            "scheduled.dispatch.configured",
            evt.ScheduleId,
            evt.Headers,
            evt.Enabled ? "Scheduled dispatch configured and enabled." : "Scheduled dispatch configured.");
}

public sealed class ScheduledDispatchEnabledAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchEnabledEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchEnabledEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchEnabledEvent evt) =>
        ScheduleSeed("scheduled.dispatch.enabled", context.OriginActorId, null, "Scheduled dispatch enabled.");
}

public sealed class ScheduledDispatchDisabledAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchDisabledEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchDisabledEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchDisabledEvent evt) =>
        ScheduleSeed(
            "scheduled.dispatch.disabled",
            context.OriginActorId,
            null,
            "Scheduled dispatch disabled.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ScheduledDispatchDeletedAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchDeletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchDeletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchDeletedEvent evt) =>
        ScheduleSeed(
            "scheduled.dispatch.deleted",
            context.OriginActorId,
            null,
            "Scheduled dispatch deleted.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ServiceDefinitionCreatedAuditTranslator
    : AuditTranslatorBase<ServiceDefinitionCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceDefinitionCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceDefinitionCreatedEvent evt) =>
        ServiceSeed(
            "service.definition.created",
            evt.Spec?.Identity,
            evt.Spec?.Identity?.ServiceId ?? string.Empty,
            "",
            $"Service definition created for {evt.Spec?.Identity?.ServiceId ?? string.Empty}.");
}

public sealed class ServiceDefinitionUpdatedAuditTranslator
    : AuditTranslatorBase<ServiceDefinitionUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceDefinitionUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceDefinitionUpdatedEvent evt) =>
        ServiceSeed(
            "service.definition.updated",
            evt.Spec?.Identity,
            evt.Spec?.Identity?.ServiceId ?? string.Empty,
            "",
            $"Service definition updated for {evt.Spec?.Identity?.ServiceId ?? string.Empty}.");
}

public sealed class ServiceRevisionCreatedAuditTranslator
    : AuditTranslatorBase<ServiceRevisionCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRevisionCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRevisionCreatedEvent evt) =>
        ServiceSeed(
            "service.revision.created",
            evt.Spec?.Identity,
            evt.Spec?.RevisionId ?? string.Empty,
            "",
            $"Service revision created: {evt.Spec?.RevisionId ?? string.Empty}.");
}

public sealed class ServiceRevisionRetiredAuditTranslator
    : AuditTranslatorBase<ServiceRevisionRetiredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRevisionRetiredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRevisionRetiredEvent evt) =>
        ServiceSeed(
            "service.revision.retired",
            evt.Identity,
            evt.RevisionId,
            "",
            $"Service revision retired: {evt.RevisionId}.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ServiceRunRegisteredAuditTranslator
    : AuditTranslatorBase<ServiceRunRegisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRunRegisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRunRegisteredEvent evt)
    {
        var record = evt.Record;
        var runId = record?.RunId ?? string.Empty;
        var serviceId = record?.ServiceId ?? string.Empty;
        var status = RunAuditAnnotations.StatusLabel(record?.Status ?? ServiceRunStatus.Unspecified);
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service_id"] = serviceId,
            ["scope_id"] = record?.ScopeId ?? string.Empty,
            ["status"] = status,
            ["target_actor_id"] = record?.TargetActorId ?? string.Empty,
            ["implementation_kind"] = ((int?)record?.ImplementationKind ?? 0).ToString(),
        };
        if (!string.IsNullOrWhiteSpace(record?.ScheduleId))
            annotations["schedule_id"] = record.ScheduleId;

        return new CommittedAuditSeed(
            "service.run.registered",
            "service_run",
            runId,
            record?.ScopeId ?? string.Empty,
            AuditSensitivityLevel.Confidential,
            CommandId: record?.CommandId ?? string.Empty,
            CorrelationId: record?.CorrelationId ?? string.Empty,
            ResultSummary: $"Service run {runId} registered for service {serviceId} (status {status}).",
            Annotations: annotations,
            LifecyclePhase: LifecyclePhase(record?.Status ?? ServiceRunStatus.Unspecified),
            TerminalOutcome: TerminalOutcome(record?.Status ?? ServiceRunStatus.Unspecified),
            Failure: BuildRunFailure(record?.Status ?? ServiceRunStatus.Unspecified),
            PublishedServiceId: serviceId,
            RunId: runId,
            OmittedFields: ["service_run.last_output", "service_run.last_error"]);
    }
}

public sealed class ServiceRunStatusUpdatedAuditTranslator
    : AuditTranslatorBase<ServiceRunStatusUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRunStatusUpdatedEvent.Descriptor);

    // The event carries only run id, status and the free-text last_output /
    // last_error terminal bodies. Only the run id and status are recorded.
    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRunStatusUpdatedEvent evt)
    {
        var status = RunAuditAnnotations.StatusLabel(evt.Status);
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = status,
        };

        return new CommittedAuditSeed(
            "service.run.status-updated",
            "service_run",
            evt.RunId ?? string.Empty,
            SensitivityLevel: AuditSensitivityLevel.Confidential,
            ResultSummary: $"Service run {evt.RunId} status updated to {status}.",
            Annotations: annotations,
            LifecyclePhase: LifecyclePhase(evt.Status),
            TerminalOutcome: TerminalOutcome(evt.Status),
            Failure: BuildRunFailure(evt.Status),
            RunId: evt.RunId ?? string.Empty,
            OmittedFields: ["service_run.last_output", "service_run.last_error"]);
    }
}

public sealed class ScheduledDispatchFireDispatchedAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchFireDispatchedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchFireDispatchedEvent.Descriptor);

    // The fire event does not carry the schedule id; the owning actor id is the
    // schedule id (ScheduledDispatchGAgent is keyed by schedule id, not a raw
    // external subject) so the plain, non subject-bearing shape applies.
    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchFireDispatchedEvent evt) =>
        new(
            "scheduled.dispatch.fire.dispatched",
            "scheduled_dispatch",
            context.OriginActorId,
            SensitivityLevel: AuditSensitivityLevel.Confidential,
            CommandId: evt.CommandId ?? string.Empty,
            CorrelationId: evt.CorrelationId ?? string.Empty,
            ResultSummary: evt.Manual
                ? $"Scheduled dispatch {context.OriginActorId} fired (manual) to {evt.TargetActorId}."
                : $"Scheduled dispatch {context.OriginActorId} fired to {evt.TargetActorId}.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target_actor_id"] = evt.TargetActorId ?? string.Empty,
                ["idempotency_key"] = evt.IdempotencyKey ?? string.Empty,
                ["manual"] = evt.Manual ? "true" : "false",
            },
            LifecyclePhase: AuditLifecyclePhase.Accepted,
            TerminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ScheduledDispatchFireFailedAuditTranslator
    : AuditTranslatorBase<ScheduledDispatchFireFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScheduledDispatchFireFailedEvent.Descriptor);

    // The free-text error body is omitted because it may embed dispatch payload
    // detail. Fire failure is terminal but is not a delete/retire operation.
    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScheduledDispatchFireFailedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["idempotency_key"] = evt.IdempotencyKey ?? string.Empty,
            ["manual"] = evt.Manual ? "true" : "false",
        };

        return new CommittedAuditSeed(
            "scheduled.dispatch.fire.failed",
            "scheduled_dispatch",
            context.OriginActorId,
            SensitivityLevel: AuditSensitivityLevel.Confidential,
            ResultSummary: $"Scheduled dispatch {context.OriginActorId} fire failed.",
            Annotations: annotations,
            TerminalOutcome: AuditTerminalOutcome.Failed,
            Failure: Failure(
                "scheduled_dispatch_failed",
                AuditFailureCategory.Execution,
                AuditLifecyclePhase.Running));
    }
}

internal static class RunAuditAnnotations
{
    public static string StatusLabel(ServiceRunStatus status) =>
        status switch
        {
            ServiceRunStatus.Accepted => "accepted",
            ServiceRunStatus.Completed => "completed",
            ServiceRunStatus.Failed => "failed",
            ServiceRunStatus.Stopped => "stopped",
            ServiceRunStatus.OutcomeUncertain => "outcome_uncertain",
            _ => "unspecified",
        };

}

public abstract class AuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed ServiceSeed(
        string operationName,
        ServiceIdentity? identity,
        string targetId,
        string credentialKid,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        IReadOnlyDictionary<string, string>? annotations = null,
        bool isDestructive = false,
        AuditLifecyclePhase lifecyclePhase = AuditLifecyclePhase.Terminal,
        AuditTerminalOutcome terminalOutcome = AuditTerminalOutcome.Succeeded,
        AuditFailure? failure = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant_id"] = identity?.TenantId ?? string.Empty,
            ["app_id"] = identity?.AppId ?? string.Empty,
            ["namespace"] = identity?.Namespace ?? string.Empty,
            ["service_id"] = identity?.ServiceId ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(credentialKid))
            merged["credential_kid"] = credentialKid;
        if (annotations != null)
        {
            foreach (var pair in annotations)
                merged[pair.Key] = pair.Value ?? string.Empty;
        }

        return new CommittedAuditSeed(
            operationName,
            "service",
            targetId,
            BuildScopeId(identity),
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: merged,
            LifecyclePhase: lifecyclePhase,
            TerminalOutcome: terminalOutcome,
            Failure: failure,
            PublishedServiceId: identity?.ServiceId ?? string.Empty);
    }

    protected static CommittedAuditSeed ScheduleSeed(
        string operationName,
        string scheduleId,
        IDictionary<string, string>? headers,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false)
    {
        var commandId = ReadHeader(headers, "command_id", "commandId");
        var requestId = ReadHeader(headers, "request_id", "requestId");
        return new CommittedAuditSeed(
            operationName,
            "scheduled_dispatch",
            scheduleId,
            ReadHeader(headers, "scope_id", "scopeId"),
            sensitivityLevel,
            isDestructive,
            commandId,
            requestId,
            ResultSummary: resultSummary);
    }

    private static string BuildScopeId(ServiceIdentity? identity)
    {
        if (identity == null)
            return string.Empty;

        var tenant = identity.TenantId ?? string.Empty;
        var app = identity.AppId ?? string.Empty;
        return string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(app)
            ? string.Empty
            : $"{tenant}/{app}";
    }

    private static string ReadHeader(IDictionary<string, string>? headers, string snakeKey, string camelKey)
    {
        if (headers == null)
            return string.Empty;
        if (headers.TryGetValue(snakeKey, out var snakeValue) && !string.IsNullOrWhiteSpace(snakeValue))
            return snakeValue;
        if (headers.TryGetValue(camelKey, out var camelValue) && !string.IsNullOrWhiteSpace(camelValue))
            return camelValue;

        return string.Empty;
    }

    protected static AuditLifecyclePhase LifecyclePhase(ServiceRunStatus status) => status switch
    {
        ServiceRunStatus.Accepted => AuditLifecyclePhase.Accepted,
        ServiceRunStatus.Completed or
            ServiceRunStatus.Failed or
            ServiceRunStatus.Stopped or
            ServiceRunStatus.OutcomeUncertain =>
            AuditLifecyclePhase.Terminal,
        _ => AuditLifecyclePhase.Running,
    };

    protected static AuditTerminalOutcome TerminalOutcome(ServiceRunStatus status) => status switch
    {
        ServiceRunStatus.Completed => AuditTerminalOutcome.Succeeded,
        ServiceRunStatus.Failed => AuditTerminalOutcome.Failed,
        ServiceRunStatus.Stopped => AuditTerminalOutcome.Cancelled,
        ServiceRunStatus.OutcomeUncertain => AuditTerminalOutcome.Unspecified,
        _ => AuditTerminalOutcome.Unspecified,
    };

    protected static AuditFailure? BuildRunFailure(ServiceRunStatus status) =>
        status is ServiceRunStatus.Failed or ServiceRunStatus.OutcomeUncertain
            ? Failure(
                status == ServiceRunStatus.Failed
                    ? "service_run_failed"
                    : "service_run_outcome_uncertain",
                AuditFailureCategory.Execution,
                AuditLifecyclePhase.Running)
            : null;

    protected static AuditFailure Failure(
        string code,
        AuditFailureCategory category,
        AuditLifecyclePhase failedPhase) =>
        new()
        {
            Code = code,
            Category = category,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = failedPhase,
            SanitizedMessage = code,
        };
}
