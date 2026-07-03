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
            AuditSensitivityLevel.Confidential,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["error_summary"] = evt.LastError ?? string.Empty,
            });
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
        bool isDestructive = false)
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
            Annotations: merged);
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
}
