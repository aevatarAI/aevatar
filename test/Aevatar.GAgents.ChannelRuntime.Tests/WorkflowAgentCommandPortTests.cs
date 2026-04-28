using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowAgentCommandPortTests
{
    private const string AgentId = "workflow-agent-test-1";
    private const string ExpectedPublisher = "scheduled.workflow-agent";

    [Fact]
    public async Task InitializeAsync_WhenRunImmediatelyFalse_DispatchesSingleEnvelope_AndCreatesActor_AndPrimesProjection()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(null));
        fixture.Runtime.CreateAsync<WorkflowAgentGAgent>(AgentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IActor>()));

        var command = new InitializeWorkflowAgentCommand
        {
            WorkflowId = "wf-1",
            WorkflowName = "demo",
            ExecutionPrompt = "do the thing",
            ScheduleCron = "0 */1 * * *",
        };

        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: false, CancellationToken.None);

        await fixture.Runtime.Received(1).GetAsync(AgentId);
        await fixture.Runtime.Received(1).CreateAsync<WorkflowAgentGAgent>(AgentId, Arg.Any<CancellationToken>());
        await fixture.Activation.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(r =>
                r.RootActorId == UserAgentCatalogGAgent.WellKnownId &&
                r.ProjectionKind == UserAgentCatalogProjectionPort.ProjectionKind),
            Arg.Any<CancellationToken>());

        fixture.Captured.Should().HaveCount(1);
        var envelope = fixture.Captured[0];
        envelope.Payload.Is(InitializeWorkflowAgentCommand.Descriptor).Should().BeTrue();
        envelope.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        envelope.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task InitializeAsync_WhenRunImmediatelyTrue_DispatchesInitializeThenTrigger_WithCreateAgentReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var command = new InitializeWorkflowAgentCommand { WorkflowId = "wf-1" };
        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: true, CancellationToken.None);

        fixture.Captured.Should().HaveCount(2);
        fixture.Captured[0].Payload.Is(InitializeWorkflowAgentCommand.Descriptor).Should().BeTrue();
        fixture.Captured[1].Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor).Should().BeTrue();
        var trigger = fixture.Captured[1].Payload.Unpack<TriggerWorkflowAgentExecutionCommand>();
        trigger.Reason.Should().Be("create_agent");
        trigger.RevisionFeedback.Should().Be(string.Empty);
        fixture.Captured[1].Route.PublisherActorId.Should().Be(ExpectedPublisher);
        fixture.Captured[1].Route.Direct.TargetActorId.Should().Be(AgentId);

        await fixture.Runtime.DidNotReceive().CreateAsync<WorkflowAgentGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_DispatchesTriggerCommand_WithReasonAndRevisionFeedback()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.TriggerAsync(AgentId, "operator_run", "tighten the prompt", CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor).Should().BeTrue();
        var trigger = env.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>();
        trigger.Reason.Should().Be("operator_run");
        trigger.RevisionFeedback.Should().Be("tighten the prompt");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task TriggerAsync_WithNullArguments_NormalizesToEmptyString()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.TriggerAsync(AgentId, null!, null, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var trigger = fixture.Captured[0].Payload.Unpack<TriggerWorkflowAgentExecutionCommand>();
        trigger.Reason.Should().Be(string.Empty);
        trigger.RevisionFeedback.Should().Be(string.Empty);
    }

    [Fact]
    public async Task DisableAsync_DispatchesDisableCommandWithReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(DisableWorkflowAgentCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<DisableWorkflowAgentCommand>().Reason.Should().Be("operator_off");
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
        env.Payload.Is(EnableWorkflowAgentCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<EnableWorkflowAgentCommand>().Reason.Should().Be("operator_on");
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
        var command = new InitializeWorkflowAgentCommand();
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
        var act = () => fixture.Port.TriggerAsync(agentId!, "reason", null, CancellationToken.None);
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
        var projection = Fixture.CreateProjectionPort(out _);

        Action ctor1 = () => new WorkflowAgentCommandPort(null!, dispatch, projection);
        Action ctor2 = () => new WorkflowAgentCommandPort(runtime, null!, projection);
        Action ctor3 = () => new WorkflowAgentCommandPort(runtime, dispatch, null!);
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
        public WorkflowAgentCommandPort Port { get; }

        public Fixture()
        {
            Runtime = Substitute.For<IActorRuntime>();
            Dispatch = Substitute.For<IActorDispatchPort>();
            Projection = CreateProjectionPort(out var activation);
            Activation = activation;
            Dispatch.DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(env => Captured.Add(env)), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Port = new WorkflowAgentCommandPort(Runtime, Dispatch, Projection);
        }

        public static UserAgentCatalogProjectionPort CreateProjectionPort(
            out IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> activation)
        {
            activation = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
            var lease = new UserAgentCatalogMaterializationRuntimeLease(
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
