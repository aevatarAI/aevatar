using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
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
    public async Task UpsertAsync_DispatchesCommand_AndReturnsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Upsert polled projection documents until a matching version appeared.
        //   New principle: Upsert returns accepted after dispatch; observation is a separate query/projection concern.
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

        var result = await fixture.Port.UpsertAsync(command, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Accepted);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<UserAgentCatalogUpsertCommand>().AgentId.Should().Be(agentId);
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        await fixture.Runtime.Received().GetAsync(CatalogActorId);
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
    public async Task TombstoneAsync_DispatchesCommand_AndReturnsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Tombstone checked projection documents before dispatch and polled after dispatch.
        //   New principle: Caller-scoped existence checks happen before the port; the port only ACKs accepted dispatch.
        var fixture = new Fixture();
        const string agentId = "agent-tombstone-1";

        var result = await fixture.Port.TombstoneAsync(agentId, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Accepted);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogTombstoneCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<UserAgentCatalogTombstoneCommand>().AgentId.Should().Be(agentId);
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
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

    private sealed class Fixture
    {
        public UserAgentCatalogProjectionPort ProjectionPort { get; }
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
                        ProjectionKind = UserAgentCatalogProjectionPort.ProjectionKind,
                    })));
            ProjectionPort = new UserAgentCatalogProjectionPort(activation);

            Dispatch.DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(env => Captured.Add(env)), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Port = new UserAgentCatalogCommandPort(
                ProjectionPort,
                Runtime,
                Dispatch);
        }
    }
}
