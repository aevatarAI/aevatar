using System.Reflection;
using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunControlAndAbstractionsCoverageTests
{
    public static TheoryData<WorkflowRunEventEnvelope, string> EventTypeCases =>
        new()
        {
            { new WorkflowRunEventEnvelope { RunStarted = new WorkflowRunStartedEventPayload() }, WorkflowRunEventTypes.RunStarted },
            { new WorkflowRunEventEnvelope { RunFinished = new WorkflowRunFinishedEventPayload() }, WorkflowRunEventTypes.RunFinished },
            { new WorkflowRunEventEnvelope { RunError = new WorkflowRunErrorEventPayload() }, WorkflowRunEventTypes.RunError },
            { new WorkflowRunEventEnvelope { RunStopped = new WorkflowRunStoppedEventPayload() }, WorkflowRunEventTypes.RunStopped },
            { new WorkflowRunEventEnvelope { StepStarted = new WorkflowStepStartedEventPayload() }, WorkflowRunEventTypes.StepStarted },
            { new WorkflowRunEventEnvelope { StepFinished = new WorkflowStepFinishedEventPayload() }, WorkflowRunEventTypes.StepFinished },
            { new WorkflowRunEventEnvelope { TextMessageStart = new WorkflowTextMessageStartEventPayload() }, WorkflowRunEventTypes.TextMessageStart },
            { new WorkflowRunEventEnvelope { TextMessageContent = new WorkflowTextMessageContentEventPayload() }, WorkflowRunEventTypes.TextMessageContent },
            { new WorkflowRunEventEnvelope { TextMessageEnd = new WorkflowTextMessageEndEventPayload() }, WorkflowRunEventTypes.TextMessageEnd },
            { new WorkflowRunEventEnvelope { StateSnapshot = new WorkflowStateSnapshotEventPayload() }, WorkflowRunEventTypes.StateSnapshot },
            { new WorkflowRunEventEnvelope { ToolCallStart = new WorkflowToolCallStartEventPayload() }, WorkflowRunEventTypes.ToolCallStart },
            { new WorkflowRunEventEnvelope { ToolCallEnd = new WorkflowToolCallEndEventPayload() }, WorkflowRunEventTypes.ToolCallEnd },
            { new WorkflowRunEventEnvelope { Custom = new WorkflowCustomEventPayload() }, WorkflowRunEventTypes.Custom },
            { new WorkflowRunEventEnvelope(), string.Empty },
        };

    [Theory]
    [MemberData(nameof(EventTypeCases))]
    public void WorkflowRunEventTypes_ShouldMapAllKnownEventCases(
        WorkflowRunEventEnvelope envelope,
        string expected)
    {
        WorkflowRunEventTypes.GetEventType(envelope).Should().Be(expected);
    }

    [Fact]
    public void WorkflowRunEventTypes_ShouldThrow_WhenEnvelopeIsNull()
    {
        var act = () => WorkflowRunEventTypes.GetEventType(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WorkflowRunControlStartError_FactoryMethods_ShouldNormalizeNullInputs()
    {
        WorkflowRunControlStartError.InvalidActorId(null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.InvalidActorId,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.InvalidRunId(null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.InvalidRunId,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.ActorNotFound(null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.ActorNotFound,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.ActorNotWorkflowRun(null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.ActorNotWorkflowRun,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.RunBindingMissing(null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.RunBindingMissing,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.RunBindingMismatch(null!, null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.RunBindingMismatch,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.InvalidStepId(null!, null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.InvalidStepId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
        WorkflowRunControlStartError.InvalidSignalName(null!, null!, null!)
            .Should().Be(new WorkflowRunControlStartError(
                WorkflowRunControlStartErrorCode.InvalidSignalName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
    }

    [Fact]
    public void WorkflowRunControlCommandBase_ShouldKeepCorrelationIdExplicit()
    {
        var resume = new WorkflowResumeCommand("actor-1", "run-1", "step-1", "cmd-1", true, "approved");
        var signal = new WorkflowSignalCommand("actor-1", "run-1", "approve", "cmd-2", "payload");
        var stop = new WorkflowStopCommand("actor-1", "run-1", "cmd-3", "stop");

        resume.CorrelationId.Should().BeNull();
        signal.CorrelationId.Should().BeNull();
        stop.CorrelationId.Should().BeNull();

        var explicitResume = new WorkflowResumeCommand(
            "actor-1",
            "run-1",
            "step-1",
            "cmd-4",
            true,
            "approved",
            CorrelationId: "corr-4");
        var explicitSignal = new WorkflowSignalCommand(
            "actor-1",
            "run-1",
            "approve",
            "cmd-5",
            "payload",
            CorrelationId: "corr-5");
        var explicitStop = new WorkflowStopCommand(
            "actor-1",
            "run-1",
            "cmd-6",
            "stop",
            CorrelationId: "corr-6");

        // Refactor (issue1326): Control command correlation is caller-provided context, not derived from command id.
        explicitResume.CorrelationId.Should().Be("corr-4");
        explicitSignal.CorrelationId.Should().Be("corr-5");
        explicitStop.CorrelationId.Should().Be("corr-6");
        resume.Headers.Should().BeNull();
        signal.Headers.Should().BeNull();
        stop.Headers.Should().BeNull();
    }

    [Fact]
    public void WorkflowYamlParseResult_ShouldReportSuccessAndNormalizeInvalidError()
    {
        var success = WorkflowYamlParseResult.Success("workflow-1");
        var invalid = WorkflowYamlParseResult.Invalid(null!);
        var whitespace = new WorkflowYamlParseResult("workflow-1", " ");

        success.Succeeded.Should().BeTrue();
        success.WorkflowName.Should().Be("workflow-1");
        invalid.Succeeded.Should().BeFalse();
        invalid.Error.Should().Be("Workflow YAML is invalid.");
        whitespace.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void WorkflowActorBinding_ShouldExposeDerivedProperties()
    {
        var unsupported = WorkflowActorBinding.Unsupported(null!);
        var definition = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "definition-1",
            string.Empty,
            string.Empty,
            "auto",
            "name: auto",
            new Dictionary<string, string>(), ExternalCapabilityExecutionMode.Interactive);
        var run = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            "run-1",
            "definition-1",
            "run-1",
            "auto",
            string.Empty,
            new Dictionary<string, string>
            {
                ["helper"] = "name: helper",
            },
            ExternalCapabilityExecutionMode.Interactive);

        unsupported.IsWorkflowCapable.Should().BeFalse();
        unsupported.HasWorkflowName.Should().BeFalse();
        unsupported.HasDefinitionPayload.Should().BeFalse();
        unsupported.ActorId.Should().BeEmpty();
        unsupported.EffectiveDefinitionActorId.Should().BeEmpty();

        definition.IsWorkflowCapable.Should().BeTrue();
        definition.HasWorkflowName.Should().BeTrue();
        definition.HasDefinitionPayload.Should().BeTrue();
        definition.EffectiveDefinitionActorId.Should().Be("definition-1");

        run.HasDefinitionPayload.Should().BeTrue();
        run.EffectiveDefinitionActorId.Should().Be("definition-1");
    }

    [Fact]
    public void WorkflowRunStepTrace_DurationMs_ShouldClampNegativeAndReturnNullWhenIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var negative = new WorkflowRunStepTrace
        {
            RequestedAt = now,
            CompletedAt = now.AddSeconds(-1),
        };
        var incomplete = new WorkflowRunStepTrace
        {
            RequestedAt = now,
        };
        var positive = new WorkflowRunStepTrace
        {
            RequestedAt = now,
            CompletedAt = now.AddMilliseconds(250),
        };

        negative.DurationMs.Should().Be(0);
        incomplete.DurationMs.Should().BeNull();
        positive.DurationMs.Should().BeApproximately(250, 0.5);
    }

    [Fact]
    public void WorkflowDefinitionActorId_Format_ShouldNormalizeOrThrow()
    {
        WorkflowDefinitionActorId.Format(" Auto_Workflow ").Should().Be("workflow-definition:auto_workflow");

        var act = () => WorkflowDefinitionActorId.Format(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowRunControlCommandTarget_ShouldValidateRunIdAndExposeActorIdentity()
    {
        var target = new WorkflowRunControlCommandTarget("actor-1", "run-1");

        target.ActorId.Should().Be("actor-1");
        target.TargetId.Should().Be("actor-1");

        var act = () => new WorkflowRunControlCommandTarget("actor-1", " ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowResumeCommandEnvelopeFactory_ShouldValidateInputs_AndNormalizeOptionalUserInput()
    {
        var factory = new WorkflowResumeCommandEnvelopeFactory();
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var envelope = factory.CreateEnvelope(
            new WorkflowResumeCommand("actor-1", "run-1", "step-1", "cmd-1", true, null),
            context);

        var resumed = envelope.Payload.Unpack<WorkflowResumedEvent>();
        envelope.Id.Should().Be("cmd-1");
        resumed.UserInput.Should().BeEmpty();
        resumed.EditedContent.Should().BeEmpty();
        resumed.Feedback.Should().BeEmpty();

        var actOnCommand = () => factory.CreateEnvelope(null!, context);
        var actOnContext = () => factory.CreateEnvelope(
            new WorkflowResumeCommand("actor-1", "run-1", "step-1", "cmd-1", true, "approved"),
            null!);
        var actOnStepId = () => factory.CreateEnvelope(
            new WorkflowResumeCommand("actor-1", "run-1", " ", "cmd-1", true, "approved"),
            context);

        actOnCommand.Should().Throw<ArgumentNullException>();
        actOnContext.Should().Throw<ArgumentNullException>();
        actOnStepId.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowSignalCommandEnvelopeFactory_ShouldValidateInputs_AndNormalizeOptionalPayload()
    {
        var factory = new WorkflowSignalCommandEnvelopeFactory();
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var envelope = factory.CreateEnvelope(
            new WorkflowSignalCommand("actor-1", "run-1", "approve", "cmd-1", null),
            context);

        envelope.Id.Should().Be("cmd-1");
        envelope.Payload.Unpack<SignalReceivedEvent>().Payload.Should().BeEmpty();

        var actOnCommand = () => factory.CreateEnvelope(null!, context);
        var actOnContext = () => factory.CreateEnvelope(
            new WorkflowSignalCommand("actor-1", "run-1", "approve", "cmd-1", "yes"),
            null!);
        var actOnSignalName = () => factory.CreateEnvelope(
            new WorkflowSignalCommand("actor-1", "run-1", " ", "cmd-1", "yes"),
            context);

        actOnCommand.Should().Throw<ArgumentNullException>();
        actOnContext.Should().Throw<ArgumentNullException>();
        actOnSignalName.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowStopCommandEnvelopeFactory_ShouldValidateInputs_AndNormalizeOptionalReason()
    {
        var factory = new WorkflowStopCommandEnvelopeFactory();
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var envelope = factory.CreateEnvelope(
            new WorkflowStopCommand("actor-1", "run-1", "cmd-1", null),
            context);

        envelope.Id.Should().Be("cmd-1");
        envelope.Payload.Unpack<WorkflowStoppedEvent>().Reason.Should().BeEmpty();

        var actOnCommand = () => factory.CreateEnvelope(null!, context);
        var actOnContext = () => factory.CreateEnvelope(
            new WorkflowStopCommand("actor-1", "run-1", "cmd-1", "stop"),
            null!);

        actOnCommand.Should().Throw<ArgumentNullException>();
        actOnContext.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectInvalidActorId()
    {
        var resolver = new WorkflowResumeCommandTargetResolver(new FakeWorkflowActorBindingReader());

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand(" ", "run-1", "step-1", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidActorId(string.Empty));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectInvalidRunId()
    {
        var resolver = new WorkflowResumeCommandTargetResolver(new FakeWorkflowActorBindingReader());

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand("actor-1", " ", "step-1", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidRunId("actor-1", string.Empty));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectInvalidStepId()
    {
        var resolver = new WorkflowResumeCommandTargetResolver(new FakeWorkflowActorBindingReader());

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand("actor-1", "run-1", " ", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " "));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectInvalidSignalName()
    {
        var resolver = new WorkflowSignalCommandTargetResolver(new FakeWorkflowActorBindingReader());

        var result = await resolver.ResolveAsync(
            new WorkflowSignalCommand("actor-1", "run-1", " ", "cmd-1", "yes"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidSignalName("actor-1", "run-1", " "));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldReturnActorNotFound_WhenRuntimeDoesNotContainActor()
    {
        var resolver = new WorkflowSignalCommandTargetResolver(new FakeWorkflowActorBindingReader());

        var result = await resolver.ResolveAsync(
            new WorkflowSignalCommand("actor-404", "run-1", "approve", "cmd-1", "yes"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.ActorNotFound("actor-404", "run-1"));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectNonRunActorBindings()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var resolver = new WorkflowResumeCommandTargetResolver(new FakeWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "actor-1",
                    "definition-1",
                    string.Empty,
                    "auto",
                    "yaml",
                    new Dictionary<string, string>(), ExternalCapabilityExecutionMode.Interactive)));

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand("actor-1", "run-1", "step-1", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.ActorNotWorkflowRun("actor-1", "run-1"));
    }

    [Fact]
    public async Task WorkflowRunControlResolver_ShouldRejectBindingsWithoutRunId()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var resolver = new WorkflowSignalCommandTargetResolver(new FakeWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "actor-1",
                    "definition-1",
                    " ",
                    "auto",
                    "yaml",
                    new Dictionary<string, string>(), ExternalCapabilityExecutionMode.Interactive)));

        var result = await resolver.ResolveAsync(
            new WorkflowSignalCommand("actor-1", "run-1", "approve", "cmd-1", "yes"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.RunBindingMissing("actor-1", "run-1"));
    }

    [Fact]
    public async Task WorkflowRunCommandTarget_ShouldValidateConstructorAndBindingArguments()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();

        var actOnActor = () => new WorkflowRunCommandTarget(null!, "workflow-1", [], projectionPort, actorPort, new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var actOnWorkflowName = () => new WorkflowRunCommandTarget("actor-1", " ", [], projectionPort, actorPort, new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var actOnProjectionPort = () => new WorkflowRunCommandTarget("actor-1", "workflow-1", [], null!, actorPort, new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var actOnActorPort = () => new WorkflowRunCommandTarget("actor-1", "workflow-1", [], projectionPort, null!, new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        actOnActor.Should().Throw<ArgumentException>();
        actOnWorkflowName.Should().Throw<ArgumentException>();
        actOnProjectionPort.Should().Throw<ArgumentNullException>();
        actOnActorPort.Should().Throw<ArgumentNullException>();

        var target = new WorkflowRunCommandTarget("actor-1", "workflow-1", [], projectionPort, actorPort, new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var lease = new FakeProjectionLease("actor-1", "cmd-1");
        var sink = new FakeEventSink();

        var actOnLease = () => target.BindLiveObservation(null!, new FakeLiveSinkLease("actor-1"), sink);
        var actOnSink = () => target.BindLiveObservation(lease, new FakeLiveSinkLease("actor-1"), null!);

        actOnLease.Should().Throw<ArgumentNullException>();
        actOnSink.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 0)]
    public async Task WorkflowRunCommandTarget_ReleaseAfterInteraction_ShouldReclaimCreatedActors_WhenCompletionRequiresCleanup(
        bool observedCompleted,
        bool terminalDurableCompletion,
        int expectedDestroyCalls)
    {
        // 06-20-observatory-run-state-feed (R2): in-request cleanup releases the lease/sink and never destroys
        // synchronously; reclaim is scheduled (no gate → direct destroy on terminal), run inline for determinism.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = new WorkflowRunCommandTarget("actor-1",
            "workflow-1",
            ["definition-1", "run-1"],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()),
            detachedReclaimLauncher: reclaim => reclaim());
        target.BindLiveObservation(
            new FakeProjectionLease("actor-1", "cmd-1"),
            new FakeLiveSinkLease("actor-1"),
            new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                observedCompleted,
                WorkflowProjectionCompletionStatus.Unknown,
                new CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>(
                    terminalDurableCompletion,
                    WorkflowProjectionCompletionStatus.Completed)),
            CancellationToken.None);
        await target.PendingReclaimTask;

        projectionPort.Events.Should().Equal("detach:actor-1", "release:actor-1");
        actorPort.DestroyCalls.Should().HaveCount(expectedDestroyCalls);
    }

    [Fact]
    public async Task WorkflowRunCommandTarget_ReleaseAfterInteraction_ShouldValidateArguments()
    {
        var target = new WorkflowRunCommandTarget("actor-1",
            "workflow-1",
            [],
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        var actOnReceipt = async () => await target.ReleaseAfterInteractionAsync(
            null!,
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                false,
                WorkflowProjectionCompletionStatus.Unknown,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        var actOnCleanup = async () => await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            null!,
            CancellationToken.None);

        await actOnReceipt.Should().ThrowAsync<ArgumentNullException>();
        await actOnCleanup.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WorkflowRunCommandTarget_ReleaseAsync_ShouldHandleSinkOnlyBranch()
    {
        var sink = new FakeEventSink();
        var target = CreateBoundTarget(sink: sink);
        SetPrivateProperty(target, nameof(WorkflowRunCommandTarget.ProjectionLease), null);

        await target.ReleaseAsync(ct: CancellationToken.None);

        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunCommandTarget_ReleaseAsync_ShouldPropagateSinkDisposeFailure_WhenOnlySinkIsBound()
    {
        var sink = new FakeEventSink
        {
            DisposeException = new InvalidOperationException("dispose failed"),
        };
        var target = CreateBoundTarget(sink: sink);
        SetPrivateProperty(target, nameof(WorkflowRunCommandTarget.ProjectionLease), null);

        var act = async () => await target.ReleaseAsync(ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispose failed");
    }

    [Fact]
    public async Task WorkflowRunCommandTarget_ReleaseAsync_ShouldPropagateProjectionReleaseFailure_WhenOnlyLeaseIsBound()
    {
        var projectionPort = new FakeProjectionPort
        {
            ReleaseException = new InvalidOperationException("release failed"),
        };
        var target = CreateBoundTarget(projectionPort: projectionPort);
        SetPrivateProperty(target, nameof(WorkflowRunCommandTarget.LiveSink), null);

        var act = async () => await target.ReleaseAsync(ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("release failed");
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldReturnFailure_WhenActorResolutionFails()
    {
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(null, "auto", WorkflowChatRunStartError.AgentNotFound)),
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("auto"), ExternalCapabilityExecutionMode.Interactive), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
    }

    [Fact]
    public async Task WorkflowRunObservationLifecycle_ShouldPropagateBindFailureWithoutRollingBackCreatedActors()
    {
        var projectionPort = new FakeProjectionPort
        {
            AttachException = new InvalidOperationException("attach failed"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var lifecycle = new WorkflowRunObservationLifecycle(projectionPort);
        var target = new WorkflowRunCommandTarget("actor-1",
            "workflow-1",
            ["actor-1"],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());
        var act = async () => await lifecycle.BindAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("workflow-1"), ExternalCapabilityExecutionMode.Interactive),
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = context,
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "cmd-1"));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    private static WorkflowRunCommandTarget CreateBoundTarget(
        FakeProjectionPort? projectionPort = null,
        FakeWorkflowRunActorPort? actorPort = null,
        FakeEventSink? sink = null)
    {
        projectionPort ??= new FakeProjectionPort();
        actorPort ??= new FakeWorkflowRunActorPort();
        sink ??= new FakeEventSink();
        var target = new WorkflowRunCommandTarget("actor-1",
            "workflow-1",
            ["definition-1", "run-1"],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        target.BindLiveObservation(new FakeProjectionLease("actor-1", "cmd-1"), new FakeLiveSinkLease("actor-1"), sink);
        return target;
    }

    private static void SetPrivateProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property.Should().NotBeNull();
        property!.SetValue(instance, value);
    }

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        private readonly WorkflowActorResolutionResult _result;

        public FakeWorkflowRunActorResolver(WorkflowActorResolutionResult result)
        {
            _result = result;
        }

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding? _binding;

        public FakeWorkflowActorBindingReader(WorkflowActorBinding? binding = null)
        {
            _binding = binding;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_binding);
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled { get; set; } = true;
        public FakeProjectionLease ExistingLease { get; set; } = new("actor-1", "cmd-1");
        public Exception? AttachException { get; set; }
        public Exception? ReleaseException { get; set; }
        public List<(string RootActorId, string CommandId)> AttachExistingCalls { get; } = [];
        public List<string> Events { get; } = [];

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            if (AttachException != null)
                throw AttachException;

            Events.Add("attach");
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachExistingCalls.Add((rootActorId, commandId));
            var liveSinkLease = await AttachLiveSinkAsync(ExistingLease, sink, ct);
            return new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(
                ExistingLease,
                liveSinkLease);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            var actorId = liveSinkLease is FakeLiveSinkLease fakeLease ? fakeLease.ActorId : string.Empty;
            Events.Add($"detach:{actorId}");
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            Events.Add($"release:{lease.ActorId}");
            if (ReleaseException != null)
                throw ReleaseException;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunProvisioningPort
    {
        public Exception? DestroyException { get; set; }
        public List<string> DestroyCalls { get; } = [];
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            if (DestroyException != null)
                throw DestroyException;
            return Task.CompletedTask;
        }

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeProjectionLease(string actorId, string commandId) : IWorkflowExecutionProjectionLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
    }

    private sealed class FakeLiveSinkLease(string actorId) : IAsyncDisposable
    {
        public string ActorId { get; } = actorId;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public int DisposeCalls { get; private set; }
        public Exception? DisposeException { get; set; }

        public void Push(WorkflowRunEventEnvelope evt) => throw new NotSupportedException();

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default) => throw new NotSupportedException();

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (DisposeException != null)
                throw DisposeException;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new FakeAgent(id + "-agent");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
