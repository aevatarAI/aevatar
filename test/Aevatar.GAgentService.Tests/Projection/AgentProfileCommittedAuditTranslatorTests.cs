using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.AgentProfiles;
using Aevatar.GAgentService.Projection.Audit;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileCommittedAuditTranslatorTests
{
    private const string DraftInstructions = "draft instructions must stay private";
    private const string SkillInstructions = "sealed skill instructions must stay private";
    private const string AssetBody = "asset body must stay private";
    private const string Bearer = "Bearer test-credential-7";
    private const string RawRemoteError = "raw remote error must stay private";

    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-24T01:00:00+00:00");

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterProfileAuditOnceOnAuthorityContextsOnly()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();
        using var provider = services.BuildServiceProvider();

        var expectedTranslators = new[]
        {
            typeof(AgentProfileProvisioningStartedAuditTranslator),
            typeof(AgentProfileProvisioningCompletedAuditTranslator),
            typeof(AgentProfileProvisioningFailedAuditTranslator),
            typeof(AgentProfilePublishedSummaryObservedAuditTranslator),
            typeof(AgentProfileInitializedAuditTranslator),
            typeof(AgentProfileInitializationRejectedAuditTranslator),
            typeof(AgentProfileDraftUpdatedAuditTranslator),
            typeof(AgentProfileSkillBindingUpsertedAuditTranslator),
            typeof(AgentProfileSkillBindingRemovedAuditTranslator),
            typeof(AgentProfilePublishedAuditTranslator),
            typeof(AgentProfilePublishNoChangeAuditTranslator),
            typeof(AgentProfileMutationNoChangeAuditTranslator),
            typeof(AgentProfileMutationRejectedAuditTranslator),
        };
        var translatorTypes = provider.GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .ToArray();
        foreach (var expected in expectedTranslators)
            translatorTypes.Count(type => type == expected).Should().Be(1, expected.Name);

        AssertCommittedAuditMaterializerRegistered<AgentProfileNamespaceCurrentStateProjectionContext>(
            services,
            provider);
        AssertCommittedAuditMaterializerRegistered<AgentProfileOwnerCurrentStateProjectionContext>(
            services,
            provider);
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType ==
                typeof(IProjectionArtifactMaterializer<AgentProfileExecutionCurrentStateProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<AgentProfileExecutionCurrentStateProjectionContext>>(
                    descriptor.ImplementationType));
    }

    [Fact]
    public void CreatedTranslator_ShouldRecordTypedIdentityAndCurrentDraftFactsWithoutAuthoredContent()
    {
        var evt = new AgentProfileInitializedEvent
        {
            Operation = Operation("create"),
            Identity = Identity(),
            InitialContent = SensitiveContent(),
            DraftRevision = 1,
            DraftSha256 = Digest(0x11),
            NamespaceActorId = AgentProfileActorIds.Namespace,
            ProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
        };

        var translator = new AgentProfileInitializedAuditTranslator();
        var record = Translate(translator, evt);

        translator.EventTypeUrl.Should().Be(
            AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileInitializedEvent.Descriptor));
        AssertCommon(record, "agent_profile.created", "applied");
        record.Annotations.Should().Contain("draft_revision", "1");
        record.Annotations.Should().Contain("draft_sha256", Hex(Digest(0x11)));
        AssertSensitiveValuesOmitted(record);
    }

    [Fact]
    public void DraftUpdatedTranslator_ShouldRecordOnlyCommittedRevisionAndDigestFacts()
    {
        var evt = new AgentProfileDraftUpdatedEvent
        {
            Operation = Operation("draft"),
            Identity = Identity(),
            Content = SensitiveContent(),
            DraftRevision = 2,
            DraftSha256 = Digest(0x22),
            Outcome = Outcome(
                "draft",
                AgentProfileMutationStatus.Applied,
                draftRevision: 2,
                draftSha256: Digest(0x22),
                publishedRevision: 1,
                publishedSnapshotSha256: Digest(0x19)),
        };

        var record = Translate(new AgentProfileDraftUpdatedAuditTranslator(), evt);

        AssertCommon(record, "agent_profile.draft.updated", "applied");
        record.Annotations.Should().Contain("draft_revision", "2");
        record.Annotations.Should().Contain("draft_sha256", Hex(Digest(0x22)));
        record.Annotations.Should().Contain("published_revision", "1");
        record.Annotations.Should().Contain("published_snapshot_sha256", Hex(Digest(0x19)));
        record.Annotations.Keys.Should().NotContain(key => key.StartsWith("old_", StringComparison.Ordinal));
        AssertSensitiveValuesOmitted(record);
    }

    [Fact]
    public void ExactSkillBindingTranslators_ShouldRecordExactReferenceAndRemovalFacts()
    {
        var upserted = new AgentProfileSkillBindingUpsertedEvent
        {
            Operation = Operation("upsert"),
            Identity = Identity(),
            Binding = Binding(),
            Content = SensitiveContent(),
            DraftRevision = 3,
            DraftSha256 = Digest(0x33),
            Outcome = Outcome(
                "upsert",
                AgentProfileMutationStatus.Applied,
                draftRevision: 3,
                draftSha256: Digest(0x33)),
        };
        var removed = new AgentProfileSkillBindingRemovedEvent
        {
            Operation = Operation("remove"),
            Identity = Identity(),
            BindingId = "binding-alpha",
            Content = SensitiveContent(),
            DraftRevision = 4,
            DraftSha256 = Digest(0x44),
            Outcome = Outcome(
                "remove",
                AgentProfileMutationStatus.Applied,
                draftRevision: 4,
                draftSha256: Digest(0x44)),
        };

        var upsertRecord = Translate(new AgentProfileSkillBindingUpsertedAuditTranslator(), upserted);
        var removedRecord = Translate(new AgentProfileSkillBindingRemovedAuditTranslator(), removed);

        AssertCommon(upsertRecord, "agent_profile.skill_binding.upserted", "applied");
        upsertRecord.Annotations.Should().Contain("binding_id", "binding-alpha");
        upsertRecord.Annotations.Should().Contain("activation_mode", "routed");
        upsertRecord.Annotations.Should().Contain("skill_guid", "11111111-1111-1111-1111-111111111111");
        upsertRecord.Annotations.Should().Contain("literal_version", "1.2");
        upsertRecord.Annotations.Should().Contain("expected_name", "calendar");
        upsertRecord.Annotations.Should().Contain("expected_publisher_id", "publisher-alpha");
        AssertCommon(removedRecord, "agent_profile.skill_binding.removed", "applied");
        removedRecord.Annotations.Should().Contain("binding_id", "binding-alpha");
        removedRecord.Annotations.Should().Contain("draft_revision", "4");
        AssertSensitiveValuesOmitted(upsertRecord);
        AssertSensitiveValuesOmitted(removedRecord);
    }

    [Fact]
    public void PublishTranslators_ShouldRecordCommittedDigestsAndStableNoChangeOutcomeWithoutSealedBodies()
    {
        var published = PublishedSnapshot();
        var applied = new AgentProfilePublishedEvent
        {
            Operation = Operation("publish"),
            Identity = Identity(),
            Snapshot = published,
            Outcome = Outcome(
                "publish",
                AgentProfileMutationStatus.Applied,
                draftRevision: 4,
                draftSha256: published.SourceDraftSha256,
                publishedRevision: 2,
                publishedSnapshotSha256: published.SnapshotSha256),
        };
        var noChange = new AgentProfilePublishNoChangeEvent
        {
            Operation = Operation("publish-no-change"),
            Identity = Identity(),
            Summary = new AgentProfilePublishedSummary
            {
                Reference = Identity().Reference,
                DisplayName = "Sensitive published display",
                Purpose = "Sensitive published purpose",
                PublishedRevision = 2,
                SnapshotSha256 = published.SnapshotSha256,
            },
            Outcome = Outcome(
                "publish-no-change",
                AgentProfileMutationStatus.NoChange,
                draftRevision: 4,
                draftSha256: published.SourceDraftSha256,
                publishedRevision: 2,
                publishedSnapshotSha256: published.SnapshotSha256),
        };

        var appliedRecord = Translate(new AgentProfilePublishedAuditTranslator(), applied);
        var noChangeRecord = Translate(new AgentProfilePublishNoChangeAuditTranslator(), noChange);

        AssertCommon(appliedRecord, "agent_profile.published", "applied");
        appliedRecord.Annotations.Should().Contain("published_revision", "2");
        appliedRecord.Annotations.Should().Contain(
            "published_source_draft_sha256",
            Hex(published.SourceDraftSha256));
        appliedRecord.Annotations.Should().Contain(
            "published_snapshot_sha256",
            Hex(published.SnapshotSha256));
        AssertCommon(noChangeRecord, "agent_profile.publish.no_change", "no_change");
        noChangeRecord.Annotations.Should().Contain("published_revision", "2");
        noChangeRecord.Annotations.Should().Contain(
            "published_snapshot_sha256",
            Hex(published.SnapshotSha256));
        AssertSensitiveValuesOmitted(appliedRecord);
        AssertSensitiveValuesOmitted(noChangeRecord);
    }

    [Fact]
    public void MutationRejectedTranslator_ShouldUseStableFailureCodeAndOmitRawDiagnosticText()
    {
        var evt = new AgentProfileMutationRejectedEvent
        {
            Operation = Operation("rejected"),
            Identity = Identity(),
            Outcome = Outcome(
                "rejected",
                AgentProfileMutationStatus.Rejected,
                draftRevision: 4,
                draftSha256: Digest(0x44),
                publishedRevision: 2,
                publishedSnapshotSha256: Digest(0x55),
                diagnostic: new AgentProfileSafeDiagnostic
                {
                    Code = "AUTHORITY_VERSION_CONFLICT",
                    Message = $"{RawRemoteError}: {Bearer}",
                    Path = "expected_authority_state_version",
                }),
        };

        var record = Translate(new AgentProfileMutationRejectedAuditTranslator(), evt);

        AssertCommon(record, "agent_profile.mutation.rejected", "rejected");
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Code.Should().Be("AUTHORITY_VERSION_CONFLICT");
        record.Failure.SanitizedMessage.Should().Be("AUTHORITY_VERSION_CONFLICT");
        record.Annotations.Should().Contain("failure_code", "AUTHORITY_VERSION_CONFLICT");
        AssertSensitiveValuesOmitted(record);
    }

    [Fact]
    public void Translator_ShouldReturnNoRecordForWrongExactEventType()
    {
        var translator = new AgentProfileDraftUpdatedAuditTranslator();

        var records = translator.Translate(
            Context(translator.EventTypeUrl),
            Any.Pack(new StringValue { Value = DraftInstructions }));

        records.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishedSummaryExactReplay_ShouldCommitNoSecondNamespaceFactOrAuditRecord()
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingProfileEventPublisher();
        var agent = GAgentServiceTestKit
            .CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
                store,
                AgentProfileActorIds.Namespace,
                static () => new AgentProfileNamespaceGAgent());
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var create = CreateCommand();
        await agent.HandleCreateAsync(create);
        await GAgentServiceTestKit.DispatchAsync(
            agent,
            new AgentProfileInitializedContinuation
            {
                Operation = create.Operation.Clone(),
                Identity = create.Identity.Clone(),
                ProfileActorId = create.ProfileActorId,
                DraftRevision = 1,
                DraftSha256 = AgentProfileDeterminism.ComputeDraftSha256(create.InitialContent),
            },
            create.ProfileActorId);
        var observation = PublishedSummaryCommand(create);
        await GAgentServiceTestKit.DispatchAsync(agent, observation, create.ProfileActorId);
        var translator = new AgentProfilePublishedSummaryObservedAuditTranslator();
        var beforeReplay = await store.GetEventsAsync(agent.Id);

        var replay = observation.Clone();
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(replay.Operation, "summary-replay");
        await GAgentServiceTestKit.DispatchAsync(agent, replay, create.ProfileActorId);

        var afterReplay = await store.GetEventsAsync(agent.Id);
        afterReplay.Should().HaveCount(beforeReplay.Count);
        afterReplay.Count(stateEvent =>
                string.Equals(
                    stateEvent.EventData.TypeUrl,
                    translator.EventTypeUrl,
                    StringComparison.Ordinal))
            .Should().Be(1);
        afterReplay
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfilePublishedSummaryObservedEvent.Descriptor))
            .SelectMany(stateEvent => translator.Translate(
                Context(translator.EventTypeUrl, stateEvent),
                stateEvent.EventData))
            .Should()
            .ContainSingle();
    }

    private static void AssertCommon(
        AuditRecord record,
        string operationName,
        string outcomeCode)
    {
        record.OperationName.Should().Be(operationName);
        record.Target.Kind.Should().Be("agent_profile");
        record.Target.Id.Should().Be("prof-alpha");
        record.ScopeId.Should().Be("scope-alpha");
        record.Correlation.CommandId.Should().StartWith("cmd-");
        record.Correlation.CorrelationId.Should().StartWith("corr-");
        record.Annotations.Should().ContainKey("operation_id");
        record.Annotations.Should().Contain("profile_id", "prof-alpha");
        record.Annotations.Should().Contain("owner_kind", "user");
        record.Annotations.Should().Contain("owner_handle", "alice");
        record.Annotations.Should().Contain("profile_slug", "assistant");
        record.Annotations.Should().Contain("outcome_code", outcomeCode);
        record.CommittedFactRef.EventTypeUrl.Should().StartWith("type.googleapis.com/");
        record.CommittedFactRef.StateVersion.Should().Be(17);
    }

    private static void AssertSensitiveValuesOmitted(AuditRecord record)
    {
        var serialized = record.ToString();
        foreach (var forbidden in new[]
                 {
                     DraftInstructions,
                     SkillInstructions,
                     AssetBody,
                     Bearer,
                     RawRemoteError,
                     "owner-secret-subject",
                     "Sensitive published display",
                     "Sensitive published purpose",
                 })
        {
            serialized.Should().NotContain(forbidden);
            record.Annotations.Values.Should().NotContain(value =>
                value.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static AuditRecord Translate(IAuditCommittedEventTranslator translator, IMessage evt)
    {
        var records = translator.Translate(Context(translator.EventTypeUrl), Any.Pack(evt));
        return records.Should().ContainSingle().Subject;
    }

    private static CommittedAuditTranslationContext Context(
        string eventTypeUrl,
        StateEvent? stateEvent = null) =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation { CorrelationId = "context-correlation" },
            },
            new CommittedStateEventPublished(),
            stateEvent ?? new StateEvent
            {
                AgentId = AgentProfileActorIds.Profile("prof-alpha"),
                EventId = "agent-profile-event-17",
                Version = 17,
            },
            AgentProfileActorIds.Profile("prof-alpha"),
            eventTypeUrl,
            ObservedAt,
            "context-command",
            "context-request",
            "context-correlation");

    private static AgentProfileIdentity Identity() =>
        GAgentServiceTestKit.CreateAgentProfileIdentity(ownerSubjectId: "owner-secret-subject");

    private static AgentProfileOperationFact Operation(string suffix) =>
        new()
        {
            OperationId = $"operation-{suffix}",
            CommandId = $"cmd-{suffix}",
            CorrelationId = $"corr-{suffix}",
            InputSha256 = Digest(0x71),
        };

    private static AgentProfileMutationOutcome Outcome(
        string suffix,
        AgentProfileMutationStatus status,
        long draftRevision,
        ByteString draftSha256,
        long publishedRevision = 0,
        ByteString? publishedSnapshotSha256 = null,
        AgentProfileSafeDiagnostic? diagnostic = null) =>
        new()
        {
            Operation = Operation(suffix),
            Status = status,
            Diagnostic = diagnostic,
            DraftRevision = draftRevision,
            DraftSha256 = draftSha256,
            PublishedRevision = publishedRevision,
            PublishedSnapshotSha256 = publishedSnapshotSha256 ?? ByteString.Empty,
        };

    private static AgentProfileContent SensitiveContent() =>
        new()
        {
            DisplayName = "Assistant",
            Purpose = "Focused help",
            Instructions = DraftInstructions,
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
            SkillBindings = { Binding() },
        };

    private static AgentProfileSkillBinding Binding() =>
        new()
        {
            BindingId = "binding-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = new ExactOrnnSkillReference
            {
                SkillGuid = "11111111-1111-1111-1111-111111111111",
                LiteralVersion = "1.2",
                ExpectedName = "calendar",
                ExpectedPublisherId = "publisher-alpha",
            },
        };

    private static AgentProfilePublishedSnapshot PublishedSnapshot()
    {
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = Identity(),
            DisplayName = "Sensitive published display",
            Purpose = "Sensitive published purpose",
            Instructions = DraftInstructions,
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
            PublishedRevision = 2,
            SourceDraftSha256 = Digest(0x44),
            SnapshotSha256 = Digest(0x55),
        };
        snapshot.SkillBindings.Add(new SealedAgentProfileSkillBinding
        {
            BindingId = "binding-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = new SealedAgentProfileSkill
            {
                ExactReference = Binding().Skill,
                ContentSha256 = Digest(0x66),
                Package = new ResolvedOrnnSkillPackage
                {
                    SkillGuid = "11111111-1111-1111-1111-111111111111",
                    LiteralVersion = "1.2",
                    CanonicalName = "calendar",
                    PublisherId = "publisher-alpha",
                    Instructions = SkillInstructions,
                    Assets =
                    {
                        new AgentProfileNamedTextAsset
                        {
                            Path = "assets/private.txt",
                            Content = AssetBody,
                        },
                    },
                },
            },
        });
        return snapshot;
    }

    private static CreateAgentProfileCommand CreateCommand()
    {
        var identity = Identity();
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        return new CreateAgentProfileCommand
        {
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                "operation-create",
                AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content)),
            Identity = identity,
            InitialContent = content,
            ProfileActorId = AgentProfileActorIds.Profile(identity.ProfileId),
        };
    }

    private static ObserveAgentProfilePublishedSummaryCommand PublishedSummaryCommand(
        CreateAgentProfileCommand create) =>
        new()
        {
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation("operation-summary", Digest(0x61)),
            Identity = create.Identity.Clone(),
            Summary = new AgentProfilePublishedSummary
            {
                Reference = create.Identity.Reference.Clone(),
                DisplayName = create.InitialContent.DisplayName,
                Purpose = create.InitialContent.Purpose,
                PublishedRevision = 1,
                SnapshotSha256 = Digest(0x61),
            },
        };

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    private static string Hex(ByteString value) =>
        Convert.ToHexString(value.Span).ToLowerInvariant();

    private static void AssertCommittedAuditMaterializerRegistered<TContext>(
        IServiceCollection services,
        IServiceProvider provider)
        where TContext : class, IProjectionMaterializationContext
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<TContext>) &&
            IsObservedProjectionArtifactMaterializerFor<CommittedAuditArtifactMaterializer<TContext>>(
                descriptor.ImplementationType));
        provider.GetRequiredService<CommittedAuditArtifactMaterializer<TContext>>()
            .Should()
            .NotBeNull();
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(System.Type? type) =>
        type?.IsGenericType == true &&
        type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
        type.GenericTypeArguments.Length == 2 &&
        type.GenericTypeArguments[1] == typeof(TMaterializer);
}
