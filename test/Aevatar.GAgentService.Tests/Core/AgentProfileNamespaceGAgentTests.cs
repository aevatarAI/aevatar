using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class AgentProfileNamespaceGAgentTests
{
    [Fact]
    public async Task CreateAndInitialize_ShouldActivateOneOpaqueProfileEntry()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var actor = CreateActor(owner);
        var operation = Operation("op-create", "create");

        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = operation.Clone(),
        });
        await HandleInitializedAsync(actor, new AgentProfileInitialized
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            Operation = operation.Clone(),
            SourceProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = operation.OperationId,
        });

        actor.State.Owner.Should().Be(owner);
        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileId == "prof-alpha" && x.Status == AgentProfileProvisioningStatus.Active);
    }

    [Fact]
    public async Task DefaultBinding_ShouldRequireMatchingPublishedTargetForOwnedProfile()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var actor = CreateActor(owner);
        var create = Operation("op-create", "create");
        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = create.Clone(),
        });
        await HandleInitializedAsync(actor, new AgentProfileInitialized
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            Operation = create.Clone(),
            SourceProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = create.OperationId,
        });

        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = Target(owner, "prof-alpha", 1, 0x22),
            Scope = new AgentProfileScopeBindingAdmission(),
            ExpectedAuthorityStateVersion = 2,
            Operation = Operation("op-bind-before-publish", "bind-before-publish"),
        });

        actor.State.DefaultBindings.Should().BeEmpty();
        actor.State.LastMutation.Code.Should().Be("PROFILE_NOT_PUBLISHED");

        var published = new ObserveAgentProfilePublishedCommand
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            PublishedRevision = 1,
            SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 32).ToArray()),
            DisplayName = "Research Assistant",
            Purpose = "Research public sources",
            SourceProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
            SourceAuthorityStateVersion = 2,
            SourceOperationId = "op-publish",
        };
        await actor.HandleEventAsync(Envelope(
            published,
            published.SourceProfileActorId,
            actor.Id));
        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = Target(owner, "prof-alpha", 1, 0x22),
            Scope = new AgentProfileScopeBindingAdmission(),
            ExpectedAuthorityStateVersion = 4,
            Operation = Operation("op-bind", "bind"),
        });

        actor.State.DefaultBindings.Should().ContainSingle(x =>
            x.AgentKind == AgentProfilePolicies.NyxIdChatAgentKind &&
            x.Target.ProfileId == "prof-alpha" &&
            x.AdmissionCase == AgentProfileDefaultBinding.AdmissionOneofCase.Scope);
    }

    [Fact]
    public async Task ScopeDefaultBinding_ShouldAcceptVerifiedSystemTargetWithoutLocalCatalogEntry()
    {
        var scopeOwner = AgentProfileOwners.ForScope("scope-gamma");
        var actor = CreateActor(scopeOwner);
        var systemOwner = AgentProfileOwners.ForSystem();

        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = scopeOwner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = Target(systemOwner, "prof-system", 7, 0x77),
            Scope = new AgentProfileScopeBindingAdmission(),
            ExpectedAuthorityStateVersion = 0,
            Operation = Operation("op-bind-system", "bind-system"),
        });

        actor.State.DefaultBindings.Should().ContainSingle(x =>
            x.Target.Owner.Equals(systemOwner) &&
            x.Target.ProfileId == "prof-system" &&
            x.Target.PublishedRevision == 7 &&
            x.Target.SnapshotSha256.Equals(ByteString.CopyFrom(Enumerable.Repeat((byte)0x77, 32).ToArray())) &&
            x.AdmissionCase == AgentProfileDefaultBinding.AdmissionOneofCase.Scope);
    }

    [Fact]
    public async Task ScopeDefaultBinding_ShouldRejectSystemRolloutAdmission()
    {
        var scopeOwner = AgentProfileOwners.ForScope("scope-gamma");
        var actor = CreateActor(scopeOwner);

        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = scopeOwner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = Target(AgentProfileOwners.ForSystem(), "prof-system", 7, 0x77),
            System = new AgentProfileSystemBindingAdmission
            {
                Enabled = true,
                CohortBasisPoints = AgentProfilePolicies.ExpandedCohortBasisPoints,
            },
            ExpectedAuthorityStateVersion = 0,
            Operation = Operation("op-bind-rollout", "bind-rollout"),
        });

        actor.State.DefaultBindings.Should().BeEmpty();
        actor.State.LastMutation.Code.Should().Be("BINDING_ADMISSION_INVALID");
    }

    [Fact]
    public async Task SystemRollout_ShouldKeepPreviousReviewedTargetAcrossFiveTwentyFiveFullAndRollback()
    {
        var owner = AgentProfileOwners.ForSystem();
        var actor = CreateActor(owner);
        await AddPublishedProfileAsync(actor, owner, "prof-a", "profile-a", 0x11);
        await AddPublishedProfileAsync(actor, owner, "prof-b", "profile-b", 0x22);
        var targetA = Target(owner, "prof-a", 1, 0x11);
        var targetB = Target(owner, "prof-b", 1, 0x22);

        await SetSystemBindingAsync(actor, owner, targetA, 6, 10_000, "baseline");
        await SetSystemBindingAsync(actor, owner, targetB, 7, 500, "canary");
        actor.State.DefaultBindings.Single().System.PreviousReviewedTarget.Should().BeEquivalentTo(targetA);

        await SetSystemBindingAsync(actor, owner, targetB, 8, 2_500, "expanded");
        actor.State.DefaultBindings.Single().System.PreviousReviewedTarget.Should().BeEquivalentTo(targetA);

        await SetSystemBindingAsync(actor, owner, targetB, 9, 10_000, "full");
        actor.State.DefaultBindings.Single().System.PreviousReviewedTarget.Should().BeEquivalentTo(targetA);

        await SetSystemBindingAsync(actor, owner, targetA, 10, 10_000, "rollback");
        var rolledBack = actor.State.DefaultBindings.Single();
        rolledBack.Target.Should().BeEquivalentTo(targetA);
        rolledBack.System.PreviousReviewedTarget.Should().BeEquivalentTo(targetB);
    }

    [Fact]
    public async Task Create_ShouldDeriveProfileAddressScheduleTimeoutAndRejectSemanticReplayDrift()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = CreateActor(owner, scheduler, publisher);
        var command = new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = Operation("op-create-semantic", "caller-digest"),
        };

        await actor.HandleCreateAsync(command);

        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileActorId == AgentProfileActorIds.Profile("prof-alpha") &&
            x.ProvisioningOperationId == command.Operation.OperationId &&
            x.ProvisioningAttempt == 1 &&
            x.ProvisioningTimeoutCallbackId.Length > 0);
        scheduler.TimeoutRequests.Should().ContainSingle();
        publisher.Sends.Should().ContainSingle(x =>
            x.TargetActorId == AgentProfileActorIds.Profile("prof-alpha") &&
            x.Event is InitializeAgentProfileCommand);

        var drifted = command.Clone();
        drifted.ProfileSlug = "changed-slug";
        var replay = () => actor.HandleCreateAsync(drifted);

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*payload drift*");
    }

    [Fact]
    public async Task ProvisioningFailure_ShouldReleaseSlugAndAllowSameOperationRetry()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = CreateActor(owner, scheduler, publisher);
        var command = new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = Operation("op-create-retry", "caller-digest"),
        };
        await actor.HandleCreateAsync(command);
        var failure = new AgentProfileInitializationFailed
        {
            Identity = Identity(owner, "prof-alpha", "research-assistant"),
            Operation = command.Operation.Clone(),
            SourceProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = command.Operation.OperationId,
            FailureCode = "INITIALIZATION_FAILED",
        };
        await actor.HandleEventAsync(Envelope(failure, failure.SourceProfileActorId, actor.Id));

        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileId == "prof-alpha" && x.Status == AgentProfileProvisioningStatus.Failed);

        await actor.HandleCreateAsync(command.Clone());

        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileId == "prof-alpha" &&
            x.Status == AgentProfileProvisioningStatus.Provisioning &&
            x.ProvisioningAttempt == 2);
        scheduler.TimeoutRequests.Should().HaveCount(2);

        failure.SourceAuthorityStateVersion = 2;
        await actor.HandleEventAsync(Envelope(failure, failure.SourceProfileActorId, actor.Id));
        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileId == "prof-alpha" && x.Status == AgentProfileProvisioningStatus.Failed);

        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-beta",
            ProfileSlug = "research-assistant",
            Operation = Operation("op-create-released-slug", "released-slug"),
        });
        actor.State.Profiles.Should().Contain(x =>
            x.ProfileId == "prof-beta" && x.Status == AgentProfileProvisioningStatus.Provisioning);
    }

    [Fact]
    public async Task ProvisioningTimeout_ShouldIgnoreStaleAttemptAndFailMatchingSelfCallback()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var actor = CreateActor(owner, scheduler, new RecordingEventPublisher());
        var command = new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = Operation("op-create-timeout", "timeout"),
        };
        await actor.HandleCreateAsync(command);
        var first = actor.State.Profiles.Single().Clone();
        await actor.HandleEventAsync(Envelope(new AgentProfileInitializationFailed
        {
            Identity = Identity(owner, "prof-alpha", "research-assistant"),
            Operation = command.Operation.Clone(),
            SourceProfileActorId = first.ProfileActorId,
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = command.Operation.OperationId,
            FailureCode = "INITIALIZATION_FAILED",
        }, first.ProfileActorId, actor.Id));
        await actor.HandleCreateAsync(command.Clone());
        var second = actor.State.Profiles.Single().Clone();

        await actor.HandleEventAsync(SelfTimeoutEnvelope(actor, first));
        actor.State.Profiles.Single().Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);

        await actor.HandleEventAsync(SelfTimeoutEnvelope(actor, second));
        actor.State.Profiles.Single().Status.Should().Be(AgentProfileProvisioningStatus.Failed);
        actor.State.LastMutation.Code.Should().Be("PROFILE_PROVISIONING_TIMED_OUT");
    }

    [Fact]
    public async Task InitializedContinuation_ShouldRejectForgedPublisher()
    {
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var actor = CreateActor(owner);
        var command = new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            Operation = Operation("op-create-source", "source"),
        };
        await actor.HandleCreateAsync(command);
        var initialized = new AgentProfileInitialized
        {
            Identity = Identity(owner, "prof-alpha", "research-assistant"),
            Operation = command.Operation.Clone(),
            SourceProfileActorId = AgentProfileActorIds.Profile("prof-alpha"),
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = command.Operation.OperationId,
        };

        var act = () => actor.HandleEventAsync(Envelope(initialized, "forged-profile-actor", actor.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*");
        actor.State.Profiles.Single().Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
    }

    private static AgentProfileNamespaceGAgent CreateActor(
        AgentProfileOwner owner,
        RecordingRuntimeCallbackScheduler? scheduler = null,
        RecordingEventPublisher? publisher = null)
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
            new InMemoryEventStore(),
            AgentProfileActorIds.Namespace(owner),
            static () => new AgentProfileNamespaceGAgent(),
            scheduler is null
                ? null
                : services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        if (publisher is not null)
            actor.EventPublisher = publisher;
        return actor;
    }

    private static Task HandleInitializedAsync(AgentProfileNamespaceGAgent actor, AgentProfileInitialized initialized) =>
        actor.HandleEventAsync(Envelope(initialized, initialized.SourceProfileActorId, actor.Id));

    private static async Task AddPublishedProfileAsync(
        AgentProfileNamespaceGAgent actor,
        AgentProfileOwner owner,
        string profileId,
        string profileSlug,
        byte digestByte)
    {
        var operation = Operation($"op-create-{profileId}", profileId);
        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = profileId,
            ProfileSlug = profileSlug,
            Operation = operation.Clone(),
        });
        await HandleInitializedAsync(actor, new AgentProfileInitialized
        {
            Identity = Identity(owner, profileId, profileSlug),
            Operation = operation.Clone(),
            SourceProfileActorId = AgentProfileActorIds.Profile(profileId),
            SourceAuthorityStateVersion = 1,
            ProvisioningOperationId = operation.OperationId,
        });
        var published = new ObserveAgentProfilePublishedCommand
        {
            Identity = Identity(owner, profileId, profileSlug),
            PublishedRevision = 1,
            SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat(digestByte, 32).ToArray()),
            DisplayName = profileSlug,
            Purpose = profileSlug,
            SourceProfileActorId = AgentProfileActorIds.Profile(profileId),
            SourceAuthorityStateVersion = 2,
            SourceOperationId = $"op-publish-{profileId}",
        };
        await actor.HandleEventAsync(Envelope(published, published.SourceProfileActorId, actor.Id));
    }

    private static Task SetSystemBindingAsync(
        AgentProfileNamespaceGAgent actor,
        AgentProfileOwner owner,
        AgentProfileBindingTarget target,
        long expectedVersion,
        int cohortBasisPoints,
        string suffix) =>
        actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = target.Clone(),
            System = new AgentProfileSystemBindingAdmission
            {
                Enabled = true,
                CohortBasisPoints = cohortBasisPoints,
            },
            ExpectedAuthorityStateVersion = expectedVersion,
            Operation = Operation($"op-bind-{suffix}", suffix),
        });

    private static AgentProfileIdentity Identity(
        AgentProfileOwner owner,
        string profileId,
        string profileSlug) => new()
    {
        Owner = owner.Clone(),
        ProfileId = profileId,
        ProfileSlug = profileSlug,
    };

    private static EventEnvelope Envelope(IMessage payload, string publisherActorId, string targetActorId) => new()
    {
        Id = $"test-{Guid.NewGuid():N}",
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
        Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
        Payload = Any.Pack(payload),
    };

    private static EventEnvelope SelfTimeoutEnvelope(
        AgentProfileNamespaceGAgent actor,
        AgentProfileCatalogEntry entry)
    {
        var envelope = Envelope(new AgentProfileProvisioningTimedOut
        {
            ProfileId = entry.ProfileId,
            ProvisioningOperationId = entry.ProvisioningOperationId,
            ProvisioningAttempt = entry.ProvisioningAttempt,
            CallbackId = entry.ProvisioningTimeoutCallbackId,
        }, actor.Id, actor.Id);
        envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(actor.Id, TopologyAudience.Self);
        envelope.Runtime = new EnvelopeRuntime
        {
            Callback = new EnvelopeCallbackContext
            {
                CallbackId = entry.ProvisioningTimeoutCallbackId,
                Generation = entry.ProvisioningAttempt,
                FireIndex = 1,
            },
        };
        return envelope;
    }

    private static AgentProfileOperationFact Operation(string operationId, string input) => new()
    {
        OperationId = operationId,
        CommandId = $"cmd-{operationId}",
        CorrelationId = $"corr-{operationId}",
        InputSha256 = ByteString.CopyFrom(AgentProfileDeterminism.Sha256Utf8(input)),
        RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
    };

    private static AgentProfileBindingTarget Target(
        AgentProfileOwner owner,
        string profileId,
        long publishedRevision,
        byte digestByte) => new()
    {
        Owner = owner.Clone(),
        ProfileId = profileId,
        PublishedRevision = publishedRevision,
        SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat(digestByte, 32).ToArray()),
    };

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(string TargetActorId, IMessage Event)> Sends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) where TEvent : IMessage
        {
            Sends.Add((targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
