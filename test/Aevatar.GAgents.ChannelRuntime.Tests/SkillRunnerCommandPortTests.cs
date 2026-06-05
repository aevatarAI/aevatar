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
    public async Task InitializeAsync_WhenRunImmediatelyFalse_DispatchesSingleEnvelope_AndCreatesActor_WithoutProjectionActivation()
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
        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);

        fixture.Captured.Should().HaveCount(1);
        var envelope = fixture.Captured[0];
        envelope.Payload.Is(InitializeSkillRunnerCommand.Descriptor).Should().BeTrue();
        envelope.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        envelope.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task InitializeAsync_WithSkillRef_PreservesTypedReferenceInEnvelope()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var command = new InitializeSkillRunnerCommand
        {
            SkillName = "demo",
            SkillRef = new SkillRunnerSkillReference
            {
                Name = "daily-report",
                Source = SkillRunnerSkillSource.Ornn,
                WorkflowId = "daily_flow",
            },
        };

        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: false, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var initialized = fixture.Captured[0].Payload.Unpack<InitializeSkillRunnerCommand>();
        initialized.SkillRef.Should().NotBeNull();
        initialized.SkillRef.Name.Should().Be("daily-report");
        initialized.SkillRef.Source.Should().Be(SkillRunnerSkillSource.Ornn);
        initialized.SkillRef.WorkflowId.Should().Be("daily_flow");
        initialized.SkillContent.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_WhenRunImmediatelyTrue_DispatchesInitializeThenTrigger_WithCreateAgentReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var command = new InitializeSkillRunnerCommand { SkillName = "demo" };
        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: true, CancellationToken.None);

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
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

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
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

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
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

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(EnableSkillRunnerCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<EnableSkillRunnerCommand>().Reason.Should().Be("operator_on");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
    }

    [Fact]
    public async Task AdmitExternalTriggerAsync_WhenRunnerExists_DispatchesAdmissionAndReturnsAcceptedOnlyReceipt()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var command = CreateExternalTriggerAdmission("webhook-main", "delivery-1", "admission-1");

        var receipt = await fixture.Port.AdmitExternalTriggerAsync(AgentId, command, CancellationToken.None);

        fixture.Captured.Should().ContainSingle();
        var envelope = fixture.Captured[0];
        envelope.Id.Should().Be("admission-1");
        envelope.Propagation.CorrelationId.Should().Be("admission-1");
        envelope.Payload.Is(AdmitSkillRunnerExternalTriggerCommand.Descriptor).Should().BeTrue();
        envelope.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        envelope.Route.Direct.TargetActorId.Should().Be(AgentId);
        var dispatched = envelope.Payload.Unpack<AdmitSkillRunnerExternalTriggerCommand>();
        dispatched.Identity.SourceId.Should().Be("webhook-main");
        dispatched.Identity.DeliveryId.Should().Be("delivery-1");

        receipt.ActorId.Should().Be(AgentId);
        receipt.CommandId.Should().Be("admission-1");
        receipt.CorrelationId.Should().Be("admission-1");
        receipt.AdmissionId.Should().Be("admission-1");
        receipt.SourceId.Should().Be("webhook-main");
        receipt.DeliveryId.Should().Be("delivery-1");
    }

    [Fact]
    public async Task AdmitExternalTriggerAsync_WhenRunnerMissing_ShouldThrowTypedNotFoundAndNotCreateRunner()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(null));
        var command = CreateExternalTriggerAdmission("webhook-main", "delivery-1", "admission-1");

        var act = () => fixture.Port.AdmitExternalTriggerAsync(AgentId, command, CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<SkillRunnerExternalTriggerAdmissionException>();
        assertion.Which.Error.Should().Be(SkillRunnerExternalTriggerAdmissionError.RunnerNotFound);
        assertion.Which.AgentId.Should().Be(AgentId);
        await fixture.Runtime.DidNotReceive()
            .CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Dispatch.DidNotReceive()
            .DispatchAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdmitExternalTriggerAsync_WithUnknownSource_ShouldStillReturnAcceptedDispatch()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var command = CreateExternalTriggerAdmission("unknown-source", "delivery-unknown", "admission-unknown");

        var receipt = await fixture.Port.AdmitExternalTriggerAsync(AgentId, command, CancellationToken.None);

        receipt.CommandId.Should().Be("admission-unknown");
        receipt.SourceId.Should().Be("unknown-source");
        fixture.Captured.Should().ContainSingle();
        fixture.Captured[0].Payload.Unpack<AdmitSkillRunnerExternalTriggerCommand>()
            .Identity.SourceId.Should().Be("unknown-source");
        await fixture.Runtime.DidNotReceive()
            .CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AdmitExternalTriggerAsync_WithInvalidAgentId_Throws(string? agentId)
    {
        var fixture = new Fixture();
        var command = CreateExternalTriggerAdmission("webhook-main", "delivery-1", "admission-1");

        var act = () => fixture.Port.AdmitExternalTriggerAsync(agentId!, command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AdmitExternalTriggerAsync_WithMissingAdmissionId_ShouldGenerateStableEnvelopeIdAndNormalizeIdentity()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var command = CreateExternalTriggerAdmission(" webhook-main ", " delivery-2 ", admissionId: string.Empty);

        var receipt = await fixture.Port.AdmitExternalTriggerAsync(AgentId, command, CancellationToken.None);

        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CommandId.Should().Be(receipt.CorrelationId);
        receipt.CommandId.Should().Be(receipt.AdmissionId);
        receipt.SourceId.Should().Be("webhook-main");
        receipt.DeliveryId.Should().Be("delivery-2");
        fixture.Captured.Should().ContainSingle();
        fixture.Captured[0].Id.Should().Be(receipt.CommandId);
        fixture.Captured[0].Payload.Unpack<AdmitSkillRunnerExternalTriggerCommand>()
            .Identity.AdmissionId.Should().Be(receipt.CommandId);
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

        Action ctor1 = () => new SkillRunnerCommandPort(null!, dispatch);
        Action ctor2 = () => new SkillRunnerCommandPort(runtime, null!);
        ctor1.Should().Throw<ArgumentNullException>();
        ctor2.Should().Throw<ArgumentNullException>();
    }

    private static AdmitSkillRunnerExternalTriggerCommand CreateExternalTriggerAdmission(
        string sourceId,
        string deliveryId,
        string? admissionId) => new()
    {
        Identity = new SkillRunnerExternalTriggerIdentity
        {
            SourceId = sourceId,
            DeliveryId = deliveryId,
            AdmissionId = admissionId ?? string.Empty,
            Kind = ExternalTriggerSourceKind.Webhook,
        },
    };

    private sealed class Fixture
    {
        public IActorRuntime Runtime { get; }
        public IActorDispatchPort Dispatch { get; }
        public UserAgentCatalogProjectionBootstrapActivator Projection { get; }
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
                .Returns(call => Task.FromResult(DispatchAdmissionFactory.Create(call.ArgAt<string>(0), call.ArgAt<EventEnvelope>(1))));
            Port = new SkillRunnerCommandPort(Runtime, Dispatch);
        }

        public static UserAgentCatalogProjectionBootstrapActivator CreateProjectionPort(
            out IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> activation,
            out UserAgentCatalogMaterializationRuntimeLease lease)
        {
            activation = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
            lease = new UserAgentCatalogMaterializationRuntimeLease(
                new UserAgentCatalogMaterializationContext
                {
                    RootActorId = UserAgentCatalogGAgent.WellKnownId,
                    ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
                });
            activation.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(lease));
            return new UserAgentCatalogProjectionBootstrapActivator(activation);
        }
    }
}
