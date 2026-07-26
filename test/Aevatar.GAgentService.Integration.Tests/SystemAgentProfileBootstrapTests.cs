using System.Reflection;
using System.Threading.Channels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Hosting.AgentProfiles;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Projection.AgentProfiles;
using Aevatar.Workflow.Extensions.Hosting;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class SystemAgentProfileBootstrapTests
{
    private const string DefinitionKey = "system/test-assistant";
    private const string ProfileId = "prof-system-test-assistant";
    private const string ProfileSlug = "test-assistant";

    [Fact]
    public async Task ReconcileAfterVersionConflict_ShouldConvergeThroughActorAndProjectedManagementReadModel()
    {
        var source = new MutableDefinitionSource(Content(displayName: "Initial Assistant"));
        var configuration = AgentProfileIngressProofTestConfiguration.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new IntegrationHostEnvironment());
        services.AddAevatarRuntime();
        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddSingleton<ISystemAgentProfileDefinitionSource>(source);
        services.AddGAgentServiceCapability(configuration);
        AddProjectionWriteBarrier<AgentProfileNamespaceCatalogDocument>(services);
        AddProjectionWriteBarrier<AgentProfileOwnerDocument>(services);
        using var provider = services.BuildServiceProvider();
        var actorPort = provider.GetRequiredService<IAgentProfileActorPort>();
        var namespaceQuery = provider.GetRequiredService<IAgentProfileNamespaceQueryPort>();
        var managementQuery = provider.GetRequiredService<IAgentProfileManagementQueryPort>();
        var executionQuery = provider.GetRequiredService<IAgentProfileExecutionSnapshotQueryPort>();
        var provisioning = provider.GetRequiredService<ISystemAgentProfileProvisioningService>();
        var namespaceWrites = provider.GetRequiredService<ProjectionWriteBarrier<AgentProfileNamespaceCatalogDocument>>();
        var ownerWrites = provider.GetRequiredService<ProjectionWriteBarrier<AgentProfileOwnerDocument>>();

        var initialNamespaceProjected = namespaceWrites.WaitForAsync(document =>
            document.Entries.Any(candidate =>
                candidate.Reference?.Equals(SystemReference()) == true &&
                candidate.Status == AgentProfileProvisioningStatus.Active));
        var initialOwnerProjected = ownerWrites.WaitForAsync(document =>
            document.Identity?.Reference?.Equals(SystemReference()) == true &&
            string.Equals(document.Draft?.DisplayName, "Initial Assistant", StringComparison.Ordinal));
        await provisioning.ReconcileAsync();
        await Task.WhenAll(initialNamespaceProjected, initialOwnerProjected).WaitAsync(TimeSpan.FromSeconds(10));
        var entry = await namespaceQuery.GetByReferenceAsync(SystemReference());
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(AgentProfileProvisioningStatus.Active);
        var stale = await managementQuery.GetAsync(entry.ProfileId);
        stale.Should().NotBeNull();
        stale!.Draft.DisplayName.Should().Be("Initial Assistant");

        var racingContent = Content(displayName: "Racing Assistant");
        var racingUpdateProjected = ownerWrites.WaitForAsync(document =>
            string.Equals(document.Id, entry.ProfileId, StringComparison.Ordinal) &&
            string.Equals(document.Draft?.DisplayName, "Racing Assistant", StringComparison.Ordinal));
        await actorPort.DispatchUpdateDraftAsync(new UpdateAgentProfileDraftCommand
        {
            Operation = new AgentProfileOperationFact
            {
                OperationId = "op-racing-update",
                CommandId = "cmd-racing-update",
                CorrelationId = "corr-racing-update",
                InputSha256 = AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                    stale.Identity,
                    racingContent),
            },
            Identity = stale.Identity,
            ExpectedAuthorityStateVersion = stale.AuthorityStateVersion,
            Content = racingContent,
        });
        await racingUpdateProjected.WaitAsync(TimeSpan.FromSeconds(10));
        var raced = await managementQuery.GetAsync(entry.ProfileId);
        raced.Should().NotBeNull();
        raced!.Draft.DisplayName.Should().Be("Racing Assistant");

        source.Content = Content(displayName: "Desired Assistant");
        var reconciliation = new SystemAgentProfileProvisioningService(
            [source],
            namespaceQuery,
            new StaleOnceManagementQueryPort(stale, managementQuery),
            executionQuery,
            actorPort,
            provider.GetRequiredService<AgentProfileSkillSealer>(),
            provider.GetRequiredService<ISystemAgentProfileOrnnAccessTokenProvider>());
        var conflictProjected = ownerWrites.WaitForAsync(document =>
            document.LastMutation?.Status == AgentProfileMutationStatus.Rejected &&
            string.Equals(document.LastMutation.Diagnostic?.Code, "DRAFT_VERSION_CONFLICT", StringComparison.Ordinal));
        await reconciliation.ReconcileAsync();
        await conflictProjected.WaitAsync(TimeSpan.FromSeconds(10));
        var conflicted = await managementQuery.GetAsync(entry.ProfileId);
        conflicted.Should().NotBeNull();
        conflicted!.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        conflicted.LastMutation.Diagnostic.Code.Should().Be("DRAFT_VERSION_CONFLICT");
        var conflictedOperationId = conflicted.LastMutation.Operation.OperationId;

        var convergenceProjected = ownerWrites.WaitForAsync(document =>
            string.Equals(document.Draft?.DisplayName, "Desired Assistant", StringComparison.Ordinal) &&
            document.LastMutation?.Status == AgentProfileMutationStatus.Applied);
        await reconciliation.ReconcileAsync();
        await convergenceProjected.WaitAsync(TimeSpan.FromSeconds(10));
        var converged = await managementQuery.GetAsync(entry.ProfileId);
        converged.Should().NotBeNull();
        converged!.Draft.DisplayName.Should().Be("Desired Assistant");
        converged.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Applied);
        converged.LastMutation.Operation.OperationId.Should().NotBe(conflictedOperationId);
        converged.AuthorityStateVersion.Should().BeGreaterThan(conflicted.AuthorityStateVersion);
    }

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
    public async Task BootstrapSignal_ShouldRetainOneWakeCoalesceAdditionalWakesAndHonorCancellation()
    {
        var signal = new SystemAgentProfileBootstrapSignal();

        signal.Pulse();
        signal.Pulse();
        await signal.WaitAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var wait = async () => await signal.WaitAsync(canceled.Token);
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AcceptedProfileProjection_ShouldWakeHostedBootstrapWithoutPolling()
    {
        var provisioning = new RecordingProvisioningService();
        var signal = new SystemAgentProfileBootstrapSignal();
        var observer = new SystemAgentProfileBootstrapMaterializationObserver(signal);
        using var hosted = new SystemAgentProfileBootstrapHostedService(provisioning, signal);
        var projector = new AgentProfileNamespaceCurrentStateProjector(
            new AppliedProjectionWriteDispatcher<AgentProfileNamespaceCatalogDocument>(),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-26T00:00:00+00:00")),
            [observer]);

        await hosted.StartAsync(CancellationToken.None);
        (await provisioning.WaitForCallAsync()).Should().Be(1);
        await projector.ProjectAsync(
            new AgentProfileNamespaceCurrentStateProjectionContext
            {
                RootActorId = AgentProfileActorIds.Namespace,
                ProjectionKind = "agent-profile-namespaces",
            },
            WrapCommittedNamespaceState());

        (await provisioning.WaitForCallAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)))
            .Should().Be(2);
        await hosted.StopAsync(CancellationToken.None);
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

    private static EventEnvelope WrapCommittedNamespaceState() =>
        new()
        {
            Id = "outer-agent-profile-namespace",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-26T00:00:00+00:00")),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = AgentProfileActorIds.Namespace,
                    EventId = "evt-agent-profile-namespace",
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.Parse("2026-07-26T00:00:00+00:00")),
                    EventData = Any.Pack(new StringValue { Value = "committed" }),
                },
                StateRoot = Any.Pack(new AgentProfileNamespaceState()),
            }),
        };

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
                RoutingPolicy = RoutingPolicy("binding-alpha").Clone(),
                Skill = new ExactOrnnSkillReference
                {
                    SkillGuid = "00000000-0000-0000-0000-000000000001",
                    LiteralVersion = "1.0",
                    ExpectedName = "skill-alpha",
                    ExpectedPublisherId = "publisher-alpha",
                },
            });
        }
        return AgentProfileDeterminism.NormalizeContent(content);
    }

    private static AgentProfileSkillRoutingPolicy RoutingPolicy(string bindingId) =>
        new()
        {
            IntentId = bindingId,
            RoutingDescription = $"Route requests for {bindingId}.",
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            },
            SideEffectClass = AgentProfileSkillSideEffectClass.ReadOnly,
            ExplicitTriggerAliases = { bindingId },
        };

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
        string profileSlug = ProfileSlug)
    {
        var normalizedContent = AgentProfileDeterminism.NormalizeContent(content);
        return new AgentProfileManagementSnapshot(
            11,
            "profile-event-11",
            SystemIdentity(profileId, profileSlug),
            normalizedContent,
            1,
            AgentProfileDeterminism.ComputeDraftSha256(normalizedContent),
            publishedRevision,
            publishedSnapshotSha256 ?? ByteString.Empty,
            publishedSourceDraftSha256 ?? ByteString.Empty,
            null);
    }

    private static AgentProfilePublishedSnapshot PublishedSnapshot(
        AgentProfileContent content,
        long revision)
    {
        var normalizedContent = AgentProfileDeterminism.NormalizeContent(content);
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = SystemIdentity(),
            DisplayName = normalizedContent.DisplayName,
            Purpose = normalizedContent.Purpose,
            Instructions = normalizedContent.Instructions,
            ToolPolicy = normalizedContent.ToolPolicy.Clone(),
            RecoveryToolPolicy = normalizedContent.RecoveryToolPolicy.Clone(),
            PublishedRevision = revision,
            SourceDraftSha256 = AgentProfileDeterminism.ComputeSourceDraftSha256(normalizedContent),
        };
        snapshot.SkillBindings.Add(normalizedContent.SkillBindings.Select(SealedBinding));
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        return AgentProfileDeterminism.NormalizePublishedSnapshot(snapshot);
    }

    private static SealedAgentProfileSkillBinding SealedBinding(AgentProfileSkillBinding binding)
    {
        var sealedSkill = new SealedAgentProfileSkill
        {
            ExactReference = binding.Skill.Clone(),
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = binding.Skill.SkillGuid,
                LiteralVersion = binding.Skill.LiteralVersion,
                CanonicalName = binding.Skill.ExpectedName,
                PublisherId = binding.Skill.ExpectedPublisherId,
                UpstreamSkillHash = "upstream-skill-hash-alpha",
                Description = "Exact system skill",
                Instructions = "Follow the resolved skill.",
                Arguments = "request",
                WhenToUse = "Use for the system test assistant.",
                ModelInvocable = true,
                UserInvocable = false,
            },
        };
        sealedSkill.ContentSha256 = AgentProfileDeterminism.ComputeSkillContentSha256(sealedSkill);
        return new SealedAgentProfileSkillBinding
        {
            BindingId = binding.BindingId,
            ActivationMode = binding.ActivationMode,
            Skill = sealedSkill,
            RoutingPolicy = binding.RoutingPolicy?.Clone(),
        };
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

    private sealed class StaleOnceManagementQueryPort(
        AgentProfileManagementSnapshot stale,
        IAgentProfileManagementQueryPort inner) : IAgentProfileManagementQueryPort
    {
        private AgentProfileManagementSnapshot? _stale = stale.DeepClone();

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var staleResult = Interlocked.Exchange(ref _stale, null);
            return staleResult is not null
                ? Task.FromResult<AgentProfileManagementSnapshot?>(staleResult.DeepClone())
                : inner.GetAsync(profileId, ct);
        }
    }

    private static void AddProjectionWriteBarrier<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        services.AddSingleton<ProjectionWriteBarrier<TReadModel>>(sp =>
            new ProjectionWriteBarrier<TReadModel>(
                sp.GetRequiredService<InMemoryProjectionDocumentStore<TReadModel, string>>()));
        services.Replace(ServiceDescriptor.Singleton<IProjectionDocumentWriter<TReadModel>>(sp =>
            sp.GetRequiredService<ProjectionWriteBarrier<TReadModel>>()));
    }

    private sealed class ProjectionWriteBarrier<TReadModel>(IProjectionDocumentWriter<TReadModel> inner)
        : IProjectionDocumentWriter<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly Lock _gate = new();
        private Func<TReadModel, bool>? _predicate;
        private TaskCompletionSource? _completion;

        public Task WaitForAsync(Func<TReadModel, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            lock (_gate)
            {
                if (_completion is not null)
                    throw new InvalidOperationException("A projection write wait is already active.");

                _predicate = predicate;
                _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _completion.Task;
            }
        }

        public async Task<ProjectionWriteResult> UpsertAsync(
            TReadModel readModel,
            CancellationToken ct = default)
        {
            var result = await inner.UpsertAsync(readModel, ct);
            TaskCompletionSource? completion = null;
            lock (_gate)
            {
                if (_predicate?.Invoke(readModel) == true)
                {
                    completion = _completion;
                    _predicate = null;
                    _completion = null;
                }
            }

            completion?.TrySetResult();
            return result;
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            inner.DeleteAsync(id, ct);
    }

    private sealed class IntegrationHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AgentProfileIntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
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

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class AppliedProjectionWriteDispatcher<TReadModel>
        : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<ProjectionWriteResult> UpsertAsync(
            TReadModel readModel,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }
}

internal static class ValueTaskTestExtensions
{
    public static async ValueTask AsValueTask(this ValueTask<bool> valueTask) =>
        _ = await valueTask;
}
