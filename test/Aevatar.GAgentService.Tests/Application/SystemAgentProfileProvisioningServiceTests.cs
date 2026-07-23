using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class SystemAgentProfileProvisioningServiceTests
{
    private const string DefinitionKey = "system/test-assistant";
    private const string ProfileId = "prof-system-test-assistant";
    private const string ProfileSlug = "test-assistant";
    private const string SystemToken = "system-ornn-token-alpha";

    [Fact]
    public void Definition_ShouldOwnImmutableContentClones()
    {
        var sourceContent = Content();
        var definition = new SystemAgentProfileDefinition(
            DefinitionKey,
            ProfileSlug,
            sourceContent);

        sourceContent.DisplayName = "mutated source";
        var returned = definition.Content;
        returned.DisplayName = "mutated getter";

        definition.Content.DisplayName.Should().Be("Test Assistant");
        definition.Required.Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_WhenNamespaceEntryIsMissing_ShouldCreateStableSystemAuthority()
    {
        var source = new MutableDefinitionSource(Content());
        var namespaceQuery = new RecordingNamespaceQueryPort();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            source,
            namespaceQuery: namespaceQuery,
            actorPort: actorPort);

        await service.ReconcileAsync();
        await service.ReconcileAsync();

        actorPort.CreateCommands.Should().HaveCount(2);
        var first = actorPort.CreateCommands[0];
        var second = actorPort.CreateCommands[1];
        first.Identity.Owner.OwnerCase.Should().Be(
            AgentProfileOwnerIdentity.OwnerOneofCase.System);
        first.Identity.Owner.System.PlatformId.Should().Be(AgentProfilePolicies.AevatarPlatformId);
        first.Identity.OwningScopeId.Should().BeEmpty();
        first.Identity.Reference.Should().BeEquivalentTo(SystemReference());
        first.InitialContent.Should().Be(Content());
        first.Identity.ProfileId.Should().StartWith("prof_");
        second.Identity.ProfileId.Should().Be(first.Identity.ProfileId);
        second.Operation.OperationId.Should().Be(first.Operation.OperationId);
        second.Operation.CommandId.Should().NotBe(first.Operation.CommandId);
        second.Operation.CorrelationId.Should().NotBe(first.Operation.CorrelationId);
        source.GetDefinitionsCalls.Should().Be(2);
        namespaceQuery.References.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReconcileAsync_WhenOrdinaryOwnerClaimsSystemReference_ShouldAdmitNoMutation()
    {
        var namespaceQuery = new RecordingNamespaceQueryPort
        {
            Result = NamespaceEntry(
                owner: new AgentProfileOwnerIdentity
                {
                    User = new AgentProfileUserOwnerIdentity
                    {
                        IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                        SubjectId = "ordinary-subject",
                    },
                },
                owningScopeId: "scope-ordinary"),
        };
        var managementQuery = new RecordingManagementQueryPort();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(Content()),
            namespaceQuery,
            managementQuery,
            actorPort: actorPort);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(0);
        managementQuery.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenManagementReadModelIsMissing_ShouldWaitWithoutMutation()
    {
        var managementQuery = new RecordingManagementQueryPort();
        var executionQuery = new RecordingExecutionQueryPort();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(Content()),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: managementQuery,
            executionQuery: executionQuery,
            actorPort: actorPort);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(0);
        managementQuery.ProfileIds.Should().Equal(ProfileId);
        executionQuery.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenDraftSurfaceAndBindingsDrift_ShouldOnlyUpdateSurface()
    {
        var desired = Content(withSkill: true);
        var current = Content(displayName: "Old Assistant");
        current.SkillBindings.Add(Binding("legacy-binding", 2, "legacy-skill"));
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(current),
            },
            actorPort: actorPort);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(1);
        var command = actorPort.UpdateCommands.Should().ContainSingle().Which;
        command.Content.DisplayName.Should().Be(desired.DisplayName);
        command.Content.Purpose.Should().Be(desired.Purpose);
        command.Content.Instructions.Should().Be(desired.Instructions);
        command.Content.ToolPolicy.Should().Be(desired.ToolPolicy);
        command.Content.SkillBindings.Should().Equal(current.SkillBindings);
        actorPort.UpsertCommands.Should().BeEmpty();
        actorPort.RemoveCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenOnlyBindingsDrift_ShouldAdmitOneBindingMutation()
    {
        var desired = Content(withSkill: true);
        var actorPort = new RecordingActorPort();
        var tokenProvider = new RecordingTokenProvider { Token = SystemToken };
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(Content()),
            },
            actorPort: actorPort,
            tokenProvider: tokenProvider);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(1);
        actorPort.UpsertCommands.Should().ContainSingle()
            .Which.Binding.Should().Be(desired.SkillBindings[0]);
        actorPort.PublishCommands.Should().BeEmpty();
        tokenProvider.DefinitionKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenDraftMatchesWithoutBindings_ShouldPublishWithoutTokenLookup()
    {
        var desired = Content();
        var tokenProvider = new RecordingTokenProvider();
        var resolver = new RecordingResolver();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(desired),
            },
            actorPort: actorPort,
            tokenProvider: tokenProvider,
            resolver: resolver);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(1);
        var command = actorPort.PublishCommands.Should().ContainSingle().Which;
        command.ExpectedDraftSha256.Should().Equal(
            AgentProfileDeterminism.ComputeDraftSha256(desired));
        command.Snapshot.SourceDraftSha256.Should().Equal(command.ExpectedDraftSha256);
        tokenProvider.DefinitionKeys.Should().BeEmpty();
        resolver.AccessTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenExactBindingHasNoSystemToken_ShouldRemainStableWithoutPublish()
    {
        var desired = Content(withSkill: true);
        var actorPort = new RecordingActorPort();
        var resolver = new RecordingResolver();
        var tokenProvider = new UnavailableSystemAgentProfileOrnnAccessTokenProvider();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(desired),
            },
            actorPort: actorPort,
            tokenProvider: tokenProvider,
            resolver: resolver);

        await service.ReconcileAsync();
        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(0);
        resolver.AccessTokens.Should().BeEmpty();
        (await tokenProvider.GetAccessTokenAsync(DefinitionKey)).Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_WhenHostProvidesToken_ShouldUseItOnlyForResolution()
    {
        var desired = Content(withSkill: true);
        var tokenProvider = new RecordingTokenProvider { Token = SystemToken };
        var resolver = new RecordingResolver();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(desired),
            },
            actorPort: actorPort,
            tokenProvider: tokenProvider,
            resolver: resolver);

        await service.ReconcileAsync();

        tokenProvider.DefinitionKeys.Should().Equal(DefinitionKey);
        resolver.AccessTokens.Should().Equal(SystemToken);
        var command = actorPort.PublishCommands.Should().ContainSingle().Which;
        command.ToString().Should().NotContain(SystemToken);
        typeof(SystemAgentProfileDefinition).GetProperties()
            .Select(static property => property.Name)
            .Should().NotContain(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        typeof(SystemAgentProfileReadinessEntry).GetProperties()
            .Select(static property => property.Name)
            .Should().NotContain(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReconcileAsync_WhenPublishedFactsMatchButExecutionLags_ShouldNotMutate()
    {
        var desired = Content();
        var published = PublishedSnapshot(desired, revision: 4);
        var executionQuery = new RecordingExecutionQueryPort();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: new RecordingManagementQueryPort
            {
                Result = Management(
                    desired,
                    publishedRevision: published.PublishedRevision,
                    publishedSourceDraftSha256: published.SourceDraftSha256,
                    publishedSnapshotSha256: published.SnapshotSha256),
            },
            executionQuery: executionQuery,
            actorPort: actorPort);

        await service.ReconcileAsync();
        executionQuery.Result = ExecutionSnapshot(
            PublishedSnapshot(Content(displayName: "Lagging Assistant"), revision: 3));
        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(0);
        executionQuery.ProfileIds.Should().Equal(ProfileId, ProfileId);
    }

    [Fact]
    public async Task ReconcileAsync_WhenDefinitionChanges_ShouldUpdateThenPublishWithNewRevisionInput()
    {
        var oldContent = Content(displayName: "Old Assistant");
        var desired = Content(displayName: "New Assistant");
        var oldPublished = PublishedSnapshot(oldContent, revision: 2);
        var managementQuery = new RecordingManagementQueryPort
        {
            Result = Management(
                oldContent,
                publishedRevision: oldPublished.PublishedRevision,
                publishedSourceDraftSha256: oldPublished.SourceDraftSha256,
                publishedSnapshotSha256: oldPublished.SnapshotSha256),
        };
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new MutableDefinitionSource(desired),
            namespaceQuery: new RecordingNamespaceQueryPort { Result = NamespaceEntry() },
            managementQuery: managementQuery,
            actorPort: actorPort);

        await service.ReconcileAsync();

        var update = actorPort.UpdateCommands.Should().ContainSingle().Which;
        actorPort.DispatchCount.Should().Be(1);
        managementQuery.Result = Management(
            desired,
            authorityVersion: 12,
            draftRevision: 2,
            publishedRevision: oldPublished.PublishedRevision,
            publishedSourceDraftSha256: oldPublished.SourceDraftSha256,
            publishedSnapshotSha256: oldPublished.SnapshotSha256);

        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(2);
        var publish = actorPort.PublishCommands.Should().ContainSingle().Which;
        publish.ExpectedDraftRevision.Should().Be(2);
        publish.Operation.OperationId.Should().NotBe(update.Operation.OperationId);
        publish.Snapshot.SourceDraftSha256.Should().Equal(
            AgentProfileDeterminism.ComputeSourceDraftSha256(desired));
    }

    [Fact]
    public async Task ReconcileAsync_WhenExecutionMatches_ShouldRereadEveryFactAndAdmitNoCommand()
    {
        var desired = Content();
        var published = PublishedSnapshot(desired, revision: 3);
        var source = new MutableDefinitionSource(desired);
        var namespaceQuery = new RecordingNamespaceQueryPort { Result = NamespaceEntry() };
        var managementQuery = new RecordingManagementQueryPort
        {
            Result = Management(
                desired,
                publishedRevision: published.PublishedRevision,
                publishedSourceDraftSha256: published.SourceDraftSha256,
                publishedSnapshotSha256: published.SnapshotSha256),
        };
        var executionQuery = new RecordingExecutionQueryPort
        {
            Result = ExecutionSnapshot(published),
        };
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            source,
            namespaceQuery,
            managementQuery,
            executionQuery,
            actorPort);

        await service.ReconcileAsync();
        await service.ReconcileAsync();

        actorPort.DispatchCount.Should().Be(0);
        source.GetDefinitionsCalls.Should().Be(2);
        namespaceQuery.References.Should().HaveCount(2);
        managementQuery.ProfileIds.Should().Equal(ProfileId, ProfileId);
        executionQuery.ProfileIds.Should().Equal(ProfileId, ProfileId);
    }

    private static SystemAgentProfileProvisioningService CreateService(
        MutableDefinitionSource source,
        RecordingNamespaceQueryPort? namespaceQuery = null,
        RecordingManagementQueryPort? managementQuery = null,
        RecordingExecutionQueryPort? executionQuery = null,
        RecordingActorPort? actorPort = null,
        ISystemAgentProfileOrnnAccessTokenProvider? tokenProvider = null,
        RecordingResolver? resolver = null)
    {
        resolver ??= new RecordingResolver();
        return new SystemAgentProfileProvisioningService(
            [source],
            namespaceQuery ?? new RecordingNamespaceQueryPort(),
            managementQuery ?? new RecordingManagementQueryPort(),
            executionQuery ?? new RecordingExecutionQueryPort(),
            actorPort ?? new RecordingActorPort(),
            new AgentProfileSkillSealer(resolver, new EmptyToolSetRegistry()),
            tokenProvider ?? new RecordingTokenProvider());
    }

    private static AgentProfileContent Content(
        bool withSkill = false,
        string displayName = "Test Assistant")
    {
        var content = new AgentProfileContent
        {
            DisplayName = displayName,
            Purpose = "Exercises system Profile bootstrap",
            Instructions = "Follow the exact built-in instructions.",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
        };
        if (withSkill)
            content.SkillBindings.Add(Binding("binding-alpha", 1, "skill-alpha"));
        return content;
    }

    private static AgentProfileSkillBinding Binding(
        string bindingId,
        int identity,
        string name) =>
        new()
        {
            BindingId = bindingId,
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = new ExactOrnnSkillReference
            {
                SkillGuid = $"00000000-0000-0000-0000-{identity:D12}",
                LiteralVersion = "1.0",
                ExpectedName = name,
                ExpectedPublisherId = "publisher-alpha",
            },
        };

    private static AgentProfileReference SystemReference() =>
        new()
        {
            OwnerHandle = AgentProfilePolicies.SystemOwnerHandle,
            ProfileSlug = ProfileSlug,
        };

    private static AgentProfileOwnerIdentity SystemOwner() =>
        new()
        {
            System = new AgentProfileSystemOwnerIdentity
            {
                PlatformId = AgentProfilePolicies.AevatarPlatformId,
            },
        };

    private static AgentProfileIdentity SystemIdentity(string profileId = ProfileId) =>
        new()
        {
            ProfileId = profileId,
            Owner = SystemOwner(),
            OwningScopeId = string.Empty,
            Reference = SystemReference(),
        };

    private static AgentProfileNamespaceEntrySnapshot NamespaceEntry(
        AgentProfileOwnerIdentity? owner = null,
        string owningScopeId = "") =>
        new(
            8,
            "namespace-event-8",
            ProfileId,
            SystemReference(),
            owner ?? SystemOwner(),
            owningScopeId,
            AgentProfileProvisioningStatus.Active,
            null);

    private static AgentProfileManagementSnapshot Management(
        AgentProfileContent content,
        long authorityVersion = 11,
        long draftRevision = 1,
        long publishedRevision = 0,
        ByteString? publishedSourceDraftSha256 = null,
        ByteString? publishedSnapshotSha256 = null) =>
        new(
            authorityVersion,
            $"profile-event-{authorityVersion}",
            SystemIdentity(),
            content,
            draftRevision,
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

    private sealed class MutableDefinitionSource(AgentProfileContent content)
        : ISystemAgentProfileDefinitionSource
    {
        public int GetDefinitionsCalls { get; private set; }

        public AgentProfileContent Content { get; set; } = content.Clone();

        public IReadOnlyList<SystemAgentProfileDefinition> GetDefinitions()
        {
            GetDefinitionsCalls++;
            return
            [
                new SystemAgentProfileDefinition(
                    DefinitionKey,
                    ProfileSlug,
                    Content.Clone()),
            ];
        }
    }

    private sealed class RecordingNamespaceQueryPort : IAgentProfileNamespaceQueryPort
    {
        public AgentProfileNamespaceEntrySnapshot? Result { get; set; }

        public List<AgentProfileReference> References { get; } = [];

        public Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
            AgentProfileOwnerIdentity owner,
            string owningScopeId,
            string profileSlug,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("System bootstrap must use the canonical reference lookup.");

        public Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
            AgentProfileReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            References.Add(reference.Clone());
            return Task.FromResult(Result?.DeepClone());
        }
    }

    private sealed class RecordingManagementQueryPort : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Result { get; set; }

        public List<string> ProfileIds { get; } = [];

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }

    private sealed class RecordingExecutionQueryPort : IAgentProfileExecutionSnapshotQueryPort
    {
        public AgentProfileExecutionSnapshot? Result { get; set; }

        public List<string> ProfileIds { get; } = [];

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }

    private sealed class RecordingActorPort : IAgentProfileActorPort
    {
        public List<CreateAgentProfileCommand> CreateCommands { get; } = [];
        public List<UpdateAgentProfileDraftCommand> UpdateCommands { get; } = [];
        public List<UpsertAgentProfileSkillBindingCommand> UpsertCommands { get; } = [];
        public List<RemoveAgentProfileSkillBindingCommand> RemoveCommands { get; } = [];
        public List<PublishAgentProfileCommand> PublishCommands { get; } = [];

        public int DispatchCount =>
            CreateCommands.Count + UpdateCommands.Count + UpsertCommands.Count +
            RemoveCommands.Count + PublishCommands.Count;

        public Task<AgentProfileActorTargets> EnsureCreateTargetsAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentProfileActorTargets(
                "agent-profile-namespace",
                $"agent-profile:{profileId}"));
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            CreateAgentProfileCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, "agent-profile-namespace"));
        }

        public Task<DispatchAdmission> DispatchUpdateDraftAsync(
            UpdateAgentProfileDraftCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpdateCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, command.Identity.ProfileId));
        }

        public Task<DispatchAdmission> DispatchUpsertSkillBindingAsync(
            UpsertAgentProfileSkillBindingCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, command.Identity.ProfileId));
        }

        public Task<DispatchAdmission> DispatchRemoveSkillBindingAsync(
            RemoveAgentProfileSkillBindingCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemoveCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, command.Identity.ProfileId));
        }

        public Task<DispatchAdmission> DispatchPublishAsync(
            PublishAgentProfileCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, command.Identity.ProfileId));
        }

        private static DispatchAdmission Admission(
            AgentProfileOperationFact operation,
            string actorId) =>
            new(
                true,
                operation.CommandId,
                DateTimeOffset.Parse("2026-07-24T00:00:00Z"),
                actorId,
                operation.CorrelationId);
    }

    private sealed class RecordingTokenProvider : ISystemAgentProfileOrnnAccessTokenProvider
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

    private sealed class RecordingResolver : IExactOrnnSkillResolver
    {
        public List<string> AccessTokens { get; } = [];

        public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactOrnnSkillReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AccessTokens.Add(nyxIdAccessToken);
            return Task.FromResult(ExactOrnnSkillResolutionResult.Success(new ResolvedOrnnSkillPackage
            {
                SkillGuid = reference.SkillGuid,
                LiteralVersion = reference.LiteralVersion,
                CanonicalName = reference.ExpectedName,
                PublisherId = reference.ExpectedPublisherId,
                UpstreamSkillHash = "upstream-skill-hash-alpha",
                Description = "Exact system skill",
                Instructions = "Follow the resolved skill.",
                Arguments = "request",
                WhenToUse = "Use for the system test assistant.",
                ModelInvocable = true,
                UserInvocable = false,
            }));
        }
    }

    private sealed class EmptyToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                toolSetRef?.Name ?? string.Empty,
                "Unknown tool set.",
                []));
    }
}
