using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
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
        fixture.CronSchedule.Ensured.Should().ContainSingle();
    }

    [Fact]
    public async Task InitializeAsync_WhenCron_ShouldEnsureScheduledDispatch()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var command = new InitializeSkillRunnerCommand
        {
            SkillName = "demo",
            ScheduleMode = SkillRunnerScheduleMode.Cron,
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "UTC",
            Enabled = true,
        };

        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: false, CancellationToken.None);

        fixture.CronSchedule.Ensured.Should().ContainSingle();
        fixture.CronSchedule.Ensured[0].AgentId.Should().Be(AgentId);
        fixture.CronSchedule.Ensured[0].Command.Should().BeSameAs(command);
    }

    [Fact]
    public async Task InitializeAsync_WhenOneShot_ShouldNotEnsureScheduledDispatch()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var command = new InitializeSkillRunnerCommand
        {
            SkillName = "reminder",
            ScheduleMode = SkillRunnerScheduleMode.OneShot,
            OneShotRunAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddMinutes(10)),
            OneShotMessage = "ship it",
        };

        await fixture.Port.InitializeAsync(AgentId, command, runImmediately: false, CancellationToken.None);

        fixture.CronSchedule.Ensured.Should().BeEmpty();
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
        fixture.CronSchedule.Ensured.Should().ContainSingle();

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
        fixture.ExecutionDocument = new SkillRunnerExecutionDocument { ScheduleMode = SkillRunnerScheduleMode.Cron };

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(DisableSkillRunnerCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<DisableSkillRunnerCommand>().Reason.Should().Be("operator_off");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
        fixture.CronSchedule.Disabled.Should().ContainSingle().Which
            .Should().Be((AgentId, "operator_off"));
    }

    [Fact]
    public async Task DisableAsync_WhenReadModelIsOneShot_ShouldStillDisableDeterministicScheduledDispatch()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        fixture.ExecutionDocument = new SkillRunnerExecutionDocument { ScheduleMode = SkillRunnerScheduleMode.OneShot };

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        fixture.CronSchedule.Disabled.Should().ContainSingle().Which
            .Should().Be((AgentId, "operator_off"));
    }

    [Fact]
    public async Task DisableAsync_WhenScheduledDispatchMissing_ShouldStillDisableRunner()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        fixture.CronSchedule.DisableException = new ScheduledDispatchNotFoundException(
            SkillRunnerCronSchedulePort.BuildScheduleId(AgentId));

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        fixture.CronSchedule.Disabled.Should().ContainSingle().Which
            .Should().Be((AgentId, "operator_off"));
        fixture.Captured.Should().ContainSingle();
        fixture.Captured[0].Payload.Is(DisableSkillRunnerCommand.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task DisableAsync_ShouldDisableScheduledDispatchBeforeRunnerCommand()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        await fixture.Port.DisableAsync(AgentId, "operator_off", CancellationToken.None);

        fixture.Operations.Should().Equal("cron:disable", "dispatch:disable");
    }

    [Fact]
    public async Task EnableAsync_DispatchesEnableCommandWithReason()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        fixture.ExecutionDocument = new SkillRunnerExecutionDocument { ScheduleMode = SkillRunnerScheduleMode.Cron };

        await fixture.Port.EnableAsync(AgentId, "operator_on", CancellationToken.None);

        await fixture.Activation.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        fixture.Captured.Should().ContainSingle();
        var env = fixture.Captured[0];
        env.Payload.Is(EnableSkillRunnerCommand.Descriptor).Should().BeTrue();
        env.Payload.Unpack<EnableSkillRunnerCommand>().Reason.Should().Be("operator_on");
        env.Route.PublisherActorId.Should().Be(ExpectedPublisher);
        env.Route.Direct.TargetActorId.Should().Be(AgentId);
        fixture.CronSchedule.Enabled.Should().ContainSingle().Which
            .Should().Be((AgentId, "operator_on"));
    }

    [Fact]
    public async Task EnableAsync_WhenOneShot_ShouldNotEnableScheduledDispatch()
    {
        var fixture = new Fixture();
        fixture.Runtime.GetAsync(AgentId).Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        fixture.ExecutionDocument = new SkillRunnerExecutionDocument { ScheduleMode = SkillRunnerScheduleMode.OneShot };

        await fixture.Port.EnableAsync(AgentId, "operator_on", CancellationToken.None);

        fixture.CronSchedule.Enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task CronSchedulePort_EnsureAsync_ShouldBuildSkillRunnerScheduledDispatchEnvelope()
    {
        var scheduledDispatch = new RecordingScheduledDispatchApplicationService();
        var port = new SkillRunnerCronSchedulePort(scheduledDispatch);
        var command = new InitializeSkillRunnerCommand
        {
            SkillName = "daily report",
            ScheduleMode = SkillRunnerScheduleMode.Cron,
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "Asia/Singapore",
            Enabled = true,
        };

        await port.EnsureAsync(AgentId, command, CancellationToken.None);

        var configuration = scheduledDispatch.Ensured.Should().ContainSingle().Subject;
        configuration.ScheduleId.Should().Be($"skill-runner.{AgentId}");
        configuration.CronExpression.Should().Be("0 9 * * *");
        configuration.Timezone.Should().Be("Asia/Singapore");
        configuration.Enabled.Should().BeTrue();
        configuration.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.SkillRunner);
        configuration.Target.Kind.Should().Be(ScheduledDispatchTargetKind.Envelope);
        configuration.Target.ActorId.Should().Be(AgentId);
        configuration.Target.Envelope.Should().NotBeNull();
        configuration.Target.Envelope!.Route.Direct.TargetActorId.Should().Be(AgentId);
        var trigger = configuration.Target.Envelope.Payload.Unpack<TriggerSkillRunnerExecutionCommand>();
        trigger.Reason.Should().Be("schedule");
    }

    [Fact]
    public void BuildScheduleId_ShouldStayWithinScheduledDispatchAllowedCharacters()
    {
        // Regression: scheduled dispatch reserves ':' as the actor-id namespace delimiter and
        // its NormalizeScheduleId only allows [A-Za-z0-9._-]. A ':' here previously made every
        // SkillRunner cron creation fail (ArgumentException -> initialize_failed). The unit test
        // above mocks IScheduledDispatchApplicationService, so it cannot catch this on its own.
        var scheduleId = SkillRunnerCronSchedulePort.BuildScheduleId(AgentId);

        scheduleId.Should().Be($"skill-runner.{AgentId}");
        scheduleId.Should().NotContain(":");
        scheduleId.Should().MatchRegex("^[A-Za-z0-9._-]+$");
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

        var cronSchedule = new RecordingSkillRunnerCronSchedulePort();
        var executionQuery = Substitute.For<ISkillRunnerExecutionQueryPort>();

        Action ctor1 = () => new SkillRunnerCommandPort(null!, dispatch, cronSchedule, executionQuery);
        Action ctor2 = () => new SkillRunnerCommandPort(runtime, null!, cronSchedule, executionQuery);
        Action ctor3 = () => new SkillRunnerCommandPort(runtime, dispatch, null!, Substitute.For<ISkillRunnerExecutionQueryPort>());
        Action ctor4 = () => new SkillRunnerCommandPort(runtime, dispatch, cronSchedule, null!);
        ctor1.Should().Throw<ArgumentNullException>();
        ctor2.Should().Throw<ArgumentNullException>();
        ctor3.Should().Throw<ArgumentNullException>();
        ctor4.Should().Throw<ArgumentNullException>();
    }

    private sealed class Fixture
    {
        public IActorRuntime Runtime { get; }
        public IActorDispatchPort Dispatch { get; }
        public UserAgentCatalogProjectionBootstrapActivator Projection { get; }
        public IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> Activation { get; }
        public RecordingSkillRunnerCronSchedulePort CronSchedule { get; }
        public ISkillRunnerExecutionQueryPort ExecutionQuery { get; }
        public List<EventEnvelope> Captured { get; } = new();
        public List<string> Operations { get; } = [];
        public SkillRunnerExecutionDocument? ExecutionDocument { get; set; }
        public SkillRunnerCommandPort Port { get; }

        public Fixture()
        {
            Runtime = Substitute.For<IActorRuntime>();
            Dispatch = Substitute.For<IActorDispatchPort>();
            Projection = CreateProjectionPort(out var activation, out _);
            Activation = activation;
            CronSchedule = new RecordingSkillRunnerCronSchedulePort(Operations);
            ExecutionQuery = Substitute.For<ISkillRunnerExecutionQueryPort>();
            ExecutionQuery.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(ExecutionDocument));
            Dispatch.DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(env =>
                {
                    Captured.Add(env);
                    Operations.Add($"dispatch:{ResolvePayloadOperation(env)}");
                }), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(DispatchAdmissionFactory.Create(call.ArgAt<string>(0), call.ArgAt<EventEnvelope>(1))));
            Port = new SkillRunnerCommandPort(Runtime, Dispatch, CronSchedule, ExecutionQuery);
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

        private static string ResolvePayloadOperation(EventEnvelope envelope)
        {
            if (envelope.Payload.Is(DisableSkillRunnerCommand.Descriptor))
                return "disable";
            if (envelope.Payload.Is(EnableSkillRunnerCommand.Descriptor))
                return "enable";
            if (envelope.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor))
                return "trigger";
            if (envelope.Payload.Is(InitializeSkillRunnerCommand.Descriptor))
                return "initialize";
            return "unknown";
        }
    }

    private sealed class RecordingSkillRunnerCronSchedulePort : ISkillRunnerCronSchedulePort
    {
        private readonly List<string> _operations;

        public RecordingSkillRunnerCronSchedulePort(List<string>? operations = null)
        {
            _operations = operations ?? [];
        }

        public List<(string AgentId, InitializeSkillRunnerCommand Command)> Ensured { get; } = [];
        public List<(string AgentId, string Reason)> Enabled { get; } = [];
        public List<(string AgentId, string Reason)> Disabled { get; } = [];
        public Exception? DisableException { get; set; }

        public Task EnsureAsync(
            string agentId,
            InitializeSkillRunnerCommand command,
            CancellationToken ct = default)
        {
            if (command.ScheduleMode != SkillRunnerScheduleMode.OneShot)
                Ensured.Add((agentId, command));
            return Task.CompletedTask;
        }

        public Task EnableAsync(string agentId, string reason, CancellationToken ct = default)
        {
            _operations.Add("cron:enable");
            Enabled.Add((agentId, reason));
            return Task.CompletedTask;
        }

        public Task DisableAsync(string agentId, string reason, CancellationToken ct = default)
        {
            _operations.Add("cron:disable");
            Disabled.Add((agentId, reason));
            if (DisableException is not null)
                return Task.FromException(DisableException);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScheduledDispatchApplicationService : IScheduledDispatchApplicationService
    {
        public List<ScheduledDispatchConfiguration> Ensured { get; } = [];
        public List<(string ScheduleId, string Reason)> Enabled { get; } = [];
        public List<(string ScheduleId, string Reason)> Disabled { get; } = [];

        public Task<ScheduledDispatchMutationReceipt> CreateAsync(
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> EnsureAsync(
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default)
        {
            Ensured.Add(configuration);
            return Task.FromResult(CreateReceipt(configuration.ScheduleId));
        }

        public Task<ScheduledDispatchMutationReceipt> UpdateAsync(
            string scheduleId,
            ScheduledDispatchConfiguration configuration,
            ScheduledDispatchMutationContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((scheduleId, reason));
            return Task.FromResult(CreateReceipt(scheduleId));
        }

        public Task<ScheduledDispatchMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((scheduleId, reason));
            return Task.FromResult(CreateReceipt(scheduleId));
        }

        public Task<ScheduledDispatchMutationReceipt> DeleteAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchPreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScheduledDispatchRunNowReceipt> RunNowAsync(string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static ScheduledDispatchMutationReceipt CreateReceipt(string scheduleId) =>
            new(scheduleId, $"scheduled-dispatch:{scheduleId}", true, "cmd", "corr", DateTimeOffset.UtcNow, "accepted");
    }
}
