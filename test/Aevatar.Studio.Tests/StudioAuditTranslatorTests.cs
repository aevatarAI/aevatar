using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Projection.Audit;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioAuditTranslatorTests
{
    [Fact]
    public void AddStudioProjectionComponents_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<StudioMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<StudioMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<StudioMaterializationContext>>()
            .Should()
            .NotBeNull();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(StudioMemberCreatedAuditTranslator),
                typeof(StudioMemberImplementationUpdatedAuditTranslator),
                typeof(StudioMemberReassignedAuditTranslator),
                typeof(StudioMemberDeletedAuditTranslator),
                typeof(StudioTeamCreatedAuditTranslator),
                typeof(StudioTeamUpdatedAuditTranslator),
                typeof(StudioTeamArchivedAuditTranslator),
                typeof(StudioMemberRenamedAuditTranslator),
                typeof(StudioMemberBindingCompletedAuditTranslator),
                typeof(StudioMemberBindingFailedAuditTranslator),
                typeof(StudioMemberBindingRejectedAuditTranslator),
                typeof(StudioTeamEntryMemberChangedAuditTranslator),
                typeof(ActorRegisteredAuditTranslator),
                typeof(ActorUnregisteredAuditTranslator),
                typeof(ConnectorCatalogSavedAuditTranslator),
                typeof(ConnectorDraftSavedAuditTranslator),
                typeof(ConnectorDraftDeletedAuditTranslator),
                typeof(RoleCatalogSavedAuditTranslator),
                typeof(RoleDraftSavedAuditTranslator),
                typeof(RoleDraftDeletedAuditTranslator),
                typeof(UserConfigUpdatedAuditTranslator),
                typeof(UserConfigGithubUsernameUpdatedAuditTranslator),
                typeof(MemoryEntriesClearedAuditTranslator),
                typeof(ConversationDeletedAuditTranslator),
                typeof(NyxIdChatActionRequestedAuditTranslator),
                typeof(NyxIdChatActionContinuationResolvedAuditTranslator),
                typeof(NyxIdChatActionPostconditionResolvedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(StudioSeedEvents))]
    public void StudioSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId,
        ExpectedAuditFields expected)
    {
        var record = translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

        record.OperationName.Should().Be(operationName);
        if (translator is StudioMemberBindingFailedAuditTranslator or StudioMemberBindingRejectedAuditTranslator)
        {
            record.Outcome.Should().Be(AuditOutcome.Error);
            record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
            record.Failure.Should().NotBeNull();
            record.Annotations.Should().NotContainKey("failure_code");
            record.Annotations.Should().NotContainKey("failure_message");
            record.Failure.Code.Should().Be(translator is StudioMemberBindingFailedAuditTranslator
                ? "studio_member_binding_failed"
                : "studio_member_binding_rejected");
            record.ToString().Should().NotContain("compactSecretToken123");
        }
        else
        {
            record.Outcome.Should().Be(AuditOutcome.Success);
            record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
        }
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.ScopeId.Should().Be(expected.ScopeId);
        record.SensitivityLevel.Should().Be(expected.SensitivityLevel);
        record.Correlation.CommandId.Should().Be("cmd-1");
        record.Correlation.RequestId.Should().Be("req-1");
        record.Correlation.TraceId.Should().BeEmpty();
        record.Correlation.CorrelationId.Should().Be("corr-1");
        record.CommittedFactRef.StateVersion.Should().Be(9);
        AssertDestructiveAnnotation(record, expected.IsDestructive);
        foreach (var annotation in expected.Annotations)
            record.Annotations.Should().Contain(annotation.Key, annotation.Value);
    }

    [Fact]
    public void StudioTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new StudioMemberCreatedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    public static IEnumerable<object[]> StudioSeedEvents()
    {
        yield return
        [
            new StudioMemberCreatedAuditTranslator(),
            new StudioMemberCreatedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "member",
                PublishedServiceId = "svc-alpha",
            },
            "studio.member.created",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioMemberImplementationUpdatedAuditTranslator(),
            new StudioMemberImplementationUpdatedEvent
            {
                ImplementationKind = StudioMemberImplementationKind.Workflow,
            },
            "studio.member.implementation.updated",
            "studio_member",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["implementation_kind"] = StudioMemberImplementationKind.Workflow.ToString(),
                }),
        ];
        yield return
        [
            new StudioMemberReassignedAuditTranslator(),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                FromTeamId = "team-old",
                ToTeamId = "team-alpha",
            },
            "studio.member.reassigned",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from_team_id"] = "team-old",
                    ["to_team_id"] = "team-alpha",
                }),
        ];
        yield return
        [
            new StudioMemberDeletedAuditTranslator(),
            new StudioMemberDeletedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                PreviousTeamId = "team-old",
                PublishedServiceId = "svc-alpha",
            },
            "studio.member.deleted",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Restricted,
                true,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["previous_team_id"] = "team-old",
                    ["published_service_id"] = "svc-alpha",
                }),
        ];
        yield return
        [
            new StudioTeamCreatedAuditTranslator(),
            new StudioTeamCreatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team",
            },
            "studio.team.created",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioTeamUpdatedAuditTranslator(),
            new StudioTeamUpdatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team 2",
            },
            "studio.team.updated",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioTeamArchivedAuditTranslator(),
            new StudioTeamArchivedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
            },
            "studio.team.archived",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioMemberRenamedAuditTranslator(),
            new StudioMemberRenamedEvent
            {
                DisplayName = "Renamed Member",
                Description = "desc",
            },
            "studio.member.renamed",
            "studio_member",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["display_name"] = "Renamed Member",
                }),
        ];
        yield return
        [
            new StudioMemberBindingCompletedAuditTranslator(),
            new StudioMemberBindingCompletedEvent
            {
                BindingRunId = "run-1",
                PublishedServiceId = "svc-alpha",
                RevisionId = "rev-1",
                ImplementationKind = StudioMemberImplementationKind.Workflow,
                ExpectedActorId = "actor-1",
            },
            "studio.member.binding.completed",
            "studio_member",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["binding_run_id"] = "run-1",
                    ["published_service_id"] = "svc-alpha",
                    ["revision_id"] = "rev-1",
                    ["implementation_kind"] = StudioMemberImplementationKind.Workflow.ToString(),
                }),
        ];
        yield return
        [
            new StudioMemberBindingFailedAuditTranslator(),
            new StudioMemberBindingFailedEvent
            {
                BindingRunId = "run-1",
                Failure = new StudioMemberBindingFailure
                {
                    Code = "compactSecretToken123",
                    Message = "compactSecretToken123",
                },
            },
            "studio.member.binding.failed",
            "studio_member",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["binding_run_id"] = "run-1",
                }),
        ];
        yield return
        [
            new StudioMemberBindingRejectedAuditTranslator(),
            new StudioMemberBindingRejectedEvent
            {
                BindingRunId = "run-1",
                ScopeId = "scope-alpha",
                MemberId = "m-alpha",
                Failure = new StudioMemberBindingFailure
                {
                    Code = "compactSecretToken123",
                    Message = "compactSecretToken123",
                },
            },
            "studio.member.binding.rejected",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["binding_run_id"] = "run-1",
                }),
        ];
        yield return
        [
            new StudioTeamEntryMemberChangedAuditTranslator(),
            new StudioTeamEntryMemberChangedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                EntryMemberId = "m-alpha",
            },
            "studio.team.entry-member.changed",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["entry_member_id"] = "m-alpha",
                }),
        ];
        yield return
        [
            new ActorRegisteredAuditTranslator(),
            new ActorRegisteredEvent
            {
                AgentKind = "chat-agent",
                ActorId = "actor-1",
            },
            "registry.actor.registered",
            "registry_actor",
            "actor-1",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["agent_kind"] = "chat-agent",
                }),
        ];
        yield return
        [
            new ActorUnregisteredAuditTranslator(),
            new ActorUnregisteredEvent
            {
                AgentKind = "chat-agent",
                ActorId = "actor-1",
            },
            "registry.actor.unregistered",
            "registry_actor",
            "actor-1",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Restricted,
                true,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["agent_kind"] = "chat-agent",
                }),
        ];
        yield return
        [
            new ConnectorCatalogSavedAuditTranslator(),
            new ConnectorCatalogSavedEvent
            {
                Connectors =
                {
                    new ConnectorDefinitionEntry { Name = "gh", Type = "http" },
                    new ConnectorDefinitionEntry { Name = "slack", Type = "mcp" },
                },
            },
            "connector.catalog.saved",
            "connector_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["connector_count"] = "2",
                    ["connector_names"] = "gh,slack",
                    ["connector_types"] = "http,mcp",
                }),
        ];
        yield return
        [
            new ConnectorDraftSavedAuditTranslator(),
            new ConnectorDraftSavedEvent
            {
                Draft = new ConnectorDefinitionEntry { Name = "gh", Type = "http" },
            },
            "connector.draft.saved",
            "connector_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["connector_name"] = "gh",
                    ["connector_type"] = "http",
                }),
        ];
        yield return
        [
            new ConnectorDraftDeletedAuditTranslator(),
            new ConnectorDraftDeletedEvent(),
            "connector.draft.deleted",
            "connector_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
        yield return
        [
            new RoleCatalogSavedAuditTranslator(),
            new RoleCatalogSavedEvent
            {
                Roles =
                {
                    new RoleDefinitionEntry { Id = "r1", Name = "Writer", Model = "gpt" },
                    new RoleDefinitionEntry { Id = "r2", Name = "Editor", Model = "claude" },
                },
            },
            "role.catalog.saved",
            "role_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["role_count"] = "2",
                    ["role_ids"] = "r1,r2",
                    ["role_names"] = "Writer,Editor",
                    ["role_models"] = "gpt,claude",
                }),
        ];
        yield return
        [
            new RoleDraftSavedAuditTranslator(),
            new RoleDraftSavedEvent
            {
                Draft = new RoleDefinitionEntry { Id = "r1", Name = "Writer", Model = "gpt" },
            },
            "role.draft.saved",
            "role_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["role_id"] = "r1",
                    ["role_name"] = "Writer",
                    ["role_model"] = "gpt",
                }),
        ];
        yield return
        [
            new RoleDraftDeletedAuditTranslator(),
            new RoleDraftDeletedEvent(),
            "role.draft.deleted",
            "role_catalog",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
        yield return
        [
            new UserConfigUpdatedAuditTranslator(),
            new UserConfigUpdatedEvent
            {
                RuntimeMode = "remote",
                LocalRuntimeBaseUrl = "",
                RemoteRuntimeBaseUrl = "https://runtime.example",
                DefaultModel = "gpt",
                PreferredLlmRoute = "route-a",
                MaxToolRounds = 4,
                GithubUsername = "octocat",
            },
            "user-config.updated",
            "user_config",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["runtime_mode"] = "remote",
                    ["has_local_runtime_base_url"] = "false",
                    ["has_remote_runtime_base_url"] = "true",
                    ["default_model"] = "gpt",
                    ["preferred_llm_route"] = "route-a",
                    ["max_tool_rounds"] = "4",
                    ["has_github_username"] = "true",
                }),
        ];
        yield return
        [
            new UserConfigGithubUsernameUpdatedAuditTranslator(),
            new UserConfigGithubUsernameUpdatedEvent
            {
                GithubUsername = "octocat",
            },
            "user-config.github-username.updated",
            "user_config",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["github_username"] = "octocat",
                }),
        ];
        yield return
        [
            new MemoryEntriesClearedAuditTranslator(),
            new MemoryEntriesClearedEvent(),
            "user-memory.cleared",
            "user_memory",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
        yield return
        [
            new ConversationDeletedAuditTranslator(),
            new ConversationDeletedEvent
            {
                ConversationId = "conv-1",
                ScopeId = "scope-alpha",
            },
            "conversation.deleted",
            "chat_conversation",
            "conv-1",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
    }

    private static IReadOnlyDictionary<string, string> EmptyAnnotations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static void AssertDestructiveAnnotation(AuditRecord record, bool isDestructive)
    {
        if (isDestructive)
            record.Annotations.Should().Contain("is_destructive", "true");
        else
            record.Annotations.Should().NotContainKey("is_destructive");
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "studio-member-actor",
                EventId = "event-1",
                Version = 9,
            },
            "studio-member-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
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

    public sealed record ExpectedAuditFields(
        string ScopeId,
        AuditSensitivityLevel SensitivityLevel,
        bool IsDestructive,
        IReadOnlyDictionary<string, string> Annotations);
}
