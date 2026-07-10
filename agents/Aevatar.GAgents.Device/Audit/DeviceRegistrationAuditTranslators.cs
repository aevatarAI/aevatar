using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Device.Audit;

// Committed-fact audit translators for device registrations. The registry actor
// is scope-scoped and each entry is keyed by an opaque generated registration
// id, so the target id / scope carry no external subject.
//
// Security boundary (docs/canon/audit-trail.md §4): the per-device HMAC signing
// key (DeviceRegistrationEntry.hmac_key / hmac_key_ref) is a credential and MUST
// NOT enter the audit artifact. Only the registration id, scope, and a safe
// label are recorded.

public sealed class DeviceRegisteredAuditTranslator
    : DeviceRegistrationAuditTranslatorBase<DeviceRegisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(DeviceRegisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        DeviceRegisteredEvent evt)
    {
        var entry = evt.Entry;
        return DeviceSeed(
            "device.registration.registered",
            entry?.Id ?? string.Empty,
            entry?.ScopeId ?? string.Empty,
            "Device registration committed.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["description"] = entry?.Description ?? string.Empty,
            });
    }
}

public sealed class DeviceUnregisteredAuditTranslator
    : DeviceRegistrationAuditTranslatorBase<DeviceUnregisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(DeviceUnregisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        DeviceUnregisteredEvent evt) =>
        DeviceSeed(
            "device.registration.unregistered",
            evt.RegistrationId,
            string.Empty,
            "Device registration removed.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public abstract class DeviceRegistrationAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
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

    protected static CommittedAuditSeed DeviceSeed(
        string operationName,
        string registrationId,
        string scopeId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? annotations = null) =>
        new(
            operationName,
            "device_registration",
            registrationId,
            scopeId,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: annotations);
}
