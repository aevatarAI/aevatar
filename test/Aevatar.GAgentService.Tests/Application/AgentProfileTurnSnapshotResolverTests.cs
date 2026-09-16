using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileTurnSnapshotResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenNoScopeOrSystemCatalogExists_ShouldReturnUnprofiled()
    {
        var resolver = CreateResolver(new FakeCatalogQuery(), new FakeExecutionQuery());

        var result = await resolver.ResolveAsync(
            "scope-a",
            "turn-a",
            ChatRouteAgentProfileKind.WorkspaceChat,
            explicitReference: null);

        result.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.Unprofiled);
        result.Profile.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_WhenScopeBindingExists_ShouldReturnPinnedPublishedSnapshot()
    {
        var fixture = ProfileFixture.Scope("scope-a");
        var catalogs = new FakeCatalogQuery();
        catalogs.Add(fixture.Catalog);
        var executions = new FakeExecutionQuery { Snapshot = fixture.Execution };
        var resolver = CreateResolver(catalogs, executions);

        var result = await resolver.ResolveAsync(
            "scope-a",
            "turn-a",
            ChatRouteAgentProfileKind.WorkspaceChat,
            explicitReference: null);

        result.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.Selected);
        result.Profile.Should().NotBeNull();
        AgentProfileSnapshotCodec.ByteEquivalent(result.Profile!, fixture.Profile).Should().BeTrue();
        executions.Target.Should().BeEquivalentTo(fixture.Target);
    }

    [Fact]
    public async Task ResolveAsync_WhenScopeIsUnbound_ShouldFallBackToEnabledSystemBinding()
    {
        var fixture = ProfileFixture.System();
        var catalogs = new FakeCatalogQuery();
        catalogs.Add(EmptyCatalog(AgentProfileOwners.ForScope("scope-a")));
        catalogs.Add(fixture.Catalog);
        var resolver = CreateResolver(
            catalogs,
            new FakeExecutionQuery { Snapshot = fixture.Execution });

        var result = await resolver.ResolveAsync(
            "scope-a",
            "stable-turn",
            ChatRouteAgentProfileKind.WorkspaceChat,
            explicitReference: null);

        result.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.Selected);
        result.Profile!.ProfileId.Should().Be(fixture.Profile.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_CanaryMiss_ShouldPinPreviousReviewedProfileInsteadOfUnprofiled()
    {
        var candidate = ProfileFixture.System("profile-candidate", 8, 0x18);
        var previous = ProfileFixture.System("profile-previous", 7, 0x17);
        var binding = candidate.Catalog.DefaultBindings.Single().Clone();
        binding.System.CohortBasisPoints = AgentProfilePolicies.CanaryCohortBasisPoints;
        binding.System.PreviousReviewedTarget = previous.Target.Clone();
        var catalog = new AgentProfileCatalogSnapshot(
            "catalog",
            4,
            AgentProfileOwners.ForSystem(),
            [candidate.Catalog.Profiles.Single().Clone(), previous.Catalog.Profiles.Single().Clone()],
            [binding],
            null,
            DateTimeOffset.UtcNow);
        var catalogs = new FakeCatalogQuery();
        catalogs.Add(EmptyCatalog(AgentProfileOwners.ForScope("scope-a")));
        catalogs.Add(catalog);
        var executions = new FakeExecutionQuery();
        executions.Snapshots.Add(candidate.Execution);
        executions.Snapshots.Add(previous.Execution);
        var resolver = CreateResolver(catalogs, executions);
        var candidateTurn = FindTurnIdentity(candidate.Target, insideCanary: true);
        var baselineTurn = FindTurnIdentity(candidate.Target, insideCanary: false);

        var selectedCandidate = await resolver.ResolveAsync(
            "scope-a", candidateTurn, ChatRouteAgentProfileKind.WorkspaceChat, explicitReference: null);
        var selectedBaseline = await resolver.ResolveAsync(
            "scope-a", baselineTurn, ChatRouteAgentProfileKind.WorkspaceChat, explicitReference: null);

        selectedCandidate.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.Selected);
        selectedCandidate.Profile!.ProfileId.Should().Be(candidate.Profile.ProfileId);
        selectedBaseline.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.Selected);
        selectedBaseline.Profile!.ProfileId.Should().Be(previous.Profile.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_WhenCatalogReadThrows_ShouldFailClosedAsReadModelUnavailable()
    {
        var resolver = CreateResolver(
            new FakeCatalogQuery { Failure = new InvalidOperationException("projection unavailable") },
            new FakeExecutionQuery());

        var result = await resolver.ResolveAsync(
            "scope-a",
            "turn-a",
            ChatRouteAgentProfileKind.WorkspaceChat,
            explicitReference: null);

        result.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_WhenExecutionSnapshotDigestDrifts_ShouldFailClosed()
    {
        var fixture = ProfileFixture.Scope("scope-a");
        fixture.Execution.Snapshot.RuntimeProfile.PolicyRevision = "tampered";
        var catalogs = new FakeCatalogQuery();
        catalogs.Add(fixture.Catalog);
        var resolver = CreateResolver(
            catalogs,
            new FakeExecutionQuery { Snapshot = fixture.Execution });

        var result = await resolver.ResolveAsync(
            "scope-a",
            "turn-a",
            ChatRouteAgentProfileKind.WorkspaceChat,
            explicitReference: null);

        result.Status.Should().Be(AgentProfileTurnSnapshotResolutionStatus.SnapshotDigestMismatch);
        result.Profile.Should().BeNull();
    }

    private static AgentProfileTurnSnapshotResolver CreateResolver(
        IAgentProfileCatalogQueryPort catalogs,
        IAgentProfileExecutionQueryPort executions) =>
        new(catalogs, executions, NullLogger<AgentProfileTurnSnapshotResolver>.Instance);

    private static AgentProfileCatalogSnapshot EmptyCatalog(AgentProfileOwner owner) =>
        new("catalog", 1, owner.Clone(), [], [], null, DateTimeOffset.UtcNow);

    private sealed class ProfileFixture
    {
        private ProfileFixture(
            AgentProfileSnapshot profile,
            AgentProfileBindingTarget target,
            AgentProfileCatalogSnapshot catalog,
            AgentProfileExecutionSnapshot execution)
        {
            Profile = profile;
            Target = target;
            Catalog = catalog;
            Execution = execution;
        }

        public AgentProfileSnapshot Profile { get; }
        public AgentProfileBindingTarget Target { get; }
        public AgentProfileCatalogSnapshot Catalog { get; }
        public AgentProfileExecutionSnapshot Execution { get; }

        public static ProfileFixture Scope(string scopeId) => Create(
            AgentProfileOwners.ForScope(scopeId),
            new AgentProfileScopeBindingAdmission());

        public static ProfileFixture System(
            string profileId = "profile-workspace",
            long publishedRevision = 7,
            byte digestByte = 9) => Create(
            AgentProfileOwners.ForSystem(),
            new AgentProfileSystemBindingAdmission
            {
                Enabled = true,
                CohortBasisPoints = AgentProfilePolicies.FullCohortBasisPoints,
            },
            profileId,
            publishedRevision,
            digestByte);

        private static ProfileFixture Create(
            AgentProfileOwner owner,
            object admission,
            string profileId = "profile-workspace",
            long publishedRevision = 7,
            byte digestByte = 9)
        {
            var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
            {
                ProfileId = profileId,
                ProfileVersion = "1.0.0",
                AgentKind = AgentProfilePolicies.WorkspaceChatAgentKind,
                PolicyRevision = "policy-1",
                RouteToolSetRef = AgentProfilePolicies.WorkspaceChatRouteToolSet,
                PublishedRevision = publishedRevision,
                MaxOwnedToolCount = 8,
                MaxSchemaBytes = 48 * 1024,
            });
            var snapshotSha = ByteString.CopyFrom(Enumerable.Repeat(digestByte, 32).ToArray());
            var target = new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = profile.ProfileId,
                PublishedRevision = profile.PublishedRevision,
                SnapshotSha256 = snapshotSha,
            };
            var binding = new AgentProfileDefaultBinding
            {
                AgentKind = profile.AgentKind,
                Target = target.Clone(),
            };
            switch (admission)
            {
                case AgentProfileScopeBindingAdmission scope:
                    binding.Scope = scope;
                    break;
                case AgentProfileSystemBindingAdmission system:
                    binding.System = system;
                    break;
            }
            var entry = new AgentProfileCatalogEntry
            {
                ProfileId = profile.ProfileId,
                ProfileSlug = profileId,
                ProfileActorId = "profile-actor",
                Status = AgentProfileProvisioningStatus.Active,
                PublishedRevision = profile.PublishedRevision,
                SnapshotSha256 = snapshotSha,
            };
            var identity = new AgentProfileIdentity
            {
                Owner = owner.Clone(),
                ProfileId = profile.ProfileId,
                ProfileSlug = entry.ProfileSlug,
            };
            var published = new AgentProfilePublishedSnapshot
            {
                Identity = identity.Clone(),
                RuntimeProfile = profile.Clone(),
                PublishedRevision = profile.PublishedRevision,
                SnapshotSha256 = snapshotSha,
            };
            return new ProfileFixture(
                profile,
                target,
                new AgentProfileCatalogSnapshot(
                    "catalog-actor",
                    3,
                    owner.Clone(),
                    [entry],
                    [binding],
                    null,
                    DateTimeOffset.UtcNow),
                new AgentProfileExecutionSnapshot(
                    "profile-actor",
                    4,
                    identity,
                    published,
                    DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeCatalogQuery : IAgentProfileCatalogQueryPort
    {
        private readonly Dictionary<string, AgentProfileCatalogSnapshot> _catalogs = [];
        public Exception? Failure { get; init; }

        public void Add(AgentProfileCatalogSnapshot snapshot) =>
            _catalogs[Key(snapshot.Owner)] = snapshot;

        public Task<AgentProfileCatalogSnapshot?> GetAsync(
            AgentProfileOwner owner,
            CancellationToken ct = default)
        {
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(_catalogs.GetValueOrDefault(Key(owner)));
        }

        private static string Key(AgentProfileOwner owner) => owner.ToByteString().ToBase64();
    }

    private sealed class FakeExecutionQuery : IAgentProfileExecutionQueryPort
    {
        public AgentProfileExecutionSnapshot? Snapshot { get; init; }
        public List<AgentProfileExecutionSnapshot> Snapshots { get; } = [];
        public AgentProfileBindingTarget? Target { get; private set; }

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            AgentProfileBindingTarget target,
            CancellationToken ct = default)
        {
            Target = target.Clone();
            var candidates = Snapshots.ToList();
            if (Snapshot is not null)
                candidates.Add(Snapshot);
            return Task.FromResult(candidates.SingleOrDefault(candidate =>
                candidate.Identity.ProfileId == target.ProfileId &&
                candidate.Snapshot.PublishedRevision == target.PublishedRevision));
        }
    }

    private static string FindTurnIdentity(AgentProfileBindingTarget target, bool insideCanary) =>
        Enumerable.Range(0, 100_000)
            .Select(static index => $"turn-{index}")
            .First(identity =>
                (CohortBucket(target, identity) < AgentProfilePolicies.CanaryCohortBasisPoints) == insideCanary);

    private static int CohortBucket(AgentProfileBindingTarget target, string identity)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{target.ProfileId}\0{target.PublishedRevision}\0{identity}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % AgentProfilePolicies.FullCohortBasisPoints);
    }
}
