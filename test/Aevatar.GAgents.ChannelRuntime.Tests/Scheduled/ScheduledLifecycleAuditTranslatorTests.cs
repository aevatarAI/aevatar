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
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Scheduled;

public sealed class ScheduledLifecycleAuditTranslatorTests
{
    [Fact]
    public void AddScheduledAgents_ShouldWireCatalogCommittedAuditMaterializerAndTranslators()
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
            ]);
    }

    [Fact]
    public void UserAgentCatalogUpsertedTranslator_ShouldRecordSafeCatalogLabels()
    {
        var evt = new UserAgentCatalogUpsertedEvent
        {
            Entry = new UserAgentCatalogEntry
            {
                AgentId = "scheduled-workflow-1",
                AgentType = ScheduledWorkflowAgentDefaults.AgentType,
                TemplateName = "daily-report",
                ScopeId = "scope-1",
                NyxProviderSlug = "api-lark-bot",
                TargetPlatform = "lark",
            },
        };

        var record = new UserAgentCatalogUpsertedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.upserted");
        record.Target.Kind.Should().Be("user_agent_catalog");
        record.Target.Id.Should().Be("scheduled-workflow-1");
        record.ScopeId.Should().Be("scope-1");
        record.Annotations.Should().Contain("agent_type", ScheduledWorkflowAgentDefaults.AgentType);
        record.Annotations.Should().Contain("template_name", "daily-report");
        record.Annotations.Should().Contain("target_platform", "lark");
    }

    [Fact]
    public void UserAgentCatalogTombstonedTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new UserAgentCatalogTombstonedAuditTranslator()
            .Translate(Context(), Any.Pack(new UserAgentCatalogTombstonedEvent { AgentId = "scheduled-workflow-1" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("scheduled.user-agent-catalog.tombstoned");
        record.Target.Id.Should().Be("scheduled-workflow-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        AssertDestructiveAnnotation(record, true);
    }

    [Fact]
    public void UserAgentCatalogSharingTranslators_ShouldRecordAudienceLabels()
    {
        var shared = new UserAgentCatalogSharedAuditTranslator()
            .Translate(Context(), Any.Pack(new UserAgentCatalogSharedEvent
            {
                AgentId = "scheduled-workflow-1",
                SharingGrant = new ScheduledAgentSharingGrant
                {
                    SharedWithRegistrationScope = "scope-bot-2",
                    AllowTrigger = true,
                },
            }))
            .Should()
            .ContainSingle()
            .Subject;
        var unshared = new UserAgentCatalogUnsharedAuditTranslator()
            .Translate(Context(), Any.Pack(new UserAgentCatalogUnsharedEvent { AgentId = "scheduled-workflow-1" }))
            .Should()
            .ContainSingle()
            .Subject;

        shared.OperationName.Should().Be("scheduled.user-agent-catalog.shared");
        shared.Annotations.Should().Contain("shared_with_registration_scope", "scope-bot-2");
        shared.Annotations.Should().Contain("allow_trigger", "true");
        unshared.OperationName.Should().Be("scheduled.user-agent-catalog.unshared");
        AssertDestructiveAnnotation(unshared, true);
    }

    private static void AssertDestructiveAnnotation(AuditRecord record, bool isDestructive)
    {
        if (isDestructive)
            record.Annotations.Should().Contain("is_destructive", "true");
        else
            record.Annotations.Should().NotContainKey("is_destructive");
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TMaterializer);
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = UserAgentCatalogGAgent.WellKnownId, EventId = "event-1", Version = 7 },
            UserAgentCatalogGAgent.WellKnownId,
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-16T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "correlation-1");
}
