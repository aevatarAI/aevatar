using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Audit;
using Aevatar.GAgentService.Projection.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCommittedAuditTranslatorTests
{
    [Fact]
    public void AddGAgentServiceProjection_ShouldWireCommittedAuditMaterializersAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();
        using var provider = services.BuildServiceProvider();

        AssertCommittedAuditMaterializerRegistered<ServiceCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceDeploymentCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceRevisionCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ScheduledDispatchProjectionContext>(services, provider);
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(ServiceRegistrationSucceededAuditTranslator),
                typeof(ServiceRegistrationFailedAuditTranslator),
                typeof(ServiceRegistrationRetiredAuditTranslator),
                typeof(ServiceRevisionPublishedAuditTranslator),
                typeof(ServiceDeploymentActivatedAuditTranslator),
                typeof(ServiceDeploymentDeactivatedAuditTranslator),
                typeof(ScheduledDispatchConfiguredAuditTranslator),
                typeof(ScheduledDispatchEnabledAuditTranslator),
                typeof(ScheduledDispatchDisabledAuditTranslator),
                typeof(ScheduledDispatchDeletedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(ServiceSeedEvents))]
    public void ServiceSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId)
    {
        var record = Translate(translator, evt);

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.CommittedFactRef.StateVersion.Should().Be(17);
    }

    [Fact]
    public void ServiceTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new ServiceRevisionPublishedAuditTranslator();
        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    public static IEnumerable<object[]> ServiceSeedEvents()
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant-a",
            AppId = "app-a",
            Namespace = "default",
            ServiceId = "svc-a",
        };

        yield return
        [
            new ServiceRegistrationSucceededAuditTranslator(),
            new ServiceRegistrationSucceededEvent
            {
                Identity = identity,
                NyxidServiceId = "nyx-svc",
                NyxidSlug = "slug",
                CredentialKid = "kid-1",
            },
            "service.registration.succeeded",
            "service",
            "svc-a",
        ];
        yield return
        [
            new ServiceRegistrationFailedAuditTranslator(),
            new ServiceRegistrationFailedEvent
            {
                Identity = identity,
                LastError = "provider rejected",
                CredentialKid = "kid-1",
            },
            "service.registration.failed",
            "service",
            "svc-a",
        ];
        yield return
        [
            new ServiceRegistrationRetiredAuditTranslator(),
            new ServiceRegistrationRetiredEvent
            {
                Identity = identity,
                NyxidServiceId = "nyx-svc",
                NyxidSlug = "slug",
            },
            "service.registration.retired",
            "service",
            "svc-a",
        ];
        yield return
        [
            new ServiceRevisionPublishedAuditTranslator(),
            new ServiceRevisionPublishedEvent
            {
                Identity = identity,
                RevisionId = "rev-1",
            },
            "service.revision.published",
            "service",
            "rev-1",
        ];
        yield return
        [
            new ServiceDeploymentActivatedAuditTranslator(),
            new ServiceDeploymentActivatedEvent
            {
                Identity = identity,
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
            },
            "service.deployment.activated",
            "service",
            "dep-1",
        ];
        yield return
        [
            new ServiceDeploymentDeactivatedAuditTranslator(),
            new ServiceDeploymentDeactivatedEvent
            {
                Identity = identity,
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
            },
            "service.deployment.deactivated",
            "service",
            "dep-1",
        ];
        yield return
        [
            new ScheduledDispatchConfiguredAuditTranslator(),
            new ScheduledDispatchConfiguredEvent
            {
                ScheduleId = "schedule-1",
                DisplayName = "Daily",
                Enabled = true,
                Headers =
                {
                    ["command_id"] = "cmd-from-event",
                    ["request_id"] = "req-from-event",
                },
            },
            "scheduled.dispatch.configured",
            "scheduled_dispatch",
            "schedule-1",
        ];
        yield return
        [
            new ScheduledDispatchEnabledAuditTranslator(),
            new ScheduledDispatchEnabledEvent { Reason = "resume" },
            "scheduled.dispatch.enabled",
            "scheduled_dispatch",
            "actor-1",
        ];
        yield return
        [
            new ScheduledDispatchDisabledAuditTranslator(),
            new ScheduledDispatchDisabledEvent { Reason = "pause" },
            "scheduled.dispatch.disabled",
            "scheduled_dispatch",
            "actor-1",
        ];
        yield return
        [
            new ScheduledDispatchDeletedAuditTranslator(),
            new ScheduledDispatchDeletedEvent { Reason = "cleanup" },
            "scheduled.dispatch.deleted",
            "scheduled_dispatch",
            "actor-1",
        ];
    }

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
