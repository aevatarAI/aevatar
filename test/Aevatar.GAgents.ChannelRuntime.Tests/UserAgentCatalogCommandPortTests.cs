using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
    public async Task UpsertAsync_ReturnsObserved_WhenStateVersionAdvances_AndEntryMatches()
    {
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

        // Initial state: no document; after dispatch the document materializes at version 1
        // matching the command. Issue #466: command port reads the projection document
        // directly (not via the deleted runtime query port) since this is internal infra
        // and not user-facing.
        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogDocument?>(null),
#pragma warning disable CS0612
                Task.FromResult<UserAgentCatalogDocument?>(new UserAgentCatalogDocument
                {
                    Id = agentId,
                    Platform = "lark",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    StateVersion = 1,
                }));
#pragma warning restore CS0612

        var result = await fixture.Port.UpsertAsync(command, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Observed);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<UserAgentCatalogUpsertCommand>().AgentId.Should().Be(agentId);
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
        await fixture.Dispatch.Received(1).DispatchAsync(CatalogActorId, Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        // Lifecycle: ensure GetAsync(catalogActorId) was called for actor lifecycle.
        await fixture.Runtime.Received().GetAsync(CatalogActorId);
    }

    [Fact]
    public async Task UpsertAsync_ReturnsAccepted_WhenPollingBudgetExhausts()
    {
        var fixture = new Fixture(projectionWaitAttempts: 3);
        const string agentId = "agent-upsert-stuck";
#pragma warning disable CS0612
        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = agentId,
            Platform = "lark",
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "api-key-1",
        };
#pragma warning restore CS0612

        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(null));

        var result = await fixture.Port.UpsertAsync(command, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Accepted);
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
    public async Task TombstoneAsync_ReturnsNotFound_WhenAgentDoesNotExistAtCallTime()
    {
        var fixture = new Fixture();
        const string agentId = "agent-missing";
        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(null));

        var result = await fixture.Port.TombstoneAsync(agentId, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.NotFound);
        fixture.Captured.Should().BeEmpty();
        await fixture.Dispatch.DidNotReceive().DispatchAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TombstoneAsync_ReturnsObserved_WhenDocumentVanishes()
    {
        var fixture = new Fixture();
        const string agentId = "agent-tombstone-1";

#pragma warning disable CS0612
        var existing = new UserAgentCatalogDocument { Id = agentId, Platform = "lark", StateVersion = 5 };
#pragma warning restore CS0612
        // First GetAsync (existence check) returns the document; subsequent calls (after
        // dispatch) return null — projector deleted the document on tombstone.
        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogDocument?>(existing),
                Task.FromResult<UserAgentCatalogDocument?>(null));

        var result = await fixture.Port.TombstoneAsync(agentId, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Observed);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(UserAgentCatalogTombstoneCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<UserAgentCatalogTombstoneCommand>().AgentId.Should().Be(agentId);
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(CatalogActorId);
    }

    [Fact]
    public async Task TombstoneAsync_ReturnsObserved_WhenDocumentTombstonedFlagSet()
    {
        var fixture = new Fixture();
        const string agentId = "agent-tombstone-flag";
#pragma warning disable CS0612
        var existing = new UserAgentCatalogDocument { Id = agentId, Platform = "lark", StateVersion = 5 };
        var tombstoned = new UserAgentCatalogDocument { Id = agentId, Platform = "lark", StateVersion = 6, Tombstoned = true };
#pragma warning restore CS0612
        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogDocument?>(existing),
                Task.FromResult<UserAgentCatalogDocument?>(tombstoned));

        var result = await fixture.Port.TombstoneAsync(agentId, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Observed);
    }

    [Fact]
    public async Task TombstoneAsync_ReturnsAccepted_WhenPollingBudgetExhausts()
    {
        var fixture = new Fixture(projectionWaitAttempts: 3);
        const string agentId = "agent-tombstone-stuck";
#pragma warning disable CS0612
        var existing = new UserAgentCatalogDocument { Id = agentId, Platform = "lark", StateVersion = 5 };
#pragma warning restore CS0612
        // Existence check returns the document, subsequent polls return the same (no advance, no tombstone).
        fixture.DocumentReader.GetAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(existing));

        var result = await fixture.Port.TombstoneAsync(agentId, CancellationToken.None);

        result.Outcome.Should().Be(CatalogCommandOutcome.Accepted);
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
        // No actor existed; runtime returns null then creates fresh.
        fixture.Runtime.GetAsync(CatalogActorId).Returns(Task.FromResult<IActor?>(null));
        fixture.Runtime.CreateAsync<UserAgentCatalogGAgent>(CatalogActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));
        fixture.DocumentReader.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(null));

        var command = new UserAgentCatalogUpsertCommand { AgentId = "agent-1" };
        await fixture.Port.UpsertAsync(command, CancellationToken.None);

        await fixture.Runtime.Received(1).CreateAsync<UserAgentCatalogGAgent>(CatalogActorId, Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        public IProjectionDocumentReader<UserAgentCatalogDocument, string> DocumentReader { get; }
        public UserAgentCatalogProjectionPort ProjectionPort { get; }
        public IActorRuntime Runtime { get; }
        public IActorDispatchPort Dispatch { get; }
        public List<EventEnvelope> Captured { get; } = new();
        public UserAgentCatalogCommandPort Port { get; }

        public Fixture(int projectionWaitAttempts = 3)
        {
            DocumentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
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
                DocumentReader,
                ProjectionPort,
                Runtime,
                Dispatch,
                projectionWaitAttempts: projectionWaitAttempts,
                projectionWaitDelayMilliseconds: 1);
        }
    }
}
