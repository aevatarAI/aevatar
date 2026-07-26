using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Stores;
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
    private const string RoutingDescription = "routing description must stay private";
    private const string RoutingAlias = "routing alias must stay private";
    private const string TaskToolName = "task tool name must stay private";
    private const string TaskToolSetRef = "task tool-set ref must stay private";
    private const string RecoveryToolName = "recovery tool name must stay private";
    private const string RecoveryToolSetRef = "recovery tool-set ref must stay private";
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
            Transition = Transition(
                before: null,
                after: RevisionDigestFacts(1, Digest(0x11), 0, ByteString.Empty)),
        };

        var translator = new AgentProfileInitializedAuditTranslator();
        var record = Translate(translator, evt);

        translator.EventTypeUrl.Should().Be(
            AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileInitializedEvent.Descriptor));
        AssertCommon(record, "agent_profile.created", "applied");
        record.Annotations.Should().Contain("draft_revision", "1");
        record.Annotations.Should().Contain("draft_sha256", Hex(Digest(0x11)));
        record.Annotations.Should().Contain("new_draft_revision", "1");
        record.Annotations.Should().Contain("new_draft_sha256", Hex(Digest(0x11)));
        record.Annotations.Should().Contain("new_published_revision", "0");
        record.Annotations.Should().Contain("new_published_snapshot_sha256", string.Empty);
        AssertRecoveryPolicyAnnotations(record, evt.InitialContent.RecoveryToolPolicy);
        record.Annotations.Keys.Should().NotContain(key => key.StartsWith("old_", StringComparison.Ordinal));
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
                publishedSnapshotSha256: Digest(0x19),
                oldDraftRevision: 1,
                oldDraftSha256: Digest(0x11),
                oldPublishedRevision: 1,
                oldPublishedSnapshotSha256: Digest(0x19)),
        };

        var record = Translate(new AgentProfileDraftUpdatedAuditTranslator(), evt);

        AssertCommon(record, "agent_profile.draft.updated", "applied");
        record.Annotations.Should().Contain("draft_revision", "2");
        record.Annotations.Should().Contain("draft_sha256", Hex(Digest(0x22)));
        record.Annotations.Should().Contain("published_revision", "1");
        record.Annotations.Should().Contain("published_snapshot_sha256", Hex(Digest(0x19)));
        AssertRecoveryPolicyAnnotations(record, evt.Content.RecoveryToolPolicy);
        AssertTransitionAnnotations(
            record,
            oldDraftRevision: 1,
            oldDraftSha256: Digest(0x11),
            oldPublishedRevision: 1,
            oldPublishedSnapshotSha256: Digest(0x19),
            newDraftRevision: 2,
            newDraftSha256: Digest(0x22),
            newPublishedRevision: 1,
            newPublishedSnapshotSha256: Digest(0x19));
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
                draftSha256: Digest(0x33),
                publishedRevision: 1,
                publishedSnapshotSha256: Digest(0x51),
                oldDraftRevision: 2,
                oldDraftSha256: Digest(0x22),
                oldPublishedRevision: 1,
                oldPublishedSnapshotSha256: Digest(0x51)),
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
                draftSha256: Digest(0x44),
                publishedRevision: 1,
                publishedSnapshotSha256: Digest(0x51),
                oldDraftRevision: 3,
                oldDraftSha256: Digest(0x33),
                oldPublishedRevision: 1,
                oldPublishedSnapshotSha256: Digest(0x51)),
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
        upsertRecord.Annotations.Should().Contain("routing_intent_id", "calendar-requests");
        upsertRecord.Annotations.Should().Contain("routing_side_effect_class", "service_call");
        upsertRecord.Annotations.Should().Contain(
            "routing_policy_sha256",
            RoutingPolicyDigest(upserted.Binding));
        AssertRecoveryPolicyAnnotations(upsertRecord, upserted.Content.RecoveryToolPolicy);
        AssertTransitionAnnotations(
            upsertRecord,
            oldDraftRevision: 2,
            oldDraftSha256: Digest(0x22),
            oldPublishedRevision: 1,
            oldPublishedSnapshotSha256: Digest(0x51),
            newDraftRevision: 3,
            newDraftSha256: Digest(0x33),
            newPublishedRevision: 1,
            newPublishedSnapshotSha256: Digest(0x51));
        AssertCommon(removedRecord, "agent_profile.skill_binding.removed", "applied");
        removedRecord.Annotations.Should().Contain("binding_id", "binding-alpha");
        removedRecord.Annotations.Should().Contain("draft_revision", "4");
        AssertRecoveryPolicyAnnotations(removedRecord, removed.Content.RecoveryToolPolicy);
        AssertTransitionAnnotations(
            removedRecord,
            oldDraftRevision: 3,
            oldDraftSha256: Digest(0x33),
            oldPublishedRevision: 1,
            oldPublishedSnapshotSha256: Digest(0x51),
            newDraftRevision: 4,
            newDraftSha256: Digest(0x44),
            newPublishedRevision: 1,
            newPublishedSnapshotSha256: Digest(0x51));
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
                publishedSnapshotSha256: published.SnapshotSha256,
                oldDraftRevision: 4,
                oldDraftSha256: published.SourceDraftSha256,
                oldPublishedRevision: 1,
                oldPublishedSnapshotSha256: Digest(0x54)),
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
        AssertRecoveryPolicyAnnotations(appliedRecord, published.RecoveryToolPolicy);
        AssertTransitionAnnotations(
            appliedRecord,
            oldDraftRevision: 4,
            oldDraftSha256: published.SourceDraftSha256,
            oldPublishedRevision: 1,
            oldPublishedSnapshotSha256: Digest(0x54),
            newDraftRevision: 4,
            newDraftSha256: published.SourceDraftSha256,
            newPublishedRevision: 2,
            newPublishedSnapshotSha256: published.SnapshotSha256);
        AssertCommon(noChangeRecord, "agent_profile.publish.no_change", "no_change");
        noChangeRecord.Annotations.Should().Contain("published_revision", "2");
        noChangeRecord.Annotations.Should().Contain(
            "published_snapshot_sha256",
            Hex(published.SnapshotSha256));
        AssertTransitionAnnotations(
            noChangeRecord,
            oldDraftRevision: 4,
            oldDraftSha256: published.SourceDraftSha256,
            oldPublishedRevision: 2,
            oldPublishedSnapshotSha256: published.SnapshotSha256,
            newDraftRevision: 4,
            newDraftSha256: published.SourceDraftSha256,
            newPublishedRevision: 2,
            newPublishedSnapshotSha256: published.SnapshotSha256);
        AssertSensitiveValuesOmitted(appliedRecord);
        AssertSensitiveValuesOmitted(noChangeRecord);
    }

    [Fact]
    public void MutationRejectedTranslator_ShouldPreserveKnownStableFailureCodeAndOmitRawDiagnosticText()
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
                    Code = "DRAFT_VERSION_CONFLICT",
                    Message = $"{RawRemoteError}: {Bearer}",
                    Path = "expected_authority_state_version",
                }),
        };

        var record = Translate(new AgentProfileMutationRejectedAuditTranslator(), evt);

        AssertCommon(record, "agent_profile.mutation.rejected", "rejected");
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Code.Should().Be("DRAFT_VERSION_CONFLICT");
        record.Failure.SanitizedMessage.Should().Be("DRAFT_VERSION_CONFLICT");
        record.Annotations.Should().Contain("failure_code", "DRAFT_VERSION_CONFLICT");
        AssertTransitionAnnotations(
            record,
            oldDraftRevision: 4,
            oldDraftSha256: Digest(0x44),
            oldPublishedRevision: 2,
            oldPublishedSnapshotSha256: Digest(0x55),
            newDraftRevision: 4,
            newDraftSha256: Digest(0x44),
            newPublishedRevision: 2,
            newPublishedSnapshotSha256: Digest(0x55));
        AssertSensitiveValuesOmitted(record);
    }

    [Theory]
    [InlineData("provisioning", "UNKNOWN_REMOTE_FAILURE", "PROFILE_PROVISIONING_FAILED")]
    [InlineData("initialization", " malformed code ", "PROFILE_INITIALIZATION_REJECTED")]
    [InlineData("mutation", Bearer, "PROFILE_MUTATION_REJECTED")]
    public void FailureTranslators_WhenDiagnosticCodeIsNotAllowlisted_ShouldUseEventFallback(
        string eventKind,
        string diagnosticCode,
        string expectedCode)
    {
        var diagnostic = new AgentProfileSafeDiagnostic
        {
            Code = diagnosticCode,
            Message = RawRemoteError,
        };

        var record = eventKind switch
        {
            "provisioning" => Translate(
                new AgentProfileProvisioningFailedAuditTranslator(),
                new AgentProfileProvisioningFailedEvent
                {
                    Operation = Operation("provisioning-failed"),
                    Identity = Identity(),
                    Diagnostic = diagnostic,
                    FailureKind = AgentProfileProvisioningFailureKind.CreateValidation,
                }),
            "initialization" => Translate(
                new AgentProfileInitializationRejectedAuditTranslator(),
                new AgentProfileInitializationRejectedEvent
                {
                    Operation = Operation("initialization-rejected"),
                    Identity = Identity(),
                    Diagnostic = diagnostic,
                }),
            "mutation" => Translate(
                new AgentProfileMutationRejectedAuditTranslator(),
                new AgentProfileMutationRejectedEvent
                {
                    Operation = Operation("mutation-rejected"),
                    Identity = Identity(),
                    Outcome = Outcome(
                        "mutation-rejected",
                        AgentProfileMutationStatus.Rejected,
                        draftRevision: 4,
                        draftSha256: Digest(0x44),
                        diagnostic: diagnostic),
                }),
            _ => throw new InvalidOperationException($"Unsupported test event kind '{eventKind}'."),
        };

        record.Failure.Code.Should().Be(expectedCode);
        record.Failure.SanitizedMessage.Should().Be(expectedCode);
        record.Annotations.Should().Contain("failure_code", expectedCode);
        record.ToString().Should().NotContain(diagnosticCode);
    }

    [Fact]
    public async Task SystemProfileCommittedAudit_ShouldAppendUsingPlatformPartitionWithoutChangingDomainScope()
    {
        var identity = SystemIdentity();
        var evt = new AgentProfileInitializedEvent
        {
            Operation = Operation("system-create"),
            Identity = identity,
            InitialContent = SensitiveContent(),
            DraftRevision = 1,
            DraftSha256 = Digest(0x11),
            NamespaceActorId = AgentProfileActorIds.Namespace,
            ProfileActorId = AgentProfileActorIds.Profile(identity.ProfileId),
        };

        var document = await MaterializeAsync(
            new AgentProfileInitializedAuditTranslator(),
            evt,
            "system-profile-event");

        identity.OwningScopeId.Should().BeEmpty();
        evt.Identity.OwningScopeId.Should().BeEmpty();
        document.Should().NotBeNull();
        document!.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
        document.Record.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
        document.Record.Provenance.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
    }

    [Fact]
    public async Task InvalidProfileIdentityCommittedFailure_ShouldAppendToPlatformAuditQuarantine()
    {
        const string hostileProfileId = "hostile-profile-id";
        const string hostileOwnerHandle = "hostile-owner";
        const string hostileProfileSlug = "hostile-slug";
        const string hostileScopeId = "hostile-scope";
        var evt = new AgentProfileProvisioningFailedEvent
        {
            Operation = Operation("invalid-create"),
            Identity = new AgentProfileIdentity
            {
                ProfileId = hostileProfileId,
                Owner = new AgentProfileOwnerIdentity(),
                OwningScopeId = hostileScopeId,
                Reference = new AgentProfileReference
                {
                    OwnerHandle = hostileOwnerHandle,
                    ProfileSlug = hostileProfileSlug,
                },
            },
            ProfileActorId = AgentProfileActorIds.Profile(hostileProfileId),
            Diagnostic = new AgentProfileSafeDiagnostic
            {
                Code = "MISSING_PROFILE_IDENTITY",
                Message = "A canonical Profile identity is required.",
                Path = "identity",
            },
            FailureKind = AgentProfileProvisioningFailureKind.CreateValidation,
        };

        var document = await MaterializeAsync(
            new AgentProfileNamespaceCurrentStateProjectionContext
            {
                RootActorId = AgentProfileActorIds.Namespace,
                ProjectionKind = "agent_profile_namespace",
            },
            new AgentProfileProvisioningFailedAuditTranslator(),
            evt,
            "invalid-profile-quarantine-event",
            AgentProfileActorIds.Namespace);

        document.Should().NotBeNull();
        document!.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
        document.Record.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
        document.Record.Provenance.ScopeId.Should().Be(AuditContractSemantics.PlatformAuditScopeId);
        document.Record.Target.Id.Should().Be("invalid-profile-identity");
        document.Record.Failure.Code.Should().Be("MISSING_PROFILE_IDENTITY");
        document.Record.Annotations.Should().NotContainKey("profile_id");
        document.Record.Annotations.Should().NotContainKey("owner_kind");
        document.Record.Annotations.Should().NotContainKey("owner_handle");
        document.Record.Annotations.Should().NotContainKey("profile_slug");
        foreach (var hostileValue in new[]
                 {
                     hostileProfileId,
                     hostileOwnerHandle,
                     hostileProfileSlug,
                     hostileScopeId,
                 })
        {
            document.Record.ToString().Should().NotContain(hostileValue);
        }
    }

    [Fact]
    public async Task ReservedUserScopeCreateFailure_ShouldEnterOnlyPlatformAuditQuarantine()
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingProfileEventPublisher();
        var agent = GAgentServiceTestKit
            .CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
                store,
                AgentProfileActorIds.Namespace,
                static () => new AgentProfileNamespaceGAgent(
                    AcceptingIngressProofVerifier.Instance));
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var command = CreateCommand();
        command.Identity.OwningScopeId = PlatformScopeSemantics.ReservedPlatformScopeId;

        await agent.HandleCreateAsync(command);

        agent.State.Profiles.Should().BeEmpty();
        publisher.Sends.Should().BeEmpty();
        var committed = (await store.GetEventsAsync(agent.Id)).Should().ContainSingle().Which;
        committed.EventData.Is(AgentProfileProvisioningFailedEvent.Descriptor).Should().BeTrue();
        var failure = committed.EventData.Unpack<AgentProfileProvisioningFailedEvent>();
        failure.Diagnostic.Code.Should().Be("RESERVED_OWNING_SCOPE_ID");
        failure.Identity.Should().BeEquivalentTo(new AgentProfileIdentity());

        var document = await MaterializeAsync(
            new AgentProfileNamespaceCurrentStateProjectionContext
            {
                RootActorId = AgentProfileActorIds.Namespace,
                ProjectionKind = "agent_profile_namespace",
            },
            new AgentProfileProvisioningFailedAuditTranslator(),
            failure,
            committed.EventId,
            AgentProfileActorIds.Namespace);

        document.Should().NotBeNull();
        document!.ScopeId.Should().Be(PlatformScopeSemantics.ReservedPlatformScopeId);
        document.Record.Target.Id.Should().Be("invalid-profile-identity");
        document.Record.Failure.Code.Should().Be("RESERVED_OWNING_SCOPE_ID");
        document.Record.Failure.Category.Should().Be(AuditFailureCategory.Validation);
        document.Record.Annotations.Should().NotContainKey("profile_id");
        document.Record.Annotations.Should().NotContainKey("owner_kind");
        document.Record.Annotations.Should().NotContainKey("owner_handle");
        document.Record.Annotations.Should().NotContainKey("profile_slug");
    }

    [Fact]
    public async Task HostileDiagnosticCode_ShouldPersistOnlyEventFallbackThroughRealAuditPipeline()
    {
        var evt = new AgentProfileMutationRejectedEvent
        {
            Operation = Operation("hostile-code"),
            Identity = Identity(),
            Outcome = Outcome(
                "hostile-code",
                AgentProfileMutationStatus.Rejected,
                draftRevision: 4,
                draftSha256: Digest(0x44),
                diagnostic: new AgentProfileSafeDiagnostic
                {
                    Code = Bearer,
                    Message = RawRemoteError,
                }),
        };

        var document = await MaterializeAsync(
            new AgentProfileMutationRejectedAuditTranslator(),
            evt,
            "hostile-code-event");

        document.Should().NotBeNull();
        document!.Record.Failure.Code.Should().Be("PROFILE_MUTATION_REJECTED");
        document.Record.Failure.SanitizedMessage.Should().Be("PROFILE_MUTATION_REJECTED");
        document.Record.Annotations.Should().Contain("failure_code", "PROFILE_MUTATION_REJECTED");
        document.Record.ToString().Should().NotContain(Bearer);
        document.Record.ToString().Should().NotContain(RawRemoteError);
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
                static () => new AgentProfileNamespaceGAgent(
                    AcceptingIngressProofVerifier.Instance));
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

    private static void AssertTransitionAnnotations(
        AuditRecord record,
        long oldDraftRevision,
        ByteString oldDraftSha256,
        long oldPublishedRevision,
        ByteString oldPublishedSnapshotSha256,
        long newDraftRevision,
        ByteString newDraftSha256,
        long newPublishedRevision,
        ByteString newPublishedSnapshotSha256)
    {
        record.Annotations.Should().Contain("old_draft_revision", oldDraftRevision.ToString());
        record.Annotations.Should().Contain("old_draft_sha256", Hex(oldDraftSha256));
        record.Annotations.Should().Contain("old_published_revision", oldPublishedRevision.ToString());
        record.Annotations.Should().Contain(
            "old_published_snapshot_sha256",
            Hex(oldPublishedSnapshotSha256));
        record.Annotations.Should().Contain("new_draft_revision", newDraftRevision.ToString());
        record.Annotations.Should().Contain("new_draft_sha256", Hex(newDraftSha256));
        record.Annotations.Should().Contain("new_published_revision", newPublishedRevision.ToString());
        record.Annotations.Should().Contain(
            "new_published_snapshot_sha256",
            Hex(newPublishedSnapshotSha256));
    }

    private static void AssertSensitiveValuesOmitted(AuditRecord record)
    {
        var serialized = record.ToString();
        foreach (var forbidden in new[]
                 {
                     DraftInstructions,
                     RoutingDescription,
                     RoutingAlias,
                     TaskToolName,
                     TaskToolSetRef,
                     RecoveryToolName,
                     RecoveryToolSetRef,
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

    private static AgentProfileIdentity SystemIdentity() =>
        new()
        {
            ProfileId = "prof-system-studio",
            Owner = new AgentProfileOwnerIdentity
            {
                System = new AgentProfileSystemOwnerIdentity
                {
                    PlatformId = AgentProfilePolicies.AevatarPlatformId,
                },
            },
            OwningScopeId = string.Empty,
            Reference = new AgentProfileReference
            {
                OwnerHandle = AgentProfilePolicies.SystemOwnerHandle,
                ProfileSlug = "studio",
            },
        };

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
        AgentProfileSafeDiagnostic? diagnostic = null,
        long? oldDraftRevision = null,
        ByteString? oldDraftSha256 = null,
        long? oldPublishedRevision = null,
        ByteString? oldPublishedSnapshotSha256 = null)
    {
        var after = RevisionDigestFacts(
            draftRevision,
            draftSha256,
            publishedRevision,
            publishedSnapshotSha256 ?? ByteString.Empty);
        return new AgentProfileMutationOutcome
        {
            Operation = Operation(suffix),
            Status = status,
            Diagnostic = diagnostic,
            DraftRevision = after.DraftRevision,
            DraftSha256 = after.DraftSha256,
            PublishedRevision = after.PublishedRevision,
            PublishedSnapshotSha256 = after.PublishedSnapshotSha256,
            Transition = Transition(
                RevisionDigestFacts(
                    oldDraftRevision ?? after.DraftRevision,
                    oldDraftSha256 ?? after.DraftSha256,
                    oldPublishedRevision ?? after.PublishedRevision,
                    oldPublishedSnapshotSha256 ?? after.PublishedSnapshotSha256),
                after),
        };
    }

    private static AgentProfileCommittedStateTransition Transition(
        AgentProfileRevisionDigestFacts? before,
        AgentProfileRevisionDigestFacts after)
    {
        var transition = new AgentProfileCommittedStateTransition { After = after };
        if (before is not null)
            transition.Before = before;
        return transition;
    }

    private static AgentProfileRevisionDigestFacts RevisionDigestFacts(
        long draftRevision,
        ByteString draftSha256,
        long publishedRevision,
        ByteString publishedSnapshotSha256) =>
        new()
        {
            DraftRevision = draftRevision,
            DraftSha256 = draftSha256,
            PublishedRevision = publishedRevision,
            PublishedSnapshotSha256 = publishedSnapshotSha256,
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
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
                ToolNames = { RecoveryToolName },
                ToolSetRefs = { RecoveryToolSetRef },
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
            RoutingPolicy = new AgentProfileSkillRoutingPolicy
            {
                IntentId = "calendar-requests",
                RoutingDescription = RoutingDescription,
                ExplicitTriggerAliases = { RoutingAlias },
                TaskToolPolicy = new AgentProfileToolPolicy
                {
                    Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
                    ToolNames = { TaskToolName },
                    ToolSetRefs = { TaskToolSetRef },
                },
                SideEffectClass = AgentProfileSkillSideEffectClass.ServiceCall,
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
            RecoveryToolPolicy = SensitiveContent().RecoveryToolPolicy.Clone(),
            PublishedRevision = 2,
            SourceDraftSha256 = Digest(0x44),
            SnapshotSha256 = Digest(0x55),
        };
        snapshot.SkillBindings.Add(new SealedAgentProfileSkillBinding
        {
            BindingId = "binding-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            RoutingPolicy = Binding().RoutingPolicy.Clone(),
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

    private static void AssertRecoveryPolicyAnnotations(
        AuditRecord record,
        AgentProfileToolPolicy policy)
    {
        record.Annotations.Should().Contain(
            "recovery_tool_policy_mode",
            "explicit_allowlist");
        record.Annotations.Should().Contain(
            "recovery_tool_policy_sha256",
            Hex(AgentProfileDeterminism.Sha256(
                AgentProfileDeterminism.NormalizeToolPolicy(policy))));
    }

    private static string RoutingPolicyDigest(AgentProfileSkillBinding binding)
    {
        var normalized = AgentProfileDeterminism.NormalizeSkillBinding(binding);
        return Hex(AgentProfileDeterminism.Sha256(normalized.RoutingPolicy));
    }

    private static async Task<AuditTrailDocument?> MaterializeAsync<TEvent>(
        IAuditCommittedEventTranslator translator,
        TEvent evt,
        string eventId)
        where TEvent : class, IMessage<TEvent>
    {
        var originActorId = evt switch
        {
            AgentProfileInitializedEvent initialized =>
                AgentProfileActorIds.Profile(initialized.Identity?.ProfileId ?? "missing"),
            AgentProfileMutationRejectedEvent rejected =>
                AgentProfileActorIds.Profile(rejected.Identity?.ProfileId ?? "missing"),
            _ => AgentProfileActorIds.Profile("unknown"),
        };
        return await MaterializeAsync(
            new AgentProfileOwnerCurrentStateProjectionContext
            {
                RootActorId = originActorId,
                ProjectionKind = "agent_profile_owner",
            },
            translator,
            evt,
            eventId,
            originActorId);
    }

    private static async Task<AuditTrailDocument?> MaterializeAsync<TContext, TEvent>(
        TContext context,
        IAuditCommittedEventTranslator translator,
        TEvent evt,
        string eventId,
        string originActorId)
        where TContext : class, IProjectionMaterializationContext
        where TEvent : class, IMessage<TEvent>
    {
        var store = new InMemoryAuditTrailStore();
        var appender = new ProjectionAuditTrailAppender([store]);
        var materializer = new CommittedAuditArtifactMaterializer<TContext>(
            new AuditCommittedEventTranslatorRegistry([translator]),
            appender,
            new FixedProjectionClock(ObservedAt.AddMinutes(1)));
        var envelope = new EventEnvelope
        {
            Id = $"envelope-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = $"correlation-{eventId}",
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = originActorId,
                    EventId = eventId,
                    Version = 17,
                    EventData = Any.Pack(evt),
                },
            }),
        };

        await materializer.ProjectAsync(context, envelope);

        return await store.GetAsync($"committed:{eventId}:{translator switch
        {
            AgentProfileProvisioningFailedAuditTranslator => "agent_profile.provisioning.failed",
            AgentProfileInitializedAuditTranslator => "agent_profile.created",
            AgentProfileMutationRejectedAuditTranslator => "agent_profile.mutation.rejected",
            _ => throw new InvalidOperationException("Unsupported materializer test translator."),
        }}");
    }

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

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class AcceptingIngressProofVerifier : IAgentProfileIngressProofVerifier
    {
        public static AcceptingIngressProofVerifier Instance { get; } = new();

        public bool Verify(string targetActorId, IMessage command) => true;
    }
}
