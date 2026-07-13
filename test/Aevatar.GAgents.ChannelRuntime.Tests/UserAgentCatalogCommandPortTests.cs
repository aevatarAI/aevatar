using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCommandPortTests
{
    private const string CatalogActorId = UserAgentCatalogGAgent.WellKnownId;
    private const string ExpectedPublisher = "scheduled.user-agent-catalog";

    [Fact]
    public async Task UpsertAsync_DispatchesCommand_AndCompletes()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Upsert polled projection documents until a matching version appeared.
        //   New principle: Upsert returns accepted after dispatch; observation is a separate query/projection concern.
        // Refactor (iter5/cluster-012):
        //   Old pattern: Upsert asserted a single-value accepted result enum.
        //   New principle: Upsert completion plus dispatch capture is the command-port contract.
        var fixture = new Fixture();
        const string agentId = "agent-upsert-1";
#pragma warning disable CS0612 // legacy fields kept on the command for rollback safety during owner_scope migration
        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = agentId,
            Platform = "lark",
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "api-key-1",
        };
#pragma warning restore CS0612

        await fixture.Port.UpsertAsync(command, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<UserAgentCatalogUpsertCommand>().AgentId.Should().Be(agentId);
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        await fixture.Runtime.Received().GetAsync(CatalogActorId);
        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
    }

    [Fact]
    public async Task UpsertAsync_WithNullCommand_Throws()
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.UpsertAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpsertAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var command = new UserAgentCatalogUpsertCommand { AgentId = agentId ?? string.Empty };
        var act = () => fixture.Port.UpsertAsync(command, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TombstoneAsync_DispatchesCommand_AndCompletes()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Tombstone checked projection documents before dispatch and polled after dispatch.
        //   New principle: Caller-scoped existence checks happen before the port; the port only ACKs accepted dispatch.
        // Refactor (iter5/cluster-012):
        //   Old pattern: Tombstone asserted a single-value accepted result enum.
        //   New principle: Tombstone completion plus dispatch capture is the command-port contract.
        var fixture = new Fixture();
        const string agentId = "agent-tombstone-1";

        await fixture.Port.TombstoneAsync(agentId, CancellationToken.None, " bearer-token ");

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogTombstoneCommand.Descriptor).Should().BeTrue();
        var command = env.Payload.Unpack<UserAgentCatalogTombstoneCommand>();
        command.AgentId.Should().Be(agentId);
        command.BearerToken.Should().Be("bearer-token");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TombstoneAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.TombstoneAsync(agentId!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordApiKeyRevocationAttemptAsync_DispatchesCommand_AndCompletes()
    {
        var fixture = new Fixture();

        await fixture.Port.RecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = "agent-1",
                ApiKeyId = "key-1",
                Completed = false,
                HttpStatus = 503,
                Error = "upstream unavailable",
                FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
            },
            CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var command = fixture.Captured[0].Payload.Unpack<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>();
        command.AgentId.Should().Be("agent-1");
        command.ApiKeyId.Should().Be("key-1");
        command.Completed.Should().BeFalse();
        command.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        fixture.Captured[0].Route.PublisherActorId.Should().Be(ExpectedPublisher);
        fixture.Captured[0].Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordApiKeyRevocationAttemptAsync_WithNullCommand_Throws()
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.RecordApiKeyRevocationAttemptAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCredentialRevocationAsync_DispatchesClonedIntentWithTrimmedBearer()
    {
        var fixture = new Fixture();
        var owner = OwnerScope.ForNyxIdNative("user-revoke-1");
        var intent = new ScheduledAgentCredentialRevocationIntent
        {
            AgentId = "agent-revoke-1",
            ApiKeyId = "key-revoke-1",
            OwnerScope = owner,
            NyxApiKeyReference = new SecretReference
            {
                Ref = "sec-revoke-1",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = "owner-revoke-1",
                Version = 1,
                Fingerprint = "sha256:test",
            },
            VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
            {
                Ref = "sec-revoke-1",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = "owner-revoke-1",
                SubjectId = "key-revoke-1",
                ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.Confirmed,
            },
        };

        await fixture.Port.RequestCredentialRevocationAsync(
            intent,
            CancellationToken.None,
            " bearer-token ");
        intent.AgentId = "mutated-after-dispatch";

        fixture.Captured.Should().ContainSingle();
        var envelope = fixture.Captured[0];
        envelope.Payload.Is(UserAgentCatalogRequestCredentialRevocationCommand.Descriptor).Should().BeTrue();
        var command = envelope.Payload.Unpack<UserAgentCatalogRequestCredentialRevocationCommand>();
        command.BearerToken.Should().Be("bearer-token");
        command.Intent.AgentId.Should().Be("agent-revoke-1");
        command.Intent.ApiKeyId.Should().Be("key-revoke-1");
        command.Intent.OwnerScope.MatchesStrictly(owner).Should().BeTrue();
        command.Intent.NyxApiKeyReference.Ref.Should().Be("sec-revoke-1");
        command.Intent.VaultRevocationDescriptor.ReferenceAvailability.Should()
            .Be(ScheduledCredentialVaultReferenceAvailability.Confirmed);
        envelope.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        envelope.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(
            CatalogActorId,
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryCredentialRevocationsAsync_DispatchesOwnerScopedBearerCommand()
    {
        var fixture = new Fixture();
        var owner = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");

        await fixture.Port.RetryCredentialRevocationsAsync(
            owner,
            " bearer-token ",
            CancellationToken.None);

        var envelope = fixture.Captured.Should().ContainSingle().Subject;
        envelope.Payload.Is(UserAgentCatalogRetryCredentialRevocationsCommand.Descriptor).Should().BeTrue();
        var command = envelope.Payload.Unpack<UserAgentCatalogRetryCredentialRevocationsCommand>();
        command.OwnerScope.MatchesStrictly(owner).Should().BeTrue();
        command.BearerToken.Should().Be("bearer-token");
        envelope.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
    }

    [Theory]
    [InlineData(null, "key-1")]
    [InlineData("", "key-1")]
    [InlineData("   ", "key-1")]
    [InlineData("agent-1", null)]
    [InlineData("agent-1", "")]
    [InlineData("agent-1", "   ")]
    public async Task RecordApiKeyRevocationAttemptAsync_WithInvalidIds_Throws(
        string? agentId,
        string? apiKeyId)
    {
        var fixture = new Fixture();
        var command = new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
        {
            AgentId = agentId ?? string.Empty,
            ApiKeyId = apiKeyId ?? string.Empty,
            Completed = false,
            FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
        };

        var act = () => fixture.Port.RecordApiKeyRevocationAttemptAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        fixture.Captured.Should().BeEmpty();
        await fixture.Dispatch.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task ShareAsync_DispatchesCommand_AndCompletes()
    {
        var fixture = new Fixture();
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");

        await fixture.Port.ShareAsync("agent-shared", owner, allowTrigger: true, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var command = fixture.Captured[0].Payload.Unpack<UserAgentCatalogShareCommand>();
        command.AgentId.Should().Be("agent-shared");
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        command.AllowTrigger.Should().BeTrue();
        fixture.Captured[0].Route.PublisherActorId.Should().Be(ExpectedPublisher);
        fixture.Captured[0].Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnshareAsync_DispatchesCommand_AndCompletes()
    {
        var fixture = new Fixture();
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");

        await fixture.Port.UnshareAsync("agent-shared", owner, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var command = fixture.Captured[0].Payload.Unpack<UserAgentCatalogUnshareCommand>();
        command.AgentId.Should().Be("agent-shared");
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        fixture.Captured[0].Route.PublisherActorId.Should().Be(ExpectedPublisher);
        fixture.Captured[0].Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShareAsync_WithNullOwnerScope_Throws()
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.ShareAsync("agent-shared", null!, allowTrigger: true, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UpsertAsync_EnsuresCatalogActorLifecycle_WhenActorMissing()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(CatalogActorId).Returns(Task.FromResult<IActor?>(null));
        fixture.Runtime.CreateAsync<UserAgentCatalogGAgent>(CatalogActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        var command = new UserAgentCatalogUpsertCommand { AgentId = "agent-1" };
        await fixture.Port.UpsertAsync(command, CancellationToken.None);

        await fixture.Runtime.Received(1).CreateAsync<UserAgentCatalogGAgent>(CatalogActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProductionSource_ShouldNotContainProjectionDocumentPolling()
    {
        var source = File.ReadAllText(GetProductionSourcePath());

        source.Should().NotContain("IProjectionDocumentReader");
        source.Should().NotContain(string.Concat("Task", ".Delay"));
        source.Should().NotContain("projectionWait");
        source.Should().NotContain("StateVersion");
        source.Should().NotContain("EnsureProjectionForActorAsync");
    }

    [Fact]
    public void PortSource_ShouldNotContainAcceptedOnlyResultAbstractions()
    {
        // Refactor (iter5/cluster-012):
        //   Old pattern: Source kept single-value result abstractions after observed outcomes were removed.
        //   New principle: Command-port source exposes Task-only mutation methods.
        var interfaceSource = File.ReadAllText(GetInterfaceSourcePath());
        var productionSource = File.ReadAllText(GetProductionSourcePath());
        var combinedSource = interfaceSource + productionSource;

        combinedSource.Should().NotContain("Catalog" + "CommandOutcome");
        combinedSource.Should().NotContain("UserAgentCatalog" + "UpsertResult");
        combinedSource.Should().NotContain("UserAgentCatalog" + "TombstoneResult");
    }

    private static string GetProductionSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "agents",
                "Aevatar.GAgents.Scheduled",
                "UserAgentCatalogCommandPort.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate UserAgentCatalogCommandPort.cs from test output directory.");
    }

    private static string GetInterfaceSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "agents",
                "Aevatar.GAgents.Scheduled",
                "IUserAgentCatalogCommandPort.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate IUserAgentCatalogCommandPort.cs from test output directory.");
    }

    private sealed class Fixture
    {
        public UserAgentCatalogProjectionBootstrapActivator ProjectionPort { get; }
        public IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> Activation { get; }
        public IActorRuntime Runtime { get; }
        public IActorDispatchPort Dispatch { get; }
        public List<EventEnvelope> Captured { get; } = new();
        public UserAgentCatalogCommandPort Port { get; }

        public Fixture()
        {
            Runtime = Substitute.For<IActorRuntime>();
            Dispatch = Substitute.For<IActorDispatchPort>();

            var activation = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
            activation.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new UserAgentCatalogMaterializationRuntimeLease(
                    new UserAgentCatalogMaterializationContext
                    {
                        RootActorId = UserAgentCatalogGAgent.WellKnownId,
                        ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
                    })));
            ProjectionPort = new UserAgentCatalogProjectionBootstrapActivator(activation);
            Activation = activation;

            Dispatch.DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(env => Captured.Add(env)), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(DispatchAdmissionFactory.Create(call.ArgAt<string>(0), call.ArgAt<EventEnvelope>(1))));

            Port = new UserAgentCatalogCommandPort(
                Runtime,
                Dispatch);
        }
    }
}
