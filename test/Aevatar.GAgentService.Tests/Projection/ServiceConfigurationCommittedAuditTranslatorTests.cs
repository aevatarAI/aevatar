using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Audit;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceConfigurationCommittedAuditTranslatorTests
{
    [Fact]
    public void AddGAgentServiceGovernanceProjection_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceGovernanceProjection();
        using var provider = services.BuildServiceProvider();

        AssertCommittedAuditMaterializerRegistered<ServiceConfigurationProjectionContext>(services, provider);
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(ServiceBindingCreatedAuditTranslator),
                typeof(ServiceBindingUpdatedAuditTranslator),
                typeof(ServiceBindingRetiredAuditTranslator),
                typeof(ServiceEndpointCatalogCreatedAuditTranslator),
                typeof(ServiceEndpointCatalogUpdatedAuditTranslator),
                typeof(ServicePolicyCreatedAuditTranslator),
                typeof(ServicePolicyUpdatedAuditTranslator),
                typeof(ServicePolicyRetiredAuditTranslator),
                typeof(LegacyServiceConfigurationImportedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(GovernanceSeedEvents))]
    public void GovernanceTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId,
        AuditSensitivityLevel sensitivityLevel,
        bool isDestructive,
        IReadOnlyDictionary<string, string> expectedAnnotations)
    {
        var record = Translate(translator, evt);

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.ScopeId.Should().Be("tenant-a/app-a");
        record.SensitivityLevel.Should().Be(sensitivityLevel);
        record.CommittedFactRef.StateVersion.Should().Be(17);
        record.Annotations.Should().Contain("service_id", "svc-a");
        if (isDestructive)
            record.Annotations.Should().Contain("is_destructive", "true");
        else
            record.Annotations.Should().NotContainKey("is_destructive");
        foreach (var annotation in expectedAnnotations)
            record.Annotations.Should().Contain(annotation.Key, annotation.Value);
    }

    [Fact]
    public void GovernanceTranslator_ShouldNeverRecordSecretValues_ForSecretBinding()
    {
        var evt = new ServiceBindingCreatedEvent
        {
            Spec = new ServiceBindingSpec
            {
                Identity = Identity(),
                BindingId = "bind-secret",
                BindingKind = ServiceBindingKind.Secret,
                SecretRef = new BoundSecretRef { SecretName = "openai-api-key" },
            },
        };

        var record = Translate(new ServiceBindingCreatedAuditTranslator(), evt);

        // The safe reference name is recorded; no secret value or credential field exists in the proto.
        record.Annotations.Should().Contain("target_secret_name", "openai-api-key");
    }

    [Fact]
    public void GovernanceTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new ServicePolicyCreatedAuditTranslator();
        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    public static IEnumerable<object[]> GovernanceSeedEvents()
    {
        yield return
        [
            new ServiceBindingCreatedAuditTranslator(),
            new ServiceBindingCreatedEvent
            {
                Spec = new ServiceBindingSpec
                {
                    Identity = Identity(),
                    BindingId = "bind-1",
                    BindingKind = ServiceBindingKind.Service,
                    ServiceRef = new BoundServiceRef { Identity = Identity(), EndpointId = "ep-1" },
                },
            },
            "service.binding.created",
            "service_binding",
            "bind-1",
            AuditSensitivityLevel.Confidential,
            false,
            Annotations(("binding_id", "bind-1"), ("target_endpoint_id", "ep-1")),
        ];
        yield return
        [
            new ServiceBindingRetiredAuditTranslator(),
            new ServiceBindingRetiredEvent { Identity = Identity(), BindingId = "bind-1" },
            "service.binding.retired",
            "service_binding",
            "bind-1",
            AuditSensitivityLevel.Restricted,
            true,
            EmptyAnnotations,
        ];
        yield return
        [
            new ServiceEndpointCatalogCreatedAuditTranslator(),
            new ServiceEndpointCatalogCreatedEvent
            {
                Spec = new ServiceEndpointCatalogSpec
                {
                    Identity = Identity(),
                    Endpoints =
                    {
                        new ServiceEndpointExposureSpec { EndpointId = "ep-1" },
                    },
                },
            },
            "service.endpoint_catalog.created",
            "service_endpoint_catalog",
            "svc-a",
            AuditSensitivityLevel.Confidential,
            false,
            Annotations(("endpoint_count", "1")),
        ];
        yield return
        [
            new ServicePolicyCreatedAuditTranslator(),
            new ServicePolicyCreatedEvent
            {
                Spec = new ServicePolicySpec
                {
                    Identity = Identity(),
                    PolicyId = "policy-1",
                    DisplayName = "Restricted callers",
                },
            },
            "service.policy.created",
            "service_policy",
            "policy-1",
            AuditSensitivityLevel.Restricted,
            false,
            Annotations(("policy_id", "policy-1")),
        ];
        yield return
        [
            new ServicePolicyRetiredAuditTranslator(),
            new ServicePolicyRetiredEvent { Identity = Identity(), PolicyId = "policy-1" },
            "service.policy.retired",
            "service_policy",
            "policy-1",
            AuditSensitivityLevel.Restricted,
            true,
            EmptyAnnotations,
        ];
        yield return
        [
            new LegacyServiceConfigurationImportedAuditTranslator(),
            new LegacyServiceConfigurationImportedEvent
            {
                State = new ServiceConfigurationState { Identity = Identity() },
            },
            "service.configuration.imported",
            "service_configuration",
            "svc-a",
            AuditSensitivityLevel.Restricted,
            false,
            Annotations(("binding_count", "0"), ("policy_count", "0")),
        ];
    }

    private static IReadOnlyDictionary<string, string> EmptyAnnotations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Annotations(params (string Key, string Value)[] pairs)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            annotations[pair.Key] = pair.Value;

        return annotations;
    }

    private static ServiceIdentity Identity() =>
        new()
        {
            TenantId = "tenant-a",
            AppId = "app-a",
            Namespace = "default",
            ServiceId = "svc-a",
        };

    private static AuditRecord Translate(IAuditCommittedEventTranslator translator, IMessage evt)
    {
        var records = translator.Translate(Context(), Any.Pack(evt));
        return records.Should().ContainSingle().Subject;
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "corr-1",
                },
            },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "actor-1",
                EventId = "state-event-1",
                Version = 17,
            },
            "actor-1",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            "command-1",
            "request-1",
            "corr-1");

    private static void AssertCommittedAuditMaterializerRegistered<TContext>(
        IServiceCollection services,
        IServiceProvider provider)
        where TContext : class, IProjectionMaterializationContext
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<TContext>) &&
            IsObservedProjectionArtifactMaterializerFor<CommittedAuditArtifactMaterializer<TContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<TContext>>()
            .Should()
            .NotBeNull();
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TMaterializer);
    }
}
