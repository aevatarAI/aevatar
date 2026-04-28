using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerCommandPortTests
{
    private const string AgentId = "skill-runner-test-1";
    private const string ExpectedPublisher = "scheduled.skill-runner";

    [Fact]
    public async Task InitializeAsync_WhenRunImmediatelyFalse_DispatchesSingleEnvelope_AndCreatesActor_AndPrimesProjection()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(null));
        fixture.Runtime.CreateAsync<SkillRunnerGAgent>(AgentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        var command = new InitializeSkillRunnerCommand
        {
            SkillName = "demo",
            ScheduleCron = "0 */1 * * *",
        };

        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: false, CancellationToken.None);

        await fixture.Runtime.Received(1).GetAsync(AgentId);
        await fixture.Runtime.Received(1).CreateAsync<SkillRunnerGAgent>(AgentId, Arg.Any<CancellationToken>());
        await fixture.Activation.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(r =>
                r.RootActorId == UserAgentCatalogGAgent.WellKnownId &&
                r.ProjectionKind == UserAgentCatalogProjectionPort.ProjectionKind),
            Arg.Any<CancellationToken>());

        fixture.Captured.Should().HaveCount(1);
        var envelope = fixture.Captured[0];
        envelope.Payload.Is(InitializeSkillRunnerCommand.Descriptor).Should().BeTrue();
        envelope.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        envelope.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task InitializeAsync_WhenRunImmediatelyTrue_DispatchesInitializeThenTrigger_WithCreateAgentReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var command = new InitializeSkillRunnerCommand { SkillName = "demo" };
        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: true, CancellationToken.None);

        fixture.Captured.Should().HaveCount(2);
        fixture.Captured[0].Payload.Is(InitializeSkillRunnerCommand.Descriptor).Should().BeTrue();
        fixture.Captured[1].Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor).Should().BeTrue();
        fixture.Captured[1].Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason.Should().Be("create_agent");
        fixture.Captured[1].Route.PublisherActorId.Should().Be(ExpectedPublisher);
        fixture.Captured[1].Route.Direct.TargetActorId.Should().Be(AgentId);

        // Actor already existed → CreateAsync should not be invoked.
        await fixture.Runtime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_DispatchesTriggerCommandWithReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.TriggerAsync(AgentId, "manual_run", CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason.Should().Be("manual_run");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task TriggerAsync_WithNullReason_NormalizesToEmptyString()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.TriggerAsync(AgentId, null!, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        fixture.Captured[0].Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason.Should().Be(string.Empty);
    }

    [Fact]
    public async Task DisableAsync_DispatchesDisableCommandWithReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(DisableSkillRunnerCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<DisableSkillRunnerCommand>().Reason.Should().Be("operator_off");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task EnableAsync_DispatchesEnableCommandWithReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.EnableAsync(AgentId, "operator_on", CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(EnableSkillRunnerCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<EnableSkillRunnerCommand>().Reason.Should().Be("operator_on");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InitializeAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var command = new InitializeSkillRunnerCommand();
        var act = () => fixture.Port.InitializeAsync(agentId!, command, runImmediately: false, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InitializeAsync_WithNullCommand_Throws()
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.InitializeAsync(AgentId, null!, runImmediately: false, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TriggerAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.TriggerAsync(agentId!, "reason", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DisableAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.DisableAsync(agentId!, "reason", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnableAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var act = () => fixture.Port.EnableAsync(agentId!, "reason", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullDependencies_Throws()
    {
        var dispatch = Substitute.For<IActorDispatchPort>();
        var runtime = Substitute.For<IActorRuntime>();
        var projection = Fixture.CreateProjectionPort(out _, out _);

        Action ctor1 = () => new SkillRunnerCommandPort(null!, dispatch, projection);
        Action ctor2 = () => new SkillRunnerCommandPort(runtime, null!, projection);
        Action ctor3 = () => new SkillRunnerCommandPort(runtime, dispatch, null!);
        ctor1.Should().Throw<ArgumentNullException>();
        ctor2.Should().Throw<ArgumentNullException>();
        ctor3.Should().Throw<ArgumentNullException>();
    }

    private sealed class Fixture
    {
        public IActorRuntime Runtime { get; }
        public IActorDispatchPort Dispatch { get; }
        public UserAgentCatalogProjectionPort Projection { get; }
        public IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> Activation { get; }
        public List<EventEnvelope> Captured { get; } = new();
        public SkillRunnerCommandPort Port { get; }

        public Fixture()
        {
            Runtime = Substitute.For<IActorRuntime>();
            Dispatch = Substitute.For<IActorDispatchPort>();
            Projection = CreateProjectionPort(out var activation, out _);
            Activation = activation;
            Dispatch.DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(env => Captured.Add(env)), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Port = new SkillRunnerCommandPort(Runtime, Dispatch, Projection);
        }

        public static UserAgentCatalogProjectionPort CreateProjectionPort(
            out IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> activation,
            out UserAgentCatalogMaterializationRuntimeLease lease)
        {
            activation = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
            lease = new UserAgentCatalogMaterializationRuntimeLease(
                new UserAgentCatalogMaterializationContext
                {
                    RootActorId = UserAgentCatalogGAgent.WellKnownId,
                    ProjectionKind = UserAgentCatalogProjectionPort.ProjectionKind,
                });
            activation.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(lease));
            return new UserAgentCatalogProjectionPort(activation);
        }
    }
}
