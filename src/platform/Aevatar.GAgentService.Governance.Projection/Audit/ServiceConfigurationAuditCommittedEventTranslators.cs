using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Governance.Projection.Audit;

public sealed class ServiceBindingCreatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServiceBindingCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceBindingCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceBindingCreatedEvent evt) =>
        GovernanceSeed(
            "service.binding.created",
            evt.Spec?.Identity,
            "service_binding",
            evt.Spec?.BindingId ?? string.Empty,
            $"Service binding created: {evt.Spec?.BindingId ?? string.Empty}.",
            annotations: BindingAnnotations(evt.Spec));
}

public sealed class ServiceBindingUpdatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServiceBindingUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceBindingUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceBindingUpdatedEvent evt) =>
        GovernanceSeed(
            "service.binding.updated",
            evt.Spec?.Identity,
            "service_binding",
            evt.Spec?.BindingId ?? string.Empty,
            $"Service binding updated: {evt.Spec?.BindingId ?? string.Empty}.",
            annotations: BindingAnnotations(evt.Spec));
}

public sealed class ServiceBindingRetiredAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServiceBindingRetiredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceBindingRetiredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceBindingRetiredEvent evt) =>
        GovernanceSeed(
            "service.binding.retired",
            evt.Identity,
            "service_binding",
            evt.BindingId ?? string.Empty,
            $"Service binding retired: {evt.BindingId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class ServiceEndpointCatalogCreatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServiceEndpointCatalogCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceEndpointCatalogCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceEndpointCatalogCreatedEvent evt) =>
        GovernanceSeed(
            "service.endpoint_catalog.created",
            evt.Spec?.Identity,
            "service_endpoint_catalog",
            evt.Spec?.Identity?.ServiceId ?? string.Empty,
            "Service endpoint catalog created.",
            annotations: EndpointCatalogAnnotations(evt.Spec));
}

public sealed class ServiceEndpointCatalogUpdatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServiceEndpointCatalogUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceEndpointCatalogUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceEndpointCatalogUpdatedEvent evt) =>
        GovernanceSeed(
            "service.endpoint_catalog.updated",
            evt.Spec?.Identity,
            "service_endpoint_catalog",
            evt.Spec?.Identity?.ServiceId ?? string.Empty,
            "Service endpoint catalog updated.",
            annotations: EndpointCatalogAnnotations(evt.Spec));
}

public sealed class ServicePolicyCreatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServicePolicyCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServicePolicyCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServicePolicyCreatedEvent evt) =>
        GovernanceSeed(
            "service.policy.created",
            evt.Spec?.Identity,
            "service_policy",
            evt.Spec?.PolicyId ?? string.Empty,
            $"Service policy created: {evt.Spec?.PolicyId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            annotations: PolicyAnnotations(evt.Spec));
}

public sealed class ServicePolicyUpdatedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServicePolicyUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServicePolicyUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServicePolicyUpdatedEvent evt) =>
        GovernanceSeed(
            "service.policy.updated",
            evt.Spec?.Identity,
            "service_policy",
            evt.Spec?.PolicyId ?? string.Empty,
            $"Service policy updated: {evt.Spec?.PolicyId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            annotations: PolicyAnnotations(evt.Spec));
}

public sealed class ServicePolicyRetiredAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<ServicePolicyRetiredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServicePolicyRetiredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServicePolicyRetiredEvent evt) =>
        GovernanceSeed(
            "service.policy.retired",
            evt.Identity,
            "service_policy",
            evt.PolicyId ?? string.Empty,
            $"Service policy retired: {evt.PolicyId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class LegacyServiceConfigurationImportedAuditTranslator
    : ServiceConfigurationAuditTranslatorBase<LegacyServiceConfigurationImportedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(LegacyServiceConfigurationImportedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        LegacyServiceConfigurationImportedEvent evt) =>
        GovernanceSeed(
            "service.configuration.imported",
            evt.State?.Identity,
            "service_configuration",
            evt.State?.Identity?.ServiceId ?? string.Empty,
            "Legacy service configuration imported.",
            AuditSensitivityLevel.Restricted,
            annotations: ConfigurationAnnotations(evt.State));
}

