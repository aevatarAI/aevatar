using Aevatar.AI.Abstractions;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Projection.Audit;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileCommittedAuditTranslatorTests
{
    private const string PromptSentinel = "prompt-secret-sentinel";
    private const string SkillBodySentinel = "skill-body-secret-sentinel";
    private const string CredentialSentinel = "credential-secret-sentinel";
    private const string RawResponseSentinel = "raw-response-secret-sentinel";

    [Fact]
    public void AddGAgentServiceProjection_ShouldWireAgentProfileAuditMaterializersAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();
        using var provider = services.BuildServiceProvider();

        AssertCommittedAuditMaterializerRegistered<AgentProfileCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<AgentProfileCurrentStateProjectionContext>(services, provider);
        provider.GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should().Contain([
                typeof(AgentProfileStateChangedAuditTranslator),
                typeof(AgentProfileNamespaceStateChangedAuditTranslator),
            ]);
    }

    [Theory]
    [InlineData("initialized", "agent_profile.created")]
    [InlineData("draft-updated", "agent_profile.draft.updated")]
    [InlineData("published", "agent_profile.published")]
    public void ProfileStateTranslator_ShouldEmitOnlySafeFacts(string changeKind, string operationName)
    {
        var state = ProfileState();
        var record = Translate(
            new AgentProfileStateChangedAuditTranslator(),
            new AgentProfileStateChangedEvent
            {
                State = state,
                ChangeKind = changeKind,
            });

        record.OperationName.Should().Be(operationName);
        record.Target.Kind.Should().Be("agent_profile");
        record.Target.Id.Should().Be("prof-alpha");
        record.ScopeId.Should().Be("scope-gamma");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Correlation.CommandId.Should().Be("cmd-profile");
        record.Correlation.CorrelationId.Should().Be("corr-profile");
        record.Annotations.Should().Contain("owner_kind", "scope");
        record.Annotations.Should().Contain("profile_slug", "research-assistant");
        record.Annotations.Should().Contain("draft_revision", "3");
        record.Annotations.Should().Contain("published_revision", "2");
        AssertSensitiveBodiesOmitted(record);
    }

    [Fact]
    public void NamespaceTranslator_ShouldAuditScopeDefaultBindingWithoutProfileBodies()
    {
        var state = NamespaceState(systemRollout: false);

        var record = Translate(
            new AgentProfileNamespaceStateChangedAuditTranslator(),
            new AgentProfileNamespaceStateChangedEvent
            {
                State = state,
                ChangeKind = "default-binding-set",
            });

        record.OperationName.Should().Be("agent_profile.default_binding.set");
        record.Target.Kind.Should().Be("agent_profile_default_binding");
        record.Target.Id.Should().Be("prof-alpha");
        record.ScopeId.Should().Be("scope-gamma");
        record.Annotations.Should().Contain("agent_kind", "nyxid-chat");
        record.Annotations.Should().Contain("admission_kind", "scope");
        record.Annotations.Should().Contain("target_published_revision", "2");
        AssertSensitiveBodiesOmitted(record);
    }

    [Fact]
    public void NamespaceTranslator_ShouldAuditSystemRolloutUsingTypedAdmissionFacts()
    {
        var state = NamespaceState(systemRollout: true);

        var record = Translate(
            new AgentProfileNamespaceStateChangedAuditTranslator(),
            new AgentProfileNamespaceStateChangedEvent
            {
                State = state,
                ChangeKind = "default-binding-set",
            });

        record.OperationName.Should().Be("agent_profile.system_rollout.updated");
        record.Target.Kind.Should().Be("agent_profile_default_binding");
        record.Target.Id.Should().Be("prof-alpha");
        record.ScopeId.Should().BeEmpty();
        record.Annotations.Should().Contain("owner_kind", "system");
        record.Annotations.Should().Contain("admission_kind", "system");
        record.Annotations.Should().Contain("enabled", "true");
        record.Annotations.Should().Contain("cohort_basis_points", "2500");
        record.Annotations.Should().Contain("previous_reviewed_profile_id", "prof-previous");
        record.Annotations.Should().Contain("previous_reviewed_published_revision", "1");
        AssertSensitiveBodiesOmitted(record);
    }

    [Fact]
    public void AgentProfileTranslators_ShouldIgnoreUnrecognizedChangeKindsAndWrongPayloads()
    {
        var profile = new AgentProfileStateChangedAuditTranslator();
        var unknown = profile.Translate(Context(), Any.Pack(new AgentProfileStateChangedEvent
        {
            State = ProfileState(),
            ChangeKind = "unknown",
        }));
        var wrong = profile.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        unknown.Should().BeEmpty();
        wrong.Should().BeEmpty();
    }

    private static AgentProfileState ProfileState()
    {
        var identity = Identity(AgentProfileOwners.ForScope("scope-gamma"));
        var draft = new AgentProfileDraft
        {
            DisplayName = CredentialSentinel,
            Purpose = RawResponseSentinel,
            Instructions = PromptSentinel,
            RuntimeProfile = new AgentProfileSnapshot
            {
                Instructions = PromptSentinel,
                Members =
                {
                    new AgentProfileSkillMember
                    {
                        IntentId = "research",
                        RoutingDescription = SkillBodySentinel,
                        ReviewedPublisherId = CredentialSentinel,
                    },
                },
            },
        };
        return new AgentProfileState
        {
            Identity = identity,
            Draft = draft,
            DraftRevision = 3,
            DraftSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 32).ToArray()),
            Published = new AgentProfilePublishedSnapshot
            {
                Identity = identity.Clone(),
                DisplayName = CredentialSentinel,
                Purpose = RawResponseSentinel,
                Instructions = PromptSentinel,
                RuntimeProfile = draft.RuntimeProfile.Clone(),
                DraftRevision = 3,
                PublishedRevision = 2,
                SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x44, 32).ToArray()),
            },
            PublishedRevision = 2,
            LastMutation = Mutation("PROFILE_PUBLISHED", 3, 2),
        };
    }

    private static AgentProfileNamespaceState NamespaceState(bool systemRollout)
    {
        var owner = systemRollout
            ? AgentProfileOwners.ForSystem()
            : AgentProfileOwners.ForScope("scope-gamma");
        var state = new AgentProfileNamespaceState
        {
            Owner = owner.Clone(),
            LastMutation = Mutation("DEFAULT_BINDING_SET", 0, 0),
        };
        var binding = new AgentProfileDefaultBinding
        {
            AgentKind = "nyxid-chat",
            Target = new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = "prof-alpha",
                PublishedRevision = 2,
                SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x44, 32).ToArray()),
            },
        };
        if (systemRollout)
        {
            binding.System = new AgentProfileSystemBindingAdmission
            {
                Enabled = true,
                CohortBasisPoints = 2_500,
                PreviousReviewedTarget = new AgentProfileBindingTarget
                {
                    Owner = owner.Clone(),
                    ProfileId = "prof-previous",
                    PublishedRevision = 1,
                    SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 32).ToArray()),
                },
            };
        }
        else
        {
            binding.Scope = new AgentProfileScopeBindingAdmission();
        }
        state.DefaultBindings.Add(binding);
        state.Profiles.Add(new AgentProfileCatalogEntry
        {
            ProfileId = "prof-sensitive",
            DisplayName = PromptSentinel,
            Purpose = RawResponseSentinel,
        });
        return state;
    }

    private static AgentProfileIdentity Identity(AgentProfileOwner owner) => new()
    {
        ProfileId = "prof-alpha",
        Owner = owner.Clone(),
        ProfileSlug = "research-assistant",
    };

    private static AgentProfileMutationOutcome Mutation(
        string code,
        long draftRevision,
        long publishedRevision) => new()
    {
        Operation = new AgentProfileOperationFact
        {
            OperationId = "op-profile",
            CommandId = "cmd-profile",
            CorrelationId = "corr-profile",
            InputSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x55, 32).ToArray()),
            AuditSubject = CredentialSentinel,
        },
        Status = AgentProfileMutationStatus.Succeeded,
        Code = code,
        AuthorityStateVersion = 17,
        DraftRevision = draftRevision,
        PublishedRevision = publishedRevision,
    };

    private static AuditRecord Translate(IAuditCommittedEventTranslator translator, IMessage evt) =>
        translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

    private static void AssertSensitiveBodiesOmitted(AuditRecord record)
    {
        var serialized = record.ToString();
        serialized.Should().NotContain(PromptSentinel);
        serialized.Should().NotContain(SkillBodySentinel);
        serialized.Should().NotContain(CredentialSentinel);
        serialized.Should().NotContain(RawResponseSentinel);
        record.Redaction.OmittedFields.Should().Contain("source_event.payload");
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation { CorrelationId = "corr-envelope" },
            },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "actor-profile",
                EventId = "state-event-profile-17",
                Version = 17,
            },
            "actor-profile",
            "type.googleapis.com/aevatar.gagentservice.AgentProfileStateChangedEvent",
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            "command-envelope",
            "request-envelope",
            "corr-envelope");

    private static void AssertCommittedAuditMaterializerRegistered<TContext>(
        IServiceCollection services,
        IServiceProvider provider)
        where TContext : class, IProjectionMaterializationContext
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<TContext>) &&
            descriptor.ImplementationType != null &&
            descriptor.ImplementationType.IsGenericType &&
            descriptor.ImplementationType.Name.StartsWith(
                "ObservedProjectionArtifactMaterializer`",
                StringComparison.Ordinal) &&
            descriptor.ImplementationType.GenericTypeArguments[1] ==
            typeof(CommittedAuditArtifactMaterializer<TContext>));
        provider.GetRequiredService<CommittedAuditArtifactMaterializer<TContext>>()
            .Should().NotBeNull();
    }
}
