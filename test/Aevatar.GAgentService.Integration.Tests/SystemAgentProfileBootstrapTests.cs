using System.Reflection;
using System.Threading.Channels;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Hosting.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class SystemAgentProfileBootstrapTests
{
    private const string DefinitionKey = "system/test-assistant";
    private const string ProfileId = "prof-system-test-assistant";
    private const string ProfileSlug = "test-assistant";

    [Fact]
    public async Task Readiness_WithNoDefinitions_ShouldBeReadyWithoutQueryingFacts()
    {
        var source = new MutableDefinitionSource();
        var namespaceQuery = new MutableNamespaceQueryPort();
        var managementQuery = new MutableManagementQueryPort();
        var executionQuery = new MutableExecutionQueryPort();
        var service = new SystemAgentProfileReadinessService(
            [source],
            namespaceQuery,
            managementQuery,
            executionQuery,
            new MutableTokenProvider());

        var readiness = await service.GetAsync();

        readiness.IsReady.Should().BeTrue();
        readiness.Profiles.Should().BeEmpty();
        source.GetDefinitionsCalls.Should().Be(1);
        namespaceQuery.Calls.Should().Be(0);
        managementQuery.Calls.Should().Be(0);
        executionQuery.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Readiness_WhenDistinctKeysShareCanonicalReference_ShouldRejectBeforeQueryingFacts()
    {
        var namespaceQuery = new MutableNamespaceQueryPort();
        var service = new SystemAgentProfileReadinessService(
            [
                new MutableDefinitionSource(Content(), "system/assistant-alpha"),
                new MutableDefinitionSource(Content(), "system/assistant-beta"),
            ],
            namespaceQuery,
            new MutableManagementQueryPort(),
            new MutableExecutionQueryPort(),
            new MutableTokenProvider());

        var act = () => service.GetAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("System Profile reference 'system/test-assistant' is registered more than once.");
        namespaceQuery.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Readiness_ShouldRereadMaterializedFactsAcrossPendingUnavailableAndReadyStates()
    {
        var desired = Content(withSkill: true);
        var source = new MutableDefinitionSource(desired);
        var namespaceQuery = new MutableNamespaceQueryPort();
        var managementQuery = new MutableManagementQueryPort();
        var executionQuery = new MutableExecutionQueryPort();
        var tokenProvider = new MutableTokenProvider();
        var service = new SystemAgentProfileReadinessService(
            [source],
            namespaceQuery,
            managementQuery,
            executionQuery,
            tokenProvider);

        var missing = await service.GetAsync();
        AssertSingle(
            missing,
            SystemAgentProfileReadinessStatus.Pending,
            SystemAgentProfileReadinessReason.NamespaceMissing);

        namespaceQuery.Result = NamespaceEntry(
            owner: new AgentProfileOwnerIdentity
            {
                User = new AgentProfileUserOwnerIdentity
                {
                    IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                    SubjectId = "ordinary-subject",
                },
            },
            owningScopeId: "ordinary-scope");
        var conflict = await service.GetAsync();
        AssertSingle(
            conflict,
            SystemAgentProfileReadinessStatus.Unhealthy,
            SystemAgentProfileReadinessReason.NamespaceConflict);

        namespaceQuery.Result = NamespaceEntry();
        managementQuery.Result = Management(desired);
        var unavailable = await service.GetAsync();
        AssertSingle(
            unavailable,
            SystemAgentProfileReadinessStatus.Unavailable,
            SystemAgentProfileReadinessReason.OrnnAccessTokenUnavailable);
        tokenProvider.DefinitionKeys.Should().Equal(DefinitionKey);

        var published = PublishedSnapshot(desired, revision: 4);
        managementQuery.Result = Management(
            desired,
            publishedRevision: published.PublishedRevision,
            publishedSourceDraftSha256: published.SourceDraftSha256,
            publishedSnapshotSha256: published.SnapshotSha256);
        var executionMissing = await service.GetAsync();
        AssertSingle(
            executionMissing,
            SystemAgentProfileReadinessStatus.Pending,
            SystemAgentProfileReadinessReason.ExecutionSnapshotMissing);
        tokenProvider.DefinitionKeys.Should().ContainSingle();

        executionQuery.Result = ExecutionSnapshot(
            PublishedSnapshot(Content(withSkill: true, displayName: "Lagging"), revision: 3));
        var executionLagging = await service.GetAsync();
        AssertSingle(
            executionLagging,
            SystemAgentProfileReadinessStatus.Pending,
            SystemAgentProfileReadinessReason.ExecutionSnapshotLagging);

        executionQuery.Result = ExecutionSnapshot(published);
        var ready = await service.GetAsync();
        var entry = AssertSingle(
            ready,
            SystemAgentProfileReadinessStatus.Ready,
            SystemAgentProfileReadinessReason.None);
        entry.Reference.Should().BeEquivalentTo(SystemReference());
        entry.ProfileId.Should().Be(ProfileId);
        entry.DraftRevision.Should().Be(1);
        entry.PublishedRevision.Should().Be(4);
        entry.ExecutionPublishedRevision.Should().Be(4);
        entry.DesiredContentSha256.Should().Equal(
            AgentProfileDeterminism.ComputeSourceDraftSha256(desired));
        entry.ExecutionSnapshotSha256.Should().Equal(published.SnapshotSha256);
        source.GetDefinitionsCalls.Should().Be(6);
        namespaceQuery.Calls.Should().Be(6);
    }

    [Fact]
    public async Task Readiness_WithoutExactBindings_ShouldNotRequestSystemToken()
    {
        var desired = Content();
        var tokenProvider = new MutableTokenProvider();
        var service = new SystemAgentProfileReadinessService(
            [new MutableDefinitionSource(desired)],
            new MutableNamespaceQueryPort { Result = NamespaceEntry() },
            new MutableManagementQueryPort { Result = Management(desired) },
            new MutableExecutionQueryPort(),
            tokenProvider);

        var readiness = await service.GetAsync();

        AssertSingle(
            readiness,
            SystemAgentProfileReadinessStatus.Pending,
            SystemAgentProfileReadinessReason.PublicationPending);
        tokenProvider.DefinitionKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Readiness_WhenExecutionPayloadIsTamperedBehindExpectedDigest_ShouldRemainPending()
    {
        var desired = Content();
        var published = PublishedSnapshot(desired, revision: 4);
        var tampered = published.Clone();
        tampered.Instructions = "Tampered after the declared digest was computed.";
        var service = new SystemAgentProfileReadinessService(
            [new MutableDefinitionSource(desired)],
            new MutableNamespaceQueryPort { Result = NamespaceEntry() },
            new MutableManagementQueryPort
            {
                Result = Management(
                    desired,
                    publishedRevision: published.PublishedRevision,
                    publishedSourceDraftSha256: published.SourceDraftSha256,
                    publishedSnapshotSha256: published.SnapshotSha256),
            },
            new MutableExecutionQueryPort { Result = ExecutionSnapshot(tampered) },
            new MutableTokenProvider());

        var readiness = await service.GetAsync();

        AssertSingle(
            readiness,
            SystemAgentProfileReadinessStatus.Pending,
            SystemAgentProfileReadinessReason.ExecutionSnapshotLagging);
    }

    [Fact]
    public async Task Readiness_WhenOptionalProfileIsUnavailable_ShouldPreserveReadyAggregateAndEntryReason()
    {
        const string optionalDefinitionKey = "system/optional-assistant";
        const string optionalProfileId = "prof-system-optional-assistant";
        const string optionalProfileSlug = "optional-assistant";
        var requiredContent = Content(displayName: "Required Assistant");
        var optionalContent = Content(withSkill: true, displayName: "Optional Assistant");
        var requiredPublished = PublishedSnapshot(requiredContent, revision: 4);
        var requiredManagement = Management(
            requiredContent,
            publishedRevision: requiredPublished.PublishedRevision,
            publishedSourceDraftSha256: requiredPublished.SourceDraftSha256,
            publishedSnapshotSha256: requiredPublished.SnapshotSha256);
        var optionalManagement = Management(
            optionalContent,
            profileId: optionalProfileId,
            profileSlug: optionalProfileSlug);
        var tokenProvider = new MutableTokenProvider();
        var service = new SystemAgentProfileReadinessService(
            [
                new MutableDefinitionSource(requiredContent),
                new MutableDefinitionSource(
                    optionalContent,
                    optionalDefinitionKey,
                    optionalProfileSlug,
                    required: false),
            ],
            new MutableNamespaceQueryPort
            {
                ResultFactory = reference => reference.ProfileSlug == ProfileSlug
                    ? NamespaceEntry()
                    : NamespaceEntry(
                        profileId: optionalProfileId,
                        profileSlug: optionalProfileSlug),
            },
            new MutableManagementQueryPort
            {
                ResultFactory = profileId => profileId == ProfileId
                    ? requiredManagement
                    : optionalManagement,
            },
            new MutableExecutionQueryPort
            {
                ResultFactory = profileId => profileId == ProfileId
                    ? ExecutionSnapshot(requiredPublished)
                    : null,
            },
            tokenProvider);

        var readiness = await service.GetAsync();

        readiness.IsReady.Should().BeTrue();
        readiness.Profiles.Should().HaveCount(2);
        var required = readiness.Profiles.Single(profile => profile.DefinitionKey == DefinitionKey);
        required.Required.Should().BeTrue();
        required.Status.Should().Be(SystemAgentProfileReadinessStatus.Ready);
        required.Reason.Should().Be(SystemAgentProfileReadinessReason.None);
        var optional = readiness.Profiles.Single(
            profile => profile.DefinitionKey == optionalDefinitionKey);
        optional.Required.Should().BeFalse();
        optional.Status.Should().Be(SystemAgentProfileReadinessStatus.Unavailable);
        optional.Reason.Should().Be(SystemAgentProfileReadinessReason.OrnnAccessTokenUnavailable);
        tokenProvider.DefinitionKeys.Should().Equal(optionalDefinitionKey);
    }

    [Fact]
    public async Task HostedService_ShouldRunStartupPassAndOnePassPerManualSignal()
    {
        var provisioning = new RecordingProvisioningService();
        var signal = new ManualBootstrapSignal();
        using var hosted = new SystemAgentProfileBootstrapHostedService(
            provisioning,
            signal);

        await hosted.StartAsync(CancellationToken.None);
        (await provisioning.WaitForCallAsync()).Should().Be(1);

        signal.Pulse();
        (await provisioning.WaitForCallAsync()).Should().Be(2);

        signal.Pulse();
        (await provisioning.WaitForCallAsync()).Should().Be(3);
        await hosted.StopAsync(CancellationToken.None);

        provisioning.Calls.Should().Be(3);
    }

    [Fact]
    public void BootstrapAndReadiness_ShouldNotOwnProfileCachesOrProjectionLifecyclePorts()
    {
        var implementationTypes = new[]
        {
            typeof(SystemAgentProfileBootstrapHostedService),
            typeof(SystemAgentProfileReadinessService),
        };

        implementationTypes.SelectMany(type => type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Select(static field => field.FieldType)
            .Should().NotContain(type =>
                type.Name.Contains("Dictionary", StringComparison.Ordinal) ||
                type.Name.Contains("Cache", StringComparison.Ordinal));

        implementationTypes.SelectMany(static type => type.GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType.Name)
            .Should().NotContain(name =>
                name.Contains("ProjectionActivation", StringComparison.Ordinal) ||
                name.Contains("ActorRuntime", StringComparison.Ordinal) ||
                name.Contains("EventStore", StringComparison.Ordinal));
    }

    private static SystemAgentProfileReadinessEntry AssertSingle(
        SystemAgentProfileReadinessSnapshot snapshot,
        SystemAgentProfileReadinessStatus status,
        SystemAgentProfileReadinessReason reason)
    {
        snapshot.IsReady.Should().Be(status == SystemAgentProfileReadinessStatus.Ready);
        var entry = snapshot.Profiles.Should().ContainSingle().Which;
        entry.Status.Should().Be(status);
        entry.Reason.Should().Be(reason);
        return entry;
    }

    private static AgentProfileContent Content(
        bool withSkill = false,
        string displayName = "Test Assistant")
    {
        var content = new AgentProfileContent
        {
            DisplayName = displayName,
            Purpose = "Exercises system Profile readiness",
            Instructions = "Follow the exact built-in instructions.",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
        };
        if (withSkill)
        {
            content.SkillBindings.Add(new AgentProfileSkillBinding
            {
                BindingId = "binding-alpha",
                ActivationMode = AgentProfileSkillActivationMode.Routed,
                Skill = new ExactOrnnSkillReference
                {
                    SkillGuid = "00000000-0000-0000-0000-000000000001",
                    LiteralVersion = "1.0",
                    ExpectedName = "skill-alpha",
                    ExpectedPublisherId = "publisher-alpha",
                },
            });
        }
        return content;
    }

    private static AgentProfileReference SystemReference(string profileSlug = ProfileSlug) =>
        new()
        {
            OwnerHandle = AgentProfilePolicies.SystemOwnerHandle,
            ProfileSlug = profileSlug,
        };

    private static AgentProfileOwnerIdentity SystemOwner() =>
        new()
        {
            System = new AgentProfileSystemOwnerIdentity
            {
                PlatformId = AgentProfilePolicies.AevatarPlatformId,
            },
        };

    private static AgentProfileIdentity SystemIdentity(
        string profileId = ProfileId,
        string profileSlug = ProfileSlug) =>
        new()
        {
            ProfileId = profileId,
            Owner = SystemOwner(),
            OwningScopeId = string.Empty,
            Reference = SystemReference(profileSlug),
        };

    private static AgentProfileNamespaceEntrySnapshot NamespaceEntry(
        AgentProfileOwnerIdentity? owner = null,
        string owningScopeId = "",
        string profileId = ProfileId,
        string profileSlug = ProfileSlug) =>
        new(
            8,
            "namespace-event-8",
            profileId,
            SystemReference(profileSlug),
            owner ?? SystemOwner(),
            owningScopeId,
            AgentProfileProvisioningStatus.Active,
            null);

    private static AgentProfileManagementSnapshot Management(
        AgentProfileContent content,
        long publishedRevision = 0,
        ByteString? publishedSourceDraftSha256 = null,
        ByteString? publishedSnapshotSha256 = null,
        string profileId = ProfileId,
        string profileSlug = ProfileSlug) =>
        new(
            11,
            "profile-event-11",
            SystemIdentity(profileId, profileSlug),
            content,
            1,
            AgentProfileDeterminism.ComputeDraftSha256(content),
            publishedRevision,
            publishedSnapshotSha256 ?? ByteString.Empty,
            publishedSourceDraftSha256 ?? ByteString.Empty,
            null);

    private static AgentProfilePublishedSnapshot PublishedSnapshot(
        AgentProfileContent content,
        long revision)
    {
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = SystemIdentity(),
            DisplayName = content.DisplayName,
            Purpose = content.Purpose,
            Instructions = content.Instructions,
            ToolPolicy = content.ToolPolicy.Clone(),
            PublishedRevision = revision,
            SourceDraftSha256 = AgentProfileDeterminism.ComputeSourceDraftSha256(content),
        };
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        return snapshot;
    }

    private static AgentProfileExecutionSnapshot ExecutionSnapshot(
        AgentProfilePublishedSnapshot snapshot) =>
        new(
            snapshot.PublishedRevision + 20,
            $"execution-event-{snapshot.PublishedRevision}",
            snapshot);

    private sealed class MutableDefinitionSource : ISystemAgentProfileDefinitionSource
    {
        private readonly string _definitionKey;
        private readonly string _profileSlug;
        private readonly bool _required;

        public MutableDefinitionSource(
            AgentProfileContent? content = null,
            string definitionKey = DefinitionKey,
            string profileSlug = ProfileSlug,
            bool required = true)
        {
            Content = content?.Clone();
            _definitionKey = definitionKey;
            _profileSlug = profileSlug;
            _required = required;
        }

        public AgentProfileContent? Content { get; set; }

        public int GetDefinitionsCalls { get; private set; }

        public IReadOnlyList<SystemAgentProfileDefinition> GetDefinitions()
        {
            GetDefinitionsCalls++;
            return Content is null
                ? []
                : [new SystemAgentProfileDefinition(
                    _definitionKey,
                    _profileSlug,
                    Content.Clone(),
                    _required)];
        }
    }

    private sealed class MutableNamespaceQueryPort : IAgentProfileNamespaceQueryPort
    {
        public AgentProfileNamespaceEntrySnapshot? Result { get; set; }

        public Func<AgentProfileReference, AgentProfileNamespaceEntrySnapshot?>? ResultFactory { get; init; }

        public int Calls { get; private set; }

        public Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
            AgentProfileOwnerIdentity owner,
            string owningScopeId,
            string profileSlug,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("System readiness must use reference lookup.");

        public Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
            AgentProfileReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var result = ResultFactory is null ? Result : ResultFactory(reference);
            return Task.FromResult(result?.DeepClone());
        }
    }

    private sealed class MutableManagementQueryPort : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Result { get; set; }

        public Func<string, AgentProfileManagementSnapshot?>? ResultFactory { get; init; }

        public int Calls { get; private set; }

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var result = ResultFactory is null ? Result : ResultFactory(profileId);
            return Task.FromResult(result?.DeepClone());
        }
    }

    private sealed class MutableExecutionQueryPort : IAgentProfileExecutionSnapshotQueryPort
    {
        public AgentProfileExecutionSnapshot? Result { get; set; }

        public Func<string, AgentProfileExecutionSnapshot?>? ResultFactory { get; init; }

        public int Calls { get; private set; }

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var result = ResultFactory is null ? Result : ResultFactory(profileId);
            return Task.FromResult(result?.DeepClone());
        }
    }

    private sealed class MutableTokenProvider : ISystemAgentProfileOrnnAccessTokenProvider
    {
        public string? Token { get; set; }

        public List<string> DefinitionKeys { get; } = [];

        public Task<string?> GetAccessTokenAsync(
            string definitionKey,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DefinitionKeys.Add(definitionKey);
            return Task.FromResult(Token);
        }
    }

    private sealed class RecordingProvisioningService : ISystemAgentProfileProvisioningService
    {
        private readonly Channel<int> _notifications = Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        public int Calls { get; private set; }

        public Task ReconcileAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            _notifications.Writer.TryWrite(Calls).Should().BeTrue();
            return Task.CompletedTask;
        }

        public ValueTask<int> WaitForCallAsync(CancellationToken ct = default) =>
            _notifications.Reader.ReadAsync(ct);
    }

    private sealed class ManualBootstrapSignal : ISystemAgentProfileBootstrapSignal
    {
        private readonly Channel<bool> _signals = Channel.CreateUnbounded<bool>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        public ValueTask WaitAsync(CancellationToken ct = default) =>
            _signals.Reader.ReadAsync(ct).AsValueTask();

        public void Pulse() => _signals.Writer.TryWrite(true).Should().BeTrue();
    }
}

internal static class ValueTaskTestExtensions
{
    public static async ValueTask AsValueTask(this ValueTask<bool> valueTask) =>
        _ = await valueTask;
}
