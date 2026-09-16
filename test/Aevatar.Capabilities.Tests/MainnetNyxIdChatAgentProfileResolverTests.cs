using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetNyxIdChatAgentProfileResolverTests
{
    [Fact]
    public async Task ResolverRestart_ShouldUseLatestProtectedRevisionWithoutChangingExistingSnapshots()
    {
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var current = ProfileFixture.Create(owner, "research", publishedRevision: 1);
        var catalogs = new StaticCatalogQueryPort(requestedOwner =>
            SameOwner(requestedOwner, owner)
                ? Catalog(current, [ScopeBinding(current.Target)])
                : null);
        var executions = new DynamicExecutionQueryPort(target =>
            current.Target.Equals(target) ? current.Execution : null);
        var firstHostResolver = CreateResolver(catalogs, executions);

        var firstResolution = await firstHostResolver.ResolveAsync(new(
            "scope-alpha",
            "conversation-a",
            null));
        var conversationA = firstResolution.Profile!.Clone();

        current = ProfileFixture.Create(owner, "research", publishedRevision: 2);
        var secondResolution = await firstHostResolver.ResolveAsync(new(
            "scope-alpha",
            "conversation-b",
            null));
        var conversationB = secondResolution.Profile!.Clone();

        var restartedHostResolver = CreateResolver(catalogs, executions);
        var restartResolution = await restartedHostResolver.ResolveAsync(new(
            "scope-alpha",
            "conversation-c",
            null));
        var conversationC = restartResolution.Profile!;

        conversationA.PublishedRevision.Should().Be(1);
        conversationB.PublishedRevision.Should().Be(2);
        conversationC.PublishedRevision.Should().Be(2);
        conversationA.PublishedRevision.Should().Be(1, "an existing Conversation snapshot cannot hot-upgrade");
        catalogs.Owners.Should().HaveCount(3);
        executions.Targets.Should().HaveCount(3);
        typeof(MainnetNyxIdChatAgentProfileResolver).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Select(static parameter => parameter.ParameterType)
            .Should().Equal(
                typeof(IAgentProfileCatalogQueryPort),
                typeof(IAgentProfileExecutionQueryPort),
                typeof(ILogger<MainnetNyxIdChatAgentProfileResolver>));
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseExplicitCallerReferenceBeforeAnyDefault()
    {
        var caller = ProfileFixture.Create(AgentProfileOwners.ForScope("scope-alpha"), "caller-profile");
        var system = ProfileFixture.Create(AgentProfileOwners.ForSystem(), "system-profile");
        var catalogs = new StaticCatalogQueryPort(owner =>
            SameOwner(owner, caller.Owner)
                ? Catalog(caller, [])
                : SameOwner(owner, system.Owner)
                    ? Catalog(system, [SystemBinding(system.Target, enabled: true, 10_000)])
                    : null);
        var resolver = CreateResolver(
            catalogs,
            new StaticExecutionQueryPort(caller, system));

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha",
            "conversation-alpha",
            new AgentProfileReference
            {
                OwnerKind = AgentProfileReferenceOwnerKind.Caller,
                ProfileSlug = caller.Slug,
            }));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Selected);
        result.Source.Should().Be(NyxIdChatAgentProfileSelectionSource.ExplicitCallerReference);
        result.Profile!.ProfileId.Should().Be(caller.Target.ProfileId);
        catalogs.Owners.Should().ContainSingle(owner => SameOwner(owner, caller.Owner));
    }

    [Fact]
    public async Task ResolveAsync_ShouldPreferScopeDefaultOverSystemDefault()
    {
        var scope = ProfileFixture.Create(AgentProfileOwners.ForScope("scope-alpha"), "scope-profile");
        var system = ProfileFixture.Create(AgentProfileOwners.ForSystem(), "system-profile");
        var resolver = CreateResolver(
            new StaticCatalogQueryPort(owner =>
                SameOwner(owner, scope.Owner)
                    ? Catalog(scope, [ScopeBinding(scope.Target)])
                    : SameOwner(owner, system.Owner)
                        ? Catalog(system, [SystemBinding(system.Target, enabled: true, 10_000)])
                        : null),
            new StaticExecutionQueryPort(scope, system));

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha", "conversation-alpha", null));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Selected);
        result.Source.Should().Be(NyxIdChatAgentProfileSelectionSource.ScopeDefault);
        result.Profile!.ProfileId.Should().Be(scope.Target.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseFullCohortSystemDefaultWhenScopeHasNoBinding()
    {
        var system = ProfileFixture.Create(AgentProfileOwners.ForSystem(), "system-profile");
        var resolver = CreateResolver(
            new StaticCatalogQueryPort(owner => SameOwner(owner, system.Owner)
                ? Catalog(system, [SystemBinding(system.Target, enabled: true, 10_000)])
                : null),
            new StaticExecutionQueryPort(system));

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha", "conversation-alpha", null));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Selected);
        result.Source.Should().Be(NyxIdChatAgentProfileSelectionSource.SystemDefault);
        result.Profile!.ProfileId.Should().Be(system.Target.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_CanaryMiss_ShouldSelectPreviousReviewedProfile()
    {
        var owner = AgentProfileOwners.ForSystem();
        var candidate = ProfileFixture.Create(owner, "candidate", publishedRevision: 2);
        var previous = ProfileFixture.Create(owner, "previous", publishedRevision: 1);
        var binding = SystemBinding(
            candidate.Target,
            enabled: true,
            AgentProfilePolicies.CanaryCohortBasisPoints,
            previous.Target);
        var catalog = Catalog([candidate, previous], [binding]);
        var resolver = CreateResolver(
            new StaticCatalogQueryPort(requestedOwner => SameOwner(requestedOwner, owner) ? catalog : null),
            new StaticExecutionQueryPort(candidate, previous));
        var canaryConversation = FindConversation(candidate.Target, insideCanary: true);
        var baselineConversation = FindConversation(candidate.Target, insideCanary: false);

        var selectedCandidate = await resolver.ResolveAsync(new(
            "scope-alpha", canaryConversation, null));
        var selectedBaseline = await resolver.ResolveAsync(new(
            "scope-alpha", baselineConversation, null));

        selectedCandidate.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Selected);
        selectedCandidate.Profile!.ProfileId.Should().Be(candidate.Target.ProfileId);
        selectedBaseline.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Selected);
        selectedBaseline.Profile!.ProfileId.Should().Be(previous.Target.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFailClosedWhenSelectedBindingHasNoExecutionReadModel()
    {
        var scope = ProfileFixture.Create(AgentProfileOwners.ForScope("scope-alpha"), "scope-profile");
        var resolver = CreateResolver(
            new StaticCatalogQueryPort(owner => SameOwner(owner, scope.Owner)
                ? Catalog(scope, [ScopeBinding(scope.Target)])
                : null),
            new StaticExecutionQueryPort());

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha", "conversation-alpha", null));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.ReadModelUnavailable);
        result.Profile.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldFailClosedWhenSystemBindingIsDisabled()
    {
        var system = ProfileFixture.Create(AgentProfileOwners.ForSystem(), "system-profile");
        var resolver = CreateResolver(
            new StaticCatalogQueryPort(owner => SameOwner(owner, system.Owner)
                ? Catalog(system, [SystemBinding(system.Target, enabled: false, 0)])
                : null),
            new StaticExecutionQueryPort(system));

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha", "conversation-alpha", null));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.BindingUnavailable);
        result.Profile.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldLogQueryFailuresBeforeFailingClosed()
    {
        var failure = new InvalidOperationException("catalog unavailable");
        var logger = new RecordingLogger<MainnetNyxIdChatAgentProfileResolver>();
        var resolver = CreateResolver(
            new ThrowingCatalogQueryPort(failure),
            new StaticExecutionQueryPort(),
            logger);

        var result = await resolver.ResolveAsync(new NyxIdChatAgentProfileSelectionRequest(
            "scope-alpha", "conversation-alpha", null));

        result.Status.Should().Be(NyxIdChatAgentProfileResolutionStatus.Unprofiled);
        logger.Entries.Should().HaveCount(2);
        logger.Entries.Should().OnlyContain(entry =>
            entry.Level == LogLevel.Warning && ReferenceEquals(entry.Exception, failure));
    }

    private static MainnetNyxIdChatAgentProfileResolver CreateResolver(
        IAgentProfileCatalogQueryPort catalogs,
        IAgentProfileExecutionQueryPort executions,
        ILogger<MainnetNyxIdChatAgentProfileResolver>? logger = null) =>
        new(catalogs, executions, logger ?? new RecordingLogger<MainnetNyxIdChatAgentProfileResolver>());

    private static AgentProfileCatalogSnapshot Catalog(
        ProfileFixture fixture,
        IReadOnlyList<AgentProfileDefaultBinding> bindings) => new(
        $"namespace-{fixture.Slug}",
        1,
        fixture.Owner.Clone(),
        [new AgentProfileCatalogEntry
        {
            ProfileId = fixture.Target.ProfileId,
            ProfileSlug = fixture.Slug,
            ProfileActorId = $"profile-{fixture.Target.ProfileId}",
            Status = AgentProfileProvisioningStatus.Active,
            PublishedRevision = fixture.Target.PublishedRevision,
            SnapshotSha256 = fixture.Target.SnapshotSha256,
        }],
        bindings,
        null,
        DateTimeOffset.UnixEpoch);

    private static AgentProfileCatalogSnapshot Catalog(
        IReadOnlyList<ProfileFixture> fixtures,
        IReadOnlyList<AgentProfileDefaultBinding> bindings) => new(
        "namespace-system-rollout",
        1,
        fixtures[0].Owner.Clone(),
        fixtures.Select(fixture => new AgentProfileCatalogEntry
        {
            ProfileId = fixture.Target.ProfileId,
            ProfileSlug = fixture.Slug,
            ProfileActorId = $"profile-{fixture.Target.ProfileId}",
            Status = AgentProfileProvisioningStatus.Active,
            PublishedRevision = fixture.Target.PublishedRevision,
            SnapshotSha256 = fixture.Target.SnapshotSha256,
        }).ToArray(),
        bindings,
        null,
        DateTimeOffset.UnixEpoch);

    private static AgentProfileDefaultBinding ScopeBinding(AgentProfileBindingTarget target) => new()
    {
        AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
        Target = target.Clone(),
        Scope = new AgentProfileScopeBindingAdmission(),
    };

    private static AgentProfileDefaultBinding SystemBinding(
        AgentProfileBindingTarget target,
        bool enabled,
        int cohortBasisPoints,
        AgentProfileBindingTarget? previousReviewedTarget = null) => new()
    {
        AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
        Target = target.Clone(),
        System = new AgentProfileSystemBindingAdmission
        {
            Enabled = enabled,
            CohortBasisPoints = cohortBasisPoints,
            PreviousReviewedTarget = previousReviewedTarget?.Clone(),
        },
    };

    private static string FindConversation(AgentProfileBindingTarget target, bool insideCanary) =>
        Enumerable.Range(0, 100_000)
            .Select(static index => $"conversation-{index}")
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

    private static bool SameOwner(AgentProfileOwner left, AgentProfileOwner right) =>
        AgentProfileDeterminism.SameOwner(left, right);

    private sealed class StaticCatalogQueryPort(Func<AgentProfileOwner, AgentProfileCatalogSnapshot?> resolve)
        : IAgentProfileCatalogQueryPort
    {
        public List<AgentProfileOwner> Owners { get; } = [];

        public Task<AgentProfileCatalogSnapshot?> GetAsync(
            AgentProfileOwner owner,
            CancellationToken ct = default)
        {
            Owners.Add(owner.Clone());
            return Task.FromResult(resolve(owner));
        }
    }

    private sealed class StaticExecutionQueryPort(params ProfileFixture[] fixtures)
        : IAgentProfileExecutionQueryPort
    {
        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            AgentProfileBindingTarget target,
            CancellationToken ct = default)
        {
            var fixture = fixtures.SingleOrDefault(x => x.Target.Equals(target));
            return Task.FromResult(fixture?.Execution);
        }
    }

    private sealed class ThrowingCatalogQueryPort(Exception exception) : IAgentProfileCatalogQueryPort
    {
        public Task<AgentProfileCatalogSnapshot?> GetAsync(
            AgentProfileOwner owner,
            CancellationToken ct = default) => Task.FromException<AgentProfileCatalogSnapshot?>(exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));

        private sealed class EmptyScope : IDisposable
        {
            public static EmptyScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class DynamicExecutionQueryPort(
        Func<AgentProfileBindingTarget, AgentProfileExecutionSnapshot?> resolve)
        : IAgentProfileExecutionQueryPort
    {
        public List<AgentProfileBindingTarget> Targets { get; } = [];

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            AgentProfileBindingTarget target,
            CancellationToken ct = default)
        {
            Targets.Add(target.Clone());
            return Task.FromResult(resolve(target));
        }
    }

    private sealed record ProfileFixture(
        AgentProfileOwner Owner,
        string Slug,
        AgentProfileBindingTarget Target,
        AgentProfileExecutionSnapshot Execution)
    {
        public static ProfileFixture Create(
            AgentProfileOwner owner,
            string slug,
            long publishedRevision = 1)
        {
            var identity = new AgentProfileIdentity
            {
                ProfileId = $"prof-{slug}",
                Owner = owner.Clone(),
                ProfileSlug = slug,
            };
            var draft = new AgentProfileDraft
            {
                DisplayName = slug,
                Instructions = "Use the profile.",
                RuntimeProfile = new AgentProfileSnapshot
                {
                    AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                    RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                    MaximumToolPolicy = new AgentProfileToolPolicy(),
                    RecoveryToolPolicy = new AgentProfileToolPolicy(),
                },
            };
            var published = AgentProfileDeterminism.BuildPublishedSnapshot(
                identity,
                draft,
                publishedRevision,
                publishedRevision,
                DateTimeOffset.UnixEpoch.AddMinutes(publishedRevision));
            var target = new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = identity.ProfileId,
                PublishedRevision = published.PublishedRevision,
                SnapshotSha256 = published.SnapshotSha256,
            };
            return new ProfileFixture(
                owner.Clone(),
                slug,
                target,
                new AgentProfileExecutionSnapshot(
                    $"profile-{identity.ProfileId}",
                    publishedRevision,
                    identity,
                    published,
                    DateTimeOffset.UnixEpoch));
        }
    }
}
