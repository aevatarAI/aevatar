using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.GAgentService.Tests.Application;

// Test-add (test-coverage/cluster-035):
//   Covers refactor-introduced behavior in GAgentDraftRunInteraction.cs:99-175.
//   Cluster intent: draft-run cleanup owns typed live-sink leases and detaches without a process registry.
public sealed class GAgentDraftRunInteractionCoverageTests
{
    private const string ExpectedAgentKind = "tests.draft-run-expected";

    [Fact]
    public async Task Resolver_ShouldReturnUnknownAgentKind_WhenTypeCannotBeResolved()
    {
        var resolver = new GAgentDraftRunCommandTargetResolver(
            new DraftRunStubActorRuntime(),
            new DraftRunProjectionPort(),
            new RecordingGAgentRunTerminalProjectionPort(),
            agentKindRegistry: BuildRegistry());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                "scope-a",
                "tests.missing-draft-run-agent",
                "hello"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.UnknownAgentKind);
    }

    [Fact]
    public async Task Resolver_ShouldCreatePreferredActor_WhenMissing()
    {
        var runtime = new DraftRunStubActorRuntime();
        var resolver = new GAgentDraftRunCommandTargetResolver(
            runtime,
            new DraftRunProjectionPort(),
            new RecordingGAgentRunTerminalProjectionPort(),
            agentKindRegistry: BuildRegistry());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                "scope-a",
                ExpectedAgentKind,
                "hello",
                PreferredActorId: "preferred-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        runtime.CreateByKindCalls.Should().ContainSingle();
        runtime.CreateByKindCalls[0].agentKind.Should().Be(ExpectedAgentKind);
        runtime.CreateByKindCalls[0].actorId.Should().Be("preferred-1");
        result.Target!.ActorId.Should().Be("preferred-1");
        result.Target.DiagnosticClrTypeName.Should().Be(typeof(DraftRunExpectedAgent).FullName);
    }

    [Fact]
    public async Task CommandTargetCleanup_ShouldDetachReleaseAndDisposeBoundObservation()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        var sink = new DraftRunRecordingSink();
        target.BindTerminalProjection(terminalLease);
        var liveSinkLease = new RecordingLiveSinkLease();
        target.BindLiveObservation(lease, liveSinkLease, sink, "session-1");

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().ContainSingle(x => ReferenceEquals(x, liveSinkLease));
        liveSinkLease.DisposeCount.Should().Be(1);
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task CommandTargetCleanup_WhenOnlySinkIsBound_ShouldCompleteDisposeAndClearInteractionSink()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var sink = new DraftRunRecordingSink();
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(new DraftRunProjectionLease("actor-1", "cmd-1"), new RecordingLiveSinkLease(), sink, "session-1");
        SetProperty(target, nameof(GAgentDraftRunCommandTarget.ProjectionLease), null);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.LiveSink.Should().BeNull();
        target.LiveSinkLease.Should().BeNull();
        var requireLiveSink = () => target.RequireLiveSink();
        requireLiveSink.Should().Throw<InvalidOperationException>()
            .WithMessage("GAgent draft-run live sink is not bound.");
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
    }

    [Fact]
    public async Task CommandTargetCleanup_WhenOnlyProjectionLeaseIsBound_ShouldReleaseLeaseWithoutDetach()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(lease, new RecordingLiveSinkLease(), new DraftRunRecordingSink(), "session-1");
        SetProperty(target, nameof(GAgentDraftRunCommandTarget.LiveSink), null);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        target.ProjectionLease.Should().BeNull();
        target.LiveSinkLease.Should().BeNull();
        var requireLiveSink = () => target.RequireLiveSink();
        requireLiveSink.Should().Throw<InvalidOperationException>()
            .WithMessage("GAgent draft-run live sink is not bound.");
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_ShouldKeepTerminalProjection_WhenDraftRunAwaitsApproval()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        var sink = new DraftRunRecordingSink();
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(lease, new RecordingLiveSinkLease(), sink, "session-1");
        sink.Push(new AGUIEvent
        {
            Custom = new CustomEvent
            {
                Name = "TOOL_APPROVAL_REQUEST",
            },
        });
        sink.Complete();

        await foreach (var _ in target.RequireLiveSink().ReadAllAsync(CancellationToken.None))
        {
        }

        await target.ReleaseAfterInteractionAsync(
            new GAgentDraftRunAcceptedReceipt(
                "actor-1",
                typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                "cmd-1",
                "corr-1",
                "session-1"),
            new CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus>(
                ObservedCompleted: true,
                ObservedCompletion: GAgentDraftRunCompletionStatus.TextMessageCompleted,
                DurableCompletion: CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete),
            CancellationToken.None);

        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        terminalPort.ReleaseCalls.Should().BeEmpty();
        target.TerminalProjectionLease.Should().BeSameAs(terminalLease);
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_ShouldReleaseTerminalProjection_WhenDraftRunTextCompletesWithoutApproval()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(lease, new RecordingLiveSinkLease(), new DraftRunRecordingSink(), "session-1");

        await target.ReleaseAfterInteractionAsync(
            new GAgentDraftRunAcceptedReceipt(
                "actor-1",
                typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                "cmd-1",
                "corr-1",
                "session-1"),
            new CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus>(
                ObservedCompleted: true,
                ObservedCompletion: GAgentDraftRunCompletionStatus.TextMessageCompleted,
                DurableCompletion: CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete),
            CancellationToken.None);

        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_ShouldReleaseTerminalProjection_WhenDraftRunIsTerminal()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var terminalLease = new RecordingGAgentRunTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(lease, new RecordingLiveSinkLease(), new DraftRunRecordingSink(), "session-1");

        await target.ReleaseAfterInteractionAsync(
            new GAgentDraftRunAcceptedReceipt(
                "actor-1",
                typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                "cmd-1",
                "corr-1",
                "session-1"),
            new CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus>(
                ObservedCompleted: true,
                ObservedCompletion: GAgentDraftRunCompletionStatus.RunFinished,
                DurableCompletion: CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete),
            CancellationToken.None);

        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task ObservationLifecycle_ShouldReturnProjectionUnavailable_WhenProjectionPipelineIsUnavailable()
    {
        var projectionPort = new DraftRunProjectionPort { LeaseToReturn = null };
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var lifecycle = new GAgentDraftRunObservationLifecycle(projectionPort, terminalPort);
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new GAgentDraftRunCommand("scope-a", ExpectedAgentKind, "hello"),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        terminalPort.Calls.Should().ContainSingle(x =>
            x.actorId == "actor-1" &&
            x.correlationId == "corr-1" &&
            x.interactionKind == GAgentRunTerminalInteractionKind.DraftRun);
        terminalPort.ReleaseCalls.Should().ContainSingle();
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task ObservationLifecycle_ShouldReturnProjectionUnavailable_WhenTerminalProjectionIsUnavailable()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort { ReturnNullLease = true };
        var lifecycle = new GAgentDraftRunObservationLifecycle(projectionPort, terminalPort);
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new GAgentDraftRunCommand("scope-a", ExpectedAgentKind, "hello"),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        terminalPort.Calls.Should().ContainSingle(x =>
            x.actorId == "actor-1" &&
            x.correlationId == "corr-1" &&
            x.interactionKind == GAgentRunTerminalInteractionKind.DraftRun);
        terminalPort.ReleaseCalls.Should().BeEmpty();
        projectionPort.AttachCalls.Should().BeEmpty();
        projectionPort.AttachCalls.Should().BeEmpty();
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public void EnvelopeFactory_ShouldMapHeadersInputPartsAndSessionFallback()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: ExpectedAgentKind,
                Prompt: "hello",
                SessionId: " ",
                NyxIdAccessToken: " token ",
                ModelOverride: " model-x ",
                PreferredLlmRoute: " /route ",
                InputParts:
                [
                    new GAgentDraftRunInputPart
                    {
                        Kind = GAgentDraftRunInputPartKind.Text,
                        Text = "body",
                        Name = "p1",
                    },
                    new GAgentDraftRunInputPart
                    {
                        Kind = GAgentDraftRunInputPartKind.Image,
                        Uri = "https://example.com/image.png",
                        MediaType = "image/png",
                    },
                ]),
            new CommandContext(
                "actor-1",
                "cmd-1",
                "corr-1",
                new Dictionary<string, string>
                {
                    [" x-trace "] = " trace-1 ",
                    [" "] = "ignored",
                    ["empty"] = " ",
                }));

        var payload = envelope.Payload.Unpack<ChatRequestEvent>();
        payload.Prompt.Should().Be("hello");
        payload.ScopeId.Should().Be("scope-a");
        payload.SessionId.Should().Be("corr-1");
        payload.Headers["x-trace"].Should().Be("trace-1");
        payload.Headers.Should().NotContainKey("empty");
        payload.Metadata.Should().NotContainKey("x-trace");
        payload.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        payload.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        payload.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        var llmControl = LLMControlContextMapper.FromPayload(payload.LlmControl);
        llmControl.NyxIdAccessToken.Should().Be("token");
        llmControl.ModelOverride.Should().Be("model-x");
        llmControl.NyxIdRoutePreference.Should().Be("/route");
        payload.InputParts.Should().HaveCount(2);
        payload.InputParts[0].Kind.Should().Be(ChatContentPartKind.Text);
        payload.InputParts[0].Text.Should().Be("body");
        payload.InputParts[1].Kind.Should().Be(ChatContentPartKind.Image);
        payload.InputParts[1].Uri.Should().Be("https://example.com/image.png");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void EnvelopeFactory_ShouldSerializeCommandTypedToolControlFields()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();
        var toolContext = NewToolContext();
        var llmControl = NewLlmControl();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: ExpectedAgentKind,
                Prompt: "hello",
                NyxIdAccessToken: " legacy-token ",
                ModelOverride: " legacy-model ",
                PreferredLlmRoute: " legacy-route ",
                ToolContext: toolContext,
                LlmControl: llmControl),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>
            {
                ["x-trace"] = "trace-1",
            }));

        var payload = envelope.Payload.Unpack<ChatRequestEvent>();
        payload.Headers.Should().Contain("x-trace", "trace-1");
        payload.Metadata.Should().NotContainKey("x-trace");
        AgentToolExecutionContextMapper.FromPayload(payload.ToolContext).Should().BeEquivalentTo(toolContext);
        LLMControlContextMapper.FromPayload(payload.LlmControl).Should().BeEquivalentTo(llmControl);
    }

    [Fact]
    public void EnvelopeFactory_ShouldLeaveSessionEmpty_WhenFallbackIsDisabled()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: ExpectedAgentKind,
                Prompt: "hello",
                SessionId: null,
                UseCorrelationIdAsFallbackSessionId: false),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        envelope.Payload.Unpack<ChatRequestEvent>().SessionId.Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiptFactoryCompletionPolicyFinalizeEmitterAndDurableResolver_ShouldBehaveAsExpected()
    {
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            "actor-type",
            new DraftRunProjectionPort(),
            new RecordingGAgentRunTerminalProjectionPort());
        var receiptFactory = new GAgentDraftRunAcceptedReceiptFactory();
        var receipt = receiptFactory.Create(
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        receipt.Should().Be(new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1", string.Empty));

        var completionPolicy = new GAgentDraftRunCompletionPolicy();
        completionPolicy.TryResolve(new AGUIEvent { TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent() }, out var textCompletion)
            .Should().BeTrue();
        textCompletion.Should().Be(GAgentDraftRunCompletionStatus.TextMessageCompleted);
        completionPolicy.TryResolve(new AGUIEvent { RunFinished = new RunFinishedEvent() }, out var finishedCompletion)
            .Should().BeTrue();
        finishedCompletion.Should().Be(GAgentDraftRunCompletionStatus.RunFinished);
        completionPolicy.TryResolve(new AGUIEvent { RunError = new RunErrorEvent { Message = "boom" } }, out var failedCompletion)
            .Should().BeTrue();
        failedCompletion.Should().Be(GAgentDraftRunCompletionStatus.Failed);
        completionPolicy.TryResolve(new AGUIEvent(), out var unknownCompletion).Should().BeFalse();
        unknownCompletion.Should().Be(GAgentDraftRunCompletionStatus.Unknown);

        var emitter = new GAgentDraftRunFinalizeEmitter();
        var emitted = new List<AGUIEvent>();
        await emitter.EmitAsync(
            receipt,
            GAgentDraftRunCompletionStatus.TextMessageCompleted,
            completed: true,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        emitted.Should().ContainSingle();
        emitted[0].RunFinished.ThreadId.Should().Be("actor-1");
        emitted[0].RunFinished.RunId.Should().Be("cmd-1");
        emitted[0].RunFinished.Result.Unpack<GAgentDraftRunResultPayload>().Output.Should().BeEmpty();

        var terminalQuery = new RecordingGAgentRunTerminalQueryPort
        {
            CorrelationSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.DraftRun,
                GAgentRunTerminalStatus.TextMessageCompleted,
                string.Empty,
                string.Empty,
                3,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var durableResolver = new GAgentDraftRunDurableCompletionResolver(terminalQuery);
        (await durableResolver.ResolveAsync(receipt, CancellationToken.None))
            .Should().Be(new CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>(
                true,
                GAgentDraftRunCompletionStatus.TextMessageCompleted));
        terminalQuery.CorrelationCalls.Should().ContainSingle(x => x.actorId == "actor-1" && x.correlationId == "corr-1");
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldIgnoreSessionFallback_WhenCorrelationDiffers()
    {
        var terminalQuery = new RecordingGAgentRunTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "old-corr",
                GAgentRunTerminalInteractionKind.DraftRun,
                GAgentRunTerminalStatus.TextMessageCompleted,
                string.Empty,
                string.Empty,
                3,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var durableResolver = new GAgentDraftRunDurableCompletionResolver(terminalQuery);

        var result = await durableResolver.ResolveAsync(
            new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete);
        terminalQuery.SessionCalls.Should().ContainSingle(x => x.actorId == "actor-1" && x.sessionId == "session-1");
    }

    private static AgentToolExecutionContext NewToolContext() =>
        new(
            new AgentToolRequestIdentity("request-1", "call-1"),
            new AgentToolCredentials("access-token", "org-token", "sender-token"),
            new AgentToolCallerContext("scope-a", "owner-a", "response-1"),
            new AgentToolChannelContext("telegram", "sender-1", "registration-scope-1", "message-1", "platform-message-1", IdentityHints: []),
            new AgentToolSenderBindingContext("binding-1"),
            new LLMRequestRoutingContext("model-1", "route-1", 3, "remember"),
            new AgentToolConnectedServicesContext("connected"),
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external"] = "value",
            });

    private static LLMControlContext NewLlmControl() =>
        new("access-token", "org-token", "sender-token", "model-1", "route-1", 3, "remember");

    [Fact]
    public async Task DurableCompletionResolver_ShouldIgnoreSessionFallback_WhenInteractionKindDiffers()
    {
        var terminalQuery = new RecordingGAgentRunTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.Approval,
                GAgentRunTerminalStatus.TextMessageCompleted,
                string.Empty,
                string.Empty,
                3,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var durableResolver = new GAgentDraftRunDurableCompletionResolver(terminalQuery);

        var result = await durableResolver.ResolveAsync(
            new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete);
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldUseSessionFallback_WhenReceiptMatches()
    {
        var terminalQuery = new RecordingGAgentRunTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.DraftRun,
                GAgentRunTerminalStatus.RunFinished,
                string.Empty,
                string.Empty,
                3,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var durableResolver = new GAgentDraftRunDurableCompletionResolver(terminalQuery);

        var result = await durableResolver.ResolveAsync(
            new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(new CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>(
            true,
            GAgentDraftRunCompletionStatus.RunFinished));
    }

    [Fact]
    public async Task ObservationLifecycle_ShouldAttachExistingTerminalMaterialization_BeforeLiveObservation()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var lifecycle = new GAgentDraftRunObservationLifecycle(projectionPort, terminalPort);
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort,
            terminalPort);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new GAgentDraftRunCommand("scope-a", ExpectedAgentKind, "hello"),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        terminalPort.Calls.Should().ContainSingle(x =>
            x.actorId == "actor-1" &&
            x.correlationId == "corr-1" &&
            x.interactionKind == GAgentRunTerminalInteractionKind.DraftRun);
        projectionPort.AttachCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Interaction_ShouldFailWithProjectionUnavailable_AndNotDispatch_WhenTerminalProjectionAttachFails()
    {
        var projectionPort = new DraftRunProjectionPort();
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort { ReturnNullLease = true };
        var dispatchPort = new RecordingActorDispatchPort();
        var interaction = CreateInteraction(projectionPort, terminalPort, dispatchPort);

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        dispatchPort.Dispatches.Should().BeEmpty();
        terminalPort.Calls.Should().ContainSingle(x =>
            x.interactionKind == GAgentRunTerminalInteractionKind.DraftRun);
        terminalPort.ReleaseCalls.Should().BeEmpty();
        projectionPort.AttachCalls.Should().BeEmpty();
        projectionPort.AttachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Interaction_ShouldFailWithProjectionUnavailable_AndNotDispatch_WhenLiveProjectionAttachFails()
    {
        var projectionPort = new DraftRunProjectionPort { LeaseToReturn = null };
        var terminalPort = new RecordingGAgentRunTerminalProjectionPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var interaction = CreateInteraction(projectionPort, terminalPort, dispatchPort);

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        dispatchPort.Dispatches.Should().BeEmpty();
        terminalPort.Calls.Should().ContainSingle(x =>
            x.interactionKind == GAgentRunTerminalInteractionKind.DraftRun);
        terminalPort.ReleaseCalls.Should().ContainSingle();
        projectionPort.AttachCalls.Should().BeEmpty();
        projectionPort.AttachCalls.Should().BeEmpty();
    }

    private static CommandDispatchExecution<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt> CreateExecution(
        GAgentDraftRunCommandTarget target,
        CommandContext context) =>
        new()
        {
            Target = target,
            Context = context,
            Envelope = new EventEnvelope { Id = "evt-1" },
            Receipt = new GAgentDraftRunAcceptedReceipt(
                target.ActorId,
                target.DiagnosticClrTypeName,
                context.CommandId,
                context.CorrelationId,
                string.Empty),
        };

    private static ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> CreateInteraction(
        DraftRunProjectionPort projectionPort,
        RecordingGAgentRunTerminalProjectionPort terminalPort,
        RecordingActorDispatchPort dispatchPort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>(
            new GAgentDraftRunCommandTargetResolver(
                new DraftRunStubActorRuntime(),
                projectionPort,
                terminalPort,
                agentKindRegistry: BuildRegistry()),
            new DefaultCommandContextPolicy(),
            new GAgentDraftRunCommandEnvelopeFactory(),
            new ActorCommandTargetDispatcher<GAgentDraftRunCommandTarget>(dispatchPort),
            new GAgentDraftRunAcceptedReceiptFactory());

        return new DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>(
            pipeline,
            new DefaultEventOutputStream<AGUIEvent, AGUIEvent>(new IdentityEventFrameMapper<AGUIEvent>()),
            new GAgentDraftRunCompletionPolicy(),
            new GAgentDraftRunFinalizeEmitter(),
            new GAgentDraftRunDurableCompletionResolver(new RecordingGAgentRunTerminalQueryPort()),
            observationLifecycle: new GAgentDraftRunObservationLifecycle(projectionPort, terminalPort));
    }

    private static GAgentDraftRunCommand CreateCommand() =>
        new("scope-a", ExpectedAgentKind, "hello");

    private static IAgentKindRegistry BuildRegistry()
    {
        var builder = new AgentKindRegistryBuilder();
        builder.Register<DraftRunExpectedAgent>();
        return new AgentKindRegistry(builder.Build());
    }

    private sealed class DraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public DraftRunProjectionLease? LeaseToReturn { get; init; } = new("actor-1", "cmd-1");
        public RecordingLiveSinkLease LiveSinkLeaseToReturn { get; } = new();
        public bool ProjectionEnabled => true;
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> AttachCalls { get; } = [];
        public List<IAsyncDisposable?> DetachedLiveSinkLeases { get; } = [];
        public List<IGAgentDraftRunProjectionLease> ReleaseCalls { get; } = [];

        public async Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            if (LeaseToReturn == null)
                return null;

            var liveSinkLease = await AttachLiveSinkAsync(LeaseToReturn, sink, ct);
            return new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(LeaseToReturn, liveSinkLease);
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            AttachCalls.Add((lease, sink));
            return Task.FromResult<IAsyncDisposable?>(LiveSinkLeaseToReturn);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            DetachedLiveSinkLeases.Add(liveSinkLease);
            if (liveSinkLease != null)
            {
                return liveSinkLease.DisposeAsync().AsTask();
            }

            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed record DraftRunProjectionLease(string ActorId, string CommandId) : IGAgentDraftRunProjectionLease;

    private sealed class RecordingLiveSinkLease : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingGAgentRunTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public List<(string actorId, string correlationId, GAgentRunTerminalInteractionKind interactionKind)> Calls { get; } = [];
        public List<IGAgentRunTerminalProjectionLease> ReleaseCalls { get; } = [];
        public bool ReturnNullLease { get; init; }

        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, correlationId, interactionKind));
            if (ReturnNullLease)
                return Task.FromResult<IGAgentRunTerminalProjectionLease?>(null);

            return Task.FromResult<IGAgentRunTerminalProjectionLease?>(
                new RecordingGAgentRunTerminalProjectionLease(actorId, correlationId, interactionKind));
        }

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed record RecordingGAgentRunTerminalProjectionLease(
        string ActorId,
        string CorrelationId,
        GAgentRunTerminalInteractionKind InteractionKind) : IGAgentRunTerminalProjectionLease;

    private sealed class RecordingGAgentRunTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public GAgentRunTerminalSnapshot? CorrelationSnapshot { get; init; }
        public GAgentRunTerminalSnapshot? SessionSnapshot { get; init; }
        public List<(string actorId, string correlationId)> CorrelationCalls { get; } = [];
        public List<(string actorId, string sessionId)> SessionCalls { get; } = [];

        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default)
        {
            CorrelationCalls.Add((actorId, correlationId));
            return Task.FromResult(CorrelationSnapshot);
        }

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            SessionCalls.Add((actorId, sessionId));
            return Task.FromResult(SessionSnapshot);
        }
    }

    private sealed class DraftRunStubActorRuntime(params IActor[] actors) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(x => x.Id, StringComparer.Ordinal);
        public List<(Type agentType, string? actorId)> CreateCalls { get; } = [];
        public List<(string agentKind, string? actorId)> CreateByKindCalls { get; } = [];

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add((agentType, actorId));
            var actor = new DraftRunStubActor(actorId, (IAgent)Activator.CreateInstance(agentType)!);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateByKindCalls.Add((agentKind, actorId));
            var actor = new DraftRunStubActor(actorId, new DraftRunExpectedAgent());
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class DraftRunStubActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [GAgent(ExpectedAgentKind)]
    private sealed class DraftRunExpectedAgent : IAgent
    {
        public string Id => "draft-run-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DraftRunRecordingSink : IEventSink<AGUIEvent>
    {
        private readonly Queue<AGUIEvent> _events = new();
        public bool Completed { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Push(AGUIEvent evt) => _events.Enqueue(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default)
        {
            _ = ct;
            Push(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete() => Completed = true;

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            while (_events.Count > 0)
                yield return _events.Dequeue();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        property.Should().NotBeNull();
        property!.SetValue(instance, value);
    }
}
