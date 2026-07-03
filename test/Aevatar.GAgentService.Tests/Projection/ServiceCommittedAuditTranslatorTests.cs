using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCommittedAuditTranslatorTests
{
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
        record.Outcome.Should().Be(AuditOutcome.Committed);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.TargetKind.Should().Be(targetKind);
        record.TargetId.Should().Be(targetId);
        record.TargetVersion.Should().Be(17);
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
}