public abstract class ServiceConfigurationAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
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

    // Governance configuration facts are service-scoped: the owning actor id embeds
    // no raw external subject, so the plain (non-subject-bearing) seed is used.
    protected static CommittedAuditSeed GovernanceSeed(
        string operationName,
        ServiceIdentity? identity,
        string targetKind,
        string targetId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        IReadOnlyDictionary<string, string>? annotations = null,
        bool isDestructive = false)
    {
        var merged = IdentityAnnotations(identity);
        if (annotations != null)
        {
            foreach (var pair in annotations)
                merged[pair.Key] = pair.Value ?? string.Empty;
        }

        return new CommittedAuditSeed(
            operationName,
            targetKind,
            targetId,
            BuildScopeId(identity),
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: merged);
    }

    // Records only safe binding references (id, kind, target ids/names) and never
    // any secret/credential material: the binding protos carry only reference names
    // (secret_name, connector_id, endpoint_id), no secret values.
    protected static IReadOnlyDictionary<string, string> BindingAnnotations(ServiceBindingSpec? spec)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec == null)
            return annotations;

        annotations["binding_id"] = spec.BindingId ?? string.Empty;
        annotations["binding_kind"] = spec.BindingKind.ToString();
        if (!string.IsNullOrWhiteSpace(spec.DisplayName))
            annotations["display_name"] = spec.DisplayName;

        switch (spec.TargetCase)
        {
            case ServiceBindingSpec.TargetOneofCase.ServiceRef:
                annotations["target_service_id"] = spec.ServiceRef?.Identity?.ServiceId ?? string.Empty;
                annotations["target_endpoint_id"] = spec.ServiceRef?.EndpointId ?? string.Empty;
                break;
            case ServiceBindingSpec.TargetOneofCase.ConnectorRef:
                annotations["target_connector_type"] = spec.ConnectorRef?.ConnectorType ?? string.Empty;
                annotations["target_connector_id"] = spec.ConnectorRef?.ConnectorId ?? string.Empty;
                break;
            case ServiceBindingSpec.TargetOneofCase.SecretRef:
                // secret_name is a reference name, not a secret value.
                annotations["target_secret_name"] = spec.SecretRef?.SecretName ?? string.Empty;
                break;
        }

        return annotations;
    }

    protected static IReadOnlyDictionary<string, string> EndpointCatalogAnnotations(ServiceEndpointCatalogSpec? spec)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec != null)
            annotations["endpoint_count"] = spec.Endpoints.Count.ToString();

        return annotations;
    }

    protected static IReadOnlyDictionary<string, string> PolicyAnnotations(ServicePolicySpec? spec)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec == null)
            return annotations;

        annotations["policy_id"] = spec.PolicyId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(spec.DisplayName))
            annotations["display_name"] = spec.DisplayName;
        annotations["invoke_requires_active_deployment"] = spec.InvokeRequiresActiveDeployment.ToString();

        return annotations;
    }

    protected static IReadOnlyDictionary<string, string> ConfigurationAnnotations(ServiceConfigurationState? state)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state == null)
            return annotations;

        annotations["binding_count"] = state.Bindings.Count.ToString();
        annotations["policy_count"] = state.Policies.Count.ToString();
        annotations["endpoint_count"] = (state.EndpointCatalog?.Endpoints.Count ?? 0).ToString();

        return annotations;
    }

    private static Dictionary<string, string> IdentityAnnotations(ServiceIdentity? identity) =>
        new(StringComparer.Ordinal)
        {
            ["tenant_id"] = identity?.TenantId ?? string.Empty,
            ["app_id"] = identity?.AppId ?? string.Empty,
            ["namespace"] = identity?.Namespace ?? string.Empty,
            ["service_id"] = identity?.ServiceId ?? string.Empty,
        };

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
}
