using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.Scheduled.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Scheduled;

public sealed class ScheduledLifecycleAuditTranslatorTests
{
    [Fact]
    public void AddScheduledAgents_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddScheduledAgents();
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<UserAgentCatalogMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<UserAgentCatalogMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(UserAgentCatalogUpsertedAuditTranslator),
                typeof(UserAgentCatalogTombstonedAuditTranslator),
                typeof(UserAgentCatalogSharedAuditTranslator),
                typeof(UserAgentCatalogUnsharedAuditTranslator),
                typeof(SkillRunnerInitializedAuditTranslator),
                typeof(SkillRunnerEnabledAuditTranslator),
                typeof(SkillRunnerDisabledAuditTranslator),
                typeof(SkillRunnerOneShotRetiredAuditTranslator),
                typeof(SkillRunnerExternalTriggerAdmittedAuditTranslator),
                typeof(SkillRunnerExternalTriggerRejectedAuditTranslator),
                typeof(SkillRunnerExecutionCompletedAuditTranslator),
                typeof(SkillRunnerExecutionFailedAuditTranslator),
                typeof(SkillRunnerExecutionRejectedAuditTranslator),
            ]);
    }

    [Fact]
    public void SkillRunnerExecutionCompletedTranslator_ShouldRecordStatus_AndNotLeakOutputBody()
    {
        var evt = new SkillRunnerExecutionCompletedEvent
        {
            ExecutionKind = SkillRunnerExecutionKind.Prompt,
            SkillName = "daily-report",
            SkillVersion = "1.2.3",
            WorkflowId = "wf-1",
            CronOccurrenceKey = "occ-1",
            Output = "confidential business report body that must not be recorded",
        };

        var record = new SkillRunnerExecutionCompletedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.execution.completed");
        record.Target.Kind.Should().Be("skill_runner");
        record.Target.Id.Should().Be("skill-runner-actor");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().Contain("status", "succeeded");
        record.Annotations.Should().Contain("skill_name", "daily-report");
        record.Annotations.Should().Contain("cron_occurrence_key", "occ-1");
        record.ToString().Should().NotContain("confidential business report body that must not be recorded");
    }

    [Fact]
    public void SkillRunnerExecutionFailedTranslator_ShouldRecordErrorClass_AndNotLeakErrorBody()
    {
        var evt = new SkillRunnerExecutionFailedEvent
        {
            ExecutionKind = SkillRunnerExecutionKind.Workflow,
            SkillName = "daily-report",
            ErrorCode = SkillRunnerExecutionErrorCode.WorkflowDispatchRejected,
            Error = "stack trace and payload echo that must not be recorded",
        };

        var record = new SkillRunnerExecutionFailedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.execution.failed");
        record.Target.Kind.Should().Be("skill_runner");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().Contain("status", "failed");
        record.Annotations.Should().Contain("error_code", "WorkflowDispatchRejected");
        record.ToString().Should().NotContain("stack trace and payload echo that must not be recorded");
    }

    [Fact]
    public void SkillRunnerExecutionRejectedTranslator_ShouldRecordReason()
    {
        var evt = new SkillRunnerExecutionRejectedEvent
        {
            Reason = "disabled",
            CronOccurrenceKey = "occ-2",
        };

        var record = new SkillRunnerExecutionRejectedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.execution.rejected");
        record.Target.Kind.Should().Be("skill_runner");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().Contain("status", "rejected");
        record.Annotations.Should().Contain("reason", "disabled");
        record.Annotations.Should().Contain("cron_occurrence_key", "occ-2");
    }

    [Fact]
    public void UserAgentCatalogUpsertedTranslator_ShouldProduceRecord_AndNotLeakApiKey()
    {
        var evt = new UserAgentCatalogUpsertedEvent
        {
            Entry = new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-abc",
                ScopeId = "scope-1",
                AgentType = "skill_runner",
                TemplateName = "daily-report",
                NyxProviderSlug = "provider-x",
                TargetPlatform = "lark",
#pragma warning disable CS0612
                NyxApiKey = "super-secret-nyx-api-key",
#pragma warning restore CS0612
            },
        };

        var record = new UserAgentCatalogUpsertedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.upserted");
        record.Target.Kind.Should().Be("user_agent_catalog");
        record.Target.Id.Should().Be("skill-runner-abc");
        record.ScopeId.Should().Be("scope-1");
        record.Annotations.Should().Contain("agent_type", "skill_runner");
        record.Annotations.Should().Contain("template_name", "daily-report");
        record.ToString().Should().NotContain("super-secret-nyx-api-key");
    }

    [Fact]
    public void UserAgentCatalogTombstonedTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new UserAgentCatalogTombstonedAuditTranslator()
            .Translate(Context(), Any.Pack(new UserAgentCatalogTombstonedEvent { AgentId = "skill-runner-abc" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.tombstoned");
        record.Target.Id.Should().Be("skill-runner-abc");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
    }

    [Fact]
    public void UserAgentCatalogSharedTranslator_ShouldRecordGranteeScope()
    {
        var evt = new UserAgentCatalogSharedEvent
        {
            AgentId = "skill-runner-abc",
            SharingGrant = new ScheduledAgentSharingGrant
            {
                SharedWithRegistrationScope = "scope-grantee",
                AllowTrigger = true,
                GrantedBy = "nyx-user-subject-should-not-be-recorded",
            },
        };

        var record = new UserAgentCatalogSharedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.shared");
        record.Annotations.Should().Contain("shared_with_registration_scope", "scope-grantee");
        record.Annotations.Should().Contain("allow_trigger", "true");
        record.ToString().Should().NotContain("nyx-user-subject-should-not-be-recorded");
    }

    [Fact]
    public void UserAgentCatalogUnsharedTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new UserAgentCatalogUnsharedAuditTranslator()
            .Translate(Context(), Any.Pack(new UserAgentCatalogUnsharedEvent { AgentId = "skill-runner-abc" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.unshared");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
    }

    [Fact]
    public void SkillRunnerInitializedTranslator_ShouldTargetOriginActor()
    {
        var evt = new SkillRunnerInitializedEvent
        {
            SkillName = "daily-report",
            TemplateName = "daily",
            ScopeId = "scope-1",
            ScheduleMode = SkillRunnerScheduleMode.Cron,
            Enabled = true,
            OutboundConfig = new SkillRunnerOutboundConfig
            {
#pragma warning disable CS0612
                NyxApiKey = "secret-outbound-api-key",
#pragma warning restore CS0612
            },
        };

        var record = new SkillRunnerInitializedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.initialized");
        record.Target.Kind.Should().Be("skill_runner");
        record.Target.Id.Should().Be("skill-runner-actor");
        record.ScopeId.Should().Be("scope-1");
        record.Annotations.Should().Contain("skill_name", "daily-report");
        record.ToString().Should().NotContain("secret-outbound-api-key");
    }

    [Fact]
    public void SkillRunnerDisabledTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new SkillRunnerDisabledAuditTranslator()
            .Translate(Context(), Any.Pack(new SkillRunnerDisabledEvent { Reason = "operator" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.disabled");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
        record.Annotations.Should().Contain("reason", "operator");
    }

    [Fact]
    public void SkillRunnerOneShotRetiredTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new SkillRunnerOneShotRetiredAuditTranslator()
            .Translate(Context(), Any.Pack(new SkillRunnerOneShotRetiredEvent { Reason = "completed" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.one-shot.retired");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
    }

    [Fact]
    public void SkillRunnerExternalTriggerAdmittedTranslator_ShouldRecordSafeIds_AndNotLeakPayloadSummary()
    {
        var evt = new SkillRunnerExternalTriggerAdmittedEvent
        {
            Identity = new SkillRunnerExternalTriggerIdentity
            {
                SourceId = "src-1",
                DeliveryId = "delivery-1",
                AdmissionId = "adm-1",
                Kind = ExternalTriggerSourceKind.Webhook,
                PayloadSummary = "raw inbound message content must not be recorded",
            },
        };

        var record = new SkillRunnerExternalTriggerAdmittedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.external-trigger.admitted");
        record.Annotations.Should().Contain("source_id", "src-1");
        record.Annotations.Should().Contain("delivery_id", "delivery-1");
        record.Annotations.Should().Contain("admission_id", "adm-1");
        record.ToString().Should().NotContain("raw inbound message content must not be recorded");
    }

    [Fact]
    public void SkillRunnerExternalTriggerRejectedTranslator_ShouldRecordReason()
    {
        var evt = new SkillRunnerExternalTriggerRejectedEvent
        {
            Identity = new SkillRunnerExternalTriggerIdentity
            {
                SourceId = "src-1",
                DeliveryId = "delivery-1",
                Kind = ExternalTriggerSourceKind.ChannelInbound,
                PayloadSummary = "raw inbound message content must not be recorded",
            },
            Reason = "not-authorized",
        };

        var record = new SkillRunnerExternalTriggerRejectedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.skill-runner.external-trigger.rejected");
        record.Annotations.Should().Contain("source_id", "src-1");
        record.Annotations.Should().Contain("reason", "not-authorized");
        record.ToString().Should().NotContain("raw inbound message content must not be recorded");
    }

    [Fact]
    public void ScheduledTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new UserAgentCatalogUpsertedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = "skill-runner-actor", EventId = "event-1", Version = 7 },
            "skill-runner-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "corr-1");

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TMaterializer);
    }
}
