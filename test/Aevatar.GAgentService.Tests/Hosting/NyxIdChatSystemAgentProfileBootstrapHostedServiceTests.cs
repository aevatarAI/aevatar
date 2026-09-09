using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Hosting.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Hosting;

public sealed class NyxIdChatSystemAgentProfileBootstrapHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldSetChannelReplyDefaultBinding()
    {
        var store = new ProfileStore();
        var actorPort = new RecordingActorPort(store);
        var service = CreateHostedService(store, actorPort, BuildOptions("Answer channel messages."));

        await service.StartAsync(CancellationToken.None);

        actorPort.BindingCommands.Should().Contain(command =>
            command.AgentKind == AgentProfilePolicies.ChannelReplyAgentKind &&
            command.Target.ProfileId == store.ProfileId("channel-reply-default"));
        store.CurrentBinding(AgentProfilePolicies.ChannelReplyAgentKind).Should().NotBeNull();
    }

    [Fact]
    public async Task StartAsync_WhenPublishedSnapshotChanges_ShouldUseNewBindingOperationId()
    {
        var store = new ProfileStore();
        var actorPort = new RecordingActorPort(store);
        await CreateHostedService(store, actorPort, BuildOptions("Answer channel messages.")).StartAsync(CancellationToken.None);
        var first = actorPort.BindingCommands.Single(command =>
            command.AgentKind == AgentProfilePolicies.ChannelReplyAgentKind);

        await CreateHostedService(store, actorPort, BuildOptions("Answer channel messages with updated policy text.")).StartAsync(CancellationToken.None);
        var second = actorPort.BindingCommands.Last(command =>
            command.AgentKind == AgentProfilePolicies.ChannelReplyAgentKind);

        second.Operation.OperationId.Should().NotBe(first.Operation.OperationId);
        second.Target.PublishedRevision.Should().BeGreaterThan(first.Target.PublishedRevision);
        store.CurrentBinding(AgentProfilePolicies.ChannelReplyAgentKind)!.Target.PublishedRevision
            .Should().Be(second.Target.PublishedRevision);
    }

    private static NyxIdChatSystemAgentProfileBootstrapHostedService CreateHostedService(
        ProfileStore store,
        RecordingActorPort actorPort,
        NyxIdChatSystemAgentProfileBootstrapOptions options)
    {
        var profileService = new AgentProfileApplicationService(
            new FakeCatalogQuery(store),
            new FakeManagementQuery(store),
            new FakeExecutionQuery(store),
            actorPort,
            new RecordingSealer(),
            TimeProvider.System);
        return new NyxIdChatSystemAgentProfileBootstrapHostedService(
            profileService,
            Options.Create(options),
            TimeProvider.System,
            NullLogger<NyxIdChatSystemAgentProfileBootstrapHostedService>.Instance);
    }

    private static NyxIdChatSystemAgentProfileBootstrapOptions BuildOptions(string instructions) =>
        new()
        {
            Enabled = true,
            ProjectionWaitTimeout = TimeSpan.FromSeconds(2),
            ProjectionPollInterval = TimeSpan.FromMilliseconds(1),
            ProfileSlug = "nyxid-chat-default",
            DisplayName = "NyxID Chat Default",
            Purpose = "Default public chat.",
            EnableChannelReplyDefaultBinding = true,
            ChannelReplyProfileSlug = "channel-reply-default",
            ChannelReplyDisplayName = "Channel Reply Default",
            ChannelReplyPurpose = "Default public channel replies.",
            Instructions = instructions,
            PolicyRevision = "same-policy-revision",
            MaximumToolPolicy = new AgentProfileToolPolicyOptions
            {
                ToolNames = { "ask_user" },
                ToolSetRefs = { AgentProfilePolicies.NyxIdChatRouteToolSet, AgentProfilePolicies.ChannelReplyRouteToolSet },
            },
        };

    private sealed class ProfileStore
    {
        private readonly Dictionary<string, ProfileState> _profiles = [];
        private readonly List<AgentProfileDefaultBinding> _bindings = [];
        private long _namespaceVersion;

        public string ProfileId(string slug) => _profiles[slug].Entry.ProfileId;

        public AgentProfileDefaultBinding? CurrentBinding(string agentKind) =>
            _bindings.SingleOrDefault(binding => binding.AgentKind == agentKind)?.Clone();

        public AgentProfileCatalogSnapshot Catalog(AgentProfileOwner owner)
        {
            owner.Should().BeEquivalentTo(AgentProfileOwners.ForSystem());
            MaterializePendingProfiles();
            return new AgentProfileCatalogSnapshot(
                "namespace-actor",
                _namespaceVersion,
                owner.Clone(),
                _profiles.Values.Select(state => state.Entry.Clone()).ToArray(),
                _bindings.Select(binding => binding.Clone()).ToArray(),
                null,
                DateTimeOffset.UtcNow);
        }

        public AgentProfileManagementSnapshot? Management(AgentProfileIdentity identity) =>
            _profiles.Values.SingleOrDefault(state => state.Identity.Equals(identity))?.Management;

        public AgentProfileExecutionSnapshot? Execution(AgentProfileBindingTarget target)
        {
            var state = _profiles.Values.SingleOrDefault(candidate =>
                AgentProfileDeterminism.SameOwner(candidate.Identity.Owner, target.Owner) &&
                candidate.Identity.ProfileId == target.ProfileId);
            if (state is null || state.Management.PublishedRevision != target.PublishedRevision)
                return null;
            if (state.ExecutionReadsRemaining > 0)
            {
                state.ExecutionReadsRemaining--;
                return null;
            }

            return new AgentProfileExecutionSnapshot(
                state.Identity.ProfileId,
                state.Management.AuthorityStateVersion,
                state.Identity.Clone(),
                new AgentProfilePublishedSnapshot
                {
                    Identity = state.Identity.Clone(),
                    PublishedRevision = state.Management.PublishedRevision,
                    SnapshotSha256 = state.Management.PublishedSnapshotSha256,
                    RuntimeProfile = state.PublishedRuntimeProfile.Clone(),
                },
                DateTimeOffset.UtcNow);
        }

        public void Create(CreateAgentProfileCommand command)
        {
            var identity = new AgentProfileIdentity
            {
                Owner = command.Owner.Clone(),
                ProfileId = command.ProfileId,
                ProfileSlug = command.ProfileSlug,
            };
            _profiles[command.ProfileSlug] = new ProfileState(
                identity,
                new AgentProfileCatalogEntry
                {
                    ProfileId = command.ProfileId,
                    ProfileSlug = command.ProfileSlug,
                    Status = AgentProfileProvisioningStatus.Provisioning,
                },
                EmptyManagement(identity));
            _namespaceVersion++;
        }

        public void UpdateDraft(UpdateAgentProfileDraftCommand command)
        {
            var state = Profile(command.Identity.ProfileSlug);
            state.Management = state.Management with
            {
                AuthorityStateVersion = state.Management.AuthorityStateVersion + 1,
                Draft = command.Draft.Clone(),
                DraftRevision = state.Management.DraftRevision + 1,
                DraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(command.Draft),
            };
        }

        public void Publish(PublishAgentProfileCommand command)
        {
            var state = Profile(command.Identity.ProfileSlug);
            state.Management = state.Management with
            {
                AuthorityStateVersion = state.Management.AuthorityStateVersion + 1,
                PublishedDisplayName = command.Snapshot.DisplayName,
                PublishedPurpose = command.Snapshot.Purpose,
                PublishedRevision = command.Snapshot.PublishedRevision,
                PublishedSnapshotSha256 = command.Snapshot.SnapshotSha256,
                PublishedAt = DateTimeOffset.UtcNow,
            };
            state.PublishedRuntimeProfile = command.Snapshot.RuntimeProfile.Clone();
            state.Entry.PublishedRevision = command.Snapshot.PublishedRevision;
            state.Entry.SnapshotSha256 = command.Snapshot.SnapshotSha256;
            state.ExecutionReadsRemaining = 1;
            _namespaceVersion++;
        }

        public void SetBinding(SetAgentProfileDefaultBindingCommand command)
        {
            _bindings.RemoveAll(binding => binding.AgentKind == command.AgentKind);
            _bindings.Add(new AgentProfileDefaultBinding
            {
                AgentKind = command.AgentKind,
                Target = command.Target.Clone(),
                System = command.System.Clone(),
            });
            _namespaceVersion++;
        }

        private ProfileState Profile(string slug) => _profiles[slug];

        private static AgentProfileManagementSnapshot EmptyManagement(AgentProfileIdentity identity) =>
            new(
                $"actor-{identity.ProfileId}",
                0,
                identity.Clone(),
                null,
                0,
                ByteString.Empty,
                string.Empty,
                string.Empty,
                0,
                ByteString.Empty,
                null,
                null,
                DateTimeOffset.UtcNow);

        private void MaterializePendingProfiles()
        {
            foreach (var state in _profiles.Values.Where(state => state.Entry.Status == AgentProfileProvisioningStatus.Provisioning))
            {
                if (state.ProfileReadsBeforeActive > 0)
                {
                    state.ProfileReadsBeforeActive--;
                    continue;
                }

                state.Entry.Status = AgentProfileProvisioningStatus.Active;
                state.Entry.ProfileActorId = state.Management.ActorId;
                _namespaceVersion++;
            }
        }
    }

    private sealed class ProfileState(
        AgentProfileIdentity identity,
        AgentProfileCatalogEntry entry,
        AgentProfileManagementSnapshot management)
    {
        public AgentProfileIdentity Identity { get; } = identity;
        public AgentProfileCatalogEntry Entry { get; } = entry;
        public AgentProfileManagementSnapshot Management { get; set; } = management;
        public AgentProfileSnapshot PublishedRuntimeProfile { get; set; } = new();
        public int ProfileReadsBeforeActive { get; set; } = 1;
        public int ExecutionReadsRemaining { get; set; }
    }

    private sealed class FakeCatalogQuery(ProfileStore store) : IAgentProfileCatalogQueryPort
    {
        public Task<AgentProfileCatalogSnapshot?> GetAsync(AgentProfileOwner owner, CancellationToken ct = default) =>
            Task.FromResult<AgentProfileCatalogSnapshot?>(store.Catalog(owner));
    }

    private sealed class FakeManagementQuery(ProfileStore store) : IAgentProfileManagementQueryPort
    {
        public Task<AgentProfileManagementSnapshot?> GetAsync(AgentProfileIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(store.Management(identity));
    }

    private sealed class FakeExecutionQuery(ProfileStore store) : IAgentProfileExecutionQueryPort
    {
        public Task<AgentProfileExecutionSnapshot?> GetAsync(AgentProfileBindingTarget target, CancellationToken ct = default) =>
            Task.FromResult(store.Execution(target));
    }

    private sealed class RecordingActorPort(ProfileStore store) : IAgentProfileActorPort
    {
        public List<SetAgentProfileDefaultBindingCommand> BindingCommands { get; } = [];

        public Task<DispatchAdmission> DispatchCreateAsync(CreateAgentProfileCommand command, CancellationToken ct = default)
        {
            store.Create(command);
            return Admission(AgentProfileActorIds.Profile(command.ProfileId), command.Operation);
        }

        public Task<DispatchAdmission> DispatchInitializeAsync(string profileActorId, InitializeAgentProfileCommand command, CancellationToken ct = default) =>
            Admission(profileActorId, command.Operation);

        public Task<DispatchAdmission> DispatchUpdateDraftAsync(string profileActorId, UpdateAgentProfileDraftCommand command, CancellationToken ct = default)
        {
            store.UpdateDraft(command);
            return Admission(profileActorId, command.Operation);
        }

        public Task<DispatchAdmission> DispatchPublishAsync(string profileActorId, PublishAgentProfileCommand command, CancellationToken ct = default)
        {
            store.Publish(command);
            return Admission(profileActorId, command.Operation);
        }

        public Task<DispatchAdmission> DispatchSetDefaultBindingAsync(SetAgentProfileDefaultBindingCommand command, CancellationToken ct = default)
        {
            BindingCommands.Add(command.Clone());
            store.SetBinding(command);
            return Admission("namespace-actor", command.Operation);
        }

        public Task<DispatchAdmission> DispatchClearDefaultBindingAsync(ClearAgentProfileDefaultBindingCommand command, CancellationToken ct = default) =>
            Admission("namespace-actor", command.Operation);

        private static Task<DispatchAdmission> Admission(string actorId, AgentProfileOperationFact operation) =>
            Task.FromResult(new DispatchAdmission(
                true,
                operation.CommandId,
                DateTimeOffset.UtcNow,
                actorId,
                operation.CorrelationId));
    }

    private sealed class RecordingSealer : IAgentProfileSkillSealer
    {
        public Task<AgentProfileSealingResult> ResolveAndSealAsync(
            AgentProfileIdentity identity,
            AgentProfileDraft draft,
            AgentProfileSealingContext context,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileSealingResult.Success(
                AgentProfileDeterminism.BuildPublishedSnapshot(
                    identity,
                    draft,
                    context.CurrentDraftRevision,
                    context.NextPublishedRevision,
                    context.PublishedAt)));
    }
}
