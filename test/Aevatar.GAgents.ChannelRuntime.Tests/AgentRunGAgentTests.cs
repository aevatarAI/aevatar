using System.Text;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunGAgentTests
{
    [Fact]
    public async Task DispatchAsync_ShouldCreateRunActorAndDispatchStartCommand()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            dispatchPort,
            NullLogger<AgentRunDispatcher>.Instance);

        await dispatcher.DispatchAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-dispatch",
            RunId = "run-dispatch",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-dispatch",
        }, CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatchPort.Dispatches.Single();
        actorId.Should().Be(AgentRunActorIds.ForRun(AgentRunId.Parse("run-dispatch")));
        envelope.Id.Should().Be("agent-run-start:run-dispatch");
        envelope.Runtime.Deduplication.OperationId.Should().Be("agent-run-start:run-dispatch");
        envelope.Propagation.CorrelationId.Should().Be("corr-dispatch");
        var command = envelope.Payload.Unpack<AgentRunStartRequested>();
        command.Request.RunId.Should().Be("run-dispatch");
        command.Request.CorrelationId.Should().Be("corr-dispatch");
        command.Request.TargetActorId.Should().Be("conversation-actor");
        command.Request.ReplyToken.Should().Be("relay-token-dispatch");
    }

    [Fact]
    public async Task DispatchAsync_WhenRunIdMissing_ShouldRejectEvenWithCorrelationId()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            dispatchPort,
            NullLogger<AgentRunDispatcher>.Instance);

        var act = () => dispatcher.DispatchAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-trace-only",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*run_id*");
        dispatchPort.Dispatches.Should().BeEmpty();
        actorRuntime.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldAcceptDuplicateStarts_ForActorOwnedAdmission()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var now = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero);
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            dispatchPort,
            NullLogger<AgentRunDispatcher>.Instance,
            new FakeTimeProvider(now));
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-duplicate-dispatch",
            RunId = "run-duplicate-dispatch",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-duplicate-dispatch",
            RequestedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        await Task.WhenAll(
            dispatcher.DispatchAsync(request, CancellationToken.None),
            dispatcher.DispatchAsync(request.Clone(), CancellationToken.None));

        dispatchPort.Dispatches.Should().HaveCount(2);
        dispatchPort.Dispatches.Select(x => x.ActorId)
            .Should().OnlyContain(id => id == AgentRunActorIds.ForRun(AgentRunId.Parse("run-duplicate-dispatch")));
        dispatchPort.Dispatches.Select(x => x.Envelope.Id)
            .Should().OnlyContain(id => id == "agent-run-start:run-duplicate-dispatch");
        actorRuntime.DestroyedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenDispatchPortFails_ShouldPropagateWithoutDestroyCompensation()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var dispatchPort = new ThrowingActorDispatchPort();
        var now = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero);
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            dispatchPort,
            NullLogger<AgentRunDispatcher>.Instance,
            new FakeTimeProvider(now));
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-retry-after-enqueue-failure",
            RunId = "run-retry-after-enqueue-failure",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-retry-after-enqueue-failure",
            RequestedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var act = () => dispatcher.DispatchAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated enqueue failure");
        actorRuntime.DestroyedIds.Should().BeEmpty();
        dispatchPort.Dispatches.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_ShouldHandStaleRequestToRunActorAdmission()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var now = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero);
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            dispatchPort,
            NullLogger<AgentRunDispatcher>.Instance,
            new FakeTimeProvider(now));

        await dispatcher.DispatchAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale-dispatch",
            RunId = "run-stale-dispatch",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale-dispatch",
            RequestedAtUnixMs = now
                .AddMilliseconds(-(AgentRunGAgent.MaxRunRequestAgeMs + 1))
                .ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches.Single().ActorId.Should().Be(AgentRunActorIds.ForRun(AgentRunId.Parse("run-stale-dispatch")));
        (await actorRuntime.ExistsAsync(AgentRunActorIds.ForRun(AgentRunId.Parse("run-stale-dispatch")))).Should().BeTrue();
    }

    [Fact]
    public void ApplyReplyProduced_HistoricalEventWithoutReplyText_MarksAsAlreadyDispatched()
    {
        // Backward-compat for pre-refactor live state: AgentRunReplyProducedEvents persisted
        // by the old code path have no reply_text / outbound / terminal_state fields (proto3
        // defaults on deserialize). The old code only wrote this event AFTER a successful
        // dispatch, so on replay we MUST treat these as ReplyDispatched=true. Otherwise:
        //   1. HandleStartAsync would fire ReDispatchProducedReplyAsync with an empty payload
        //      (would surface as a blank or structural-error reply).
        //   2. HandleCleanupAsync would refuse to destroy the actor, leaking grain state.
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var historical = new AgentRunReplyProducedEvent
        {
            RunId = "run-historic",
            CorrelationId = "corr-historic",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // ReplyText, Outbound, TerminalState intentionally left default — this is the
            // shape proto3 deserialization gives for an event persisted before those fields
            // existed.
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), historical);

        // Legacy events get promoted straight to handed-off on replay (ADR-0021):
        // historically a ReplyProduced event was only persisted *after* successful
        // dispatch, so on replay we treat the event as if dispatch had also landed.
        next.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
    }

    [Fact]
    public void ApplyReplyProduced_NewInteractiveOnlyEvent_EmptyReplyText_ButNonNullOutbound_IsNotMisclassifiedAsHistorical()
    {
        // Interactive-only turns (reply_with_interaction, card-only intents) produce an
        // empty reply_text but a non-null outbound (card / button payload). The historical-
        // event discriminator MUST require BOTH empty reply_text AND null outbound,
        // otherwise this event would be marked ReplyDispatched=true on replay and
        // ReDispatchProducedReplyAsync would never fire after a failed dispatch — the user
        // would silently lose the interactive reply.
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var interactiveCard = new MessageContent { Text = string.Empty };
        interactiveCard.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var interactiveOnly = new AgentRunReplyProducedEvent
        {
            RunId = "run-interactive",
            CorrelationId = "corr-interactive",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyText = string.Empty, // intentionally empty — interactive-only turn
            Outbound = interactiveCard,
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), interactiveOnly);

        // Interactive-only fresh event: payload persisted, but status stays at
        // REPLY_PRODUCED until ApplyReplyDispatched promotes it to REPLY_HANDED_OFF.
        next.Status.Should().Be(AgentRunStatus.ReplyProduced);
        next.ProducedReplyText.Should().BeEmpty();
        next.ProducedOutbound.Should().NotBeNull();
        next.ProducedOutbound!.Actions.Should().ContainSingle(a => a.ActionId == "confirm");
    }

    [Fact]
    public void ApplyReplyProduced_NewEventWithReplyText_LeavesStatusAtReplyProduced()
    {
        // New events always carry a non-empty reply_text (empty replies get replaced with a
        // user-visible fallback before persisting). Those events represent "payload persisted
        // but not yet handed off" — Status stays at REPLY_PRODUCED here; the subsequent
        // AgentRunReplyDispatchedEvent promotes it to REPLY_HANDED_OFF after the
        // conversation actor accepts the LlmReplyReadyEvent (ADR-0021).
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var fresh = new AgentRunReplyProducedEvent
        {
            RunId = "run-fresh",
            CorrelationId = "corr-fresh",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyText = "hello",
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), fresh);

        next.Status.Should().Be(AgentRunStatus.ReplyProduced);
        next.ProducedReplyText.Should().Be("hello");
        next.ProducedTerminalState.Should().Be(LlmReplyTerminalState.Completed);
    }

    [Fact]
    public async Task HandleStartAsync_WhenAccepted_PersistsGenerationRequestedAndHandsOffToExecutor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new PausedReplyGenerationExecutor();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            DeliverSelf = false,
        };
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-generation-requested",
            RunId = "run-generation-requested",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-generation-requested",
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        runtime.State.GenerationAttempt.Should().Be(1);
        runtime.State.GenerationRequestedAtUnixMs.Should().BeGreaterThan(0);
        generationExecutor.InitialSteps.Should().ContainSingle();
        generationExecutor.InitialSteps[0].RunId.Should().Be("run-generation-requested");
        generationExecutor.InitialSteps[0].RunActorId.Should().Be(runtime.Id);
        runtime.State.GenerationStep.Should().NotBeNull();
        runtime.State.GenerationStep!.NextStepIndex.Should().Be(1);
        publisher.Published.Should().ContainSingle(e =>
            e.Audience == TopologyAudience.Self &&
            e.Event is AgentRunNextLlmStepRequestedEvent);
    }

    [Fact]
    public async Task HandleStartAsync_WhenGenerationRequested_DoesNotStartSecondExecutor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new PausedReplyGenerationExecutor();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            DeliverSelf = false,
        };
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-generation-duplicate",
            RunId = "run-generation-duplicate",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-generation-duplicate",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        generationExecutor.InitialSteps.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_ShouldAdvanceLlmToolLlmSteps_AndAppendToolResult()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new ScriptedStepGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });
        generationExecutor.Bind(runtime);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-per-step",
            RunId = "run-per-step",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-per-step",
        };

        await runtime.HandleStartAsync(request);

        generationExecutor.LlmSteps.Should().HaveCount(2);
        generationExecutor.ToolSteps.Should().ContainSingle();
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.GenerationStep.Should().NotBeNull();
        runtime.State.GenerationStep!.Messages.Should().Contain(message =>
            message.Role == "tool" &&
            message.ToolCallId == "tool-call-1" &&
            message.Content == """{"result":"tool-ok"}""");
        var ready = handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Subject
            .Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("final answer after tool");
        ready.ReplyToken.Should().Be("relay-token-per-step");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_WithRealExecutor_ShouldPersistInitialState_RunToolRound_AndCompleteOnce()
    {
        var provider = new DeterministicToolRoundProvider();
        var tool = new DeterministicLookupTool();
        var replyGenerator = new RealExecutorStepReplyGenerator(provider, tool);
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            DeliverSelf = false,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-real-per-step",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity("msg-real-per-step", "run lookup"),
            ReplyToken = "relay-token-real-per-step",
        };

        await runtime.HandleStartAsync(request);

        runtime.State.GenerationStep.Should().NotBeNull();
        var initial = runtime.State.GenerationStep!;
        initial.NextStepIndex.Should().Be(1);
        initial.Round.Should().Be(0);
        initial.MaxToolRounds.Should().Be(1);
        initial.Messages.Should().Contain(message => message.Role == "system");
        initial.Messages.Should().Contain(message => message.Role == "user" && message.Content == "run lookup");
        var firstSelf = publisher.Published.Should()
            .ContainSingle(item => item.Audience == TopologyAudience.Self &&
                                   item.Event is AgentRunNextLlmStepRequestedEvent)
            .Subject.Event.Should().BeOfType<AgentRunNextLlmStepRequestedEvent>().Subject;
        firstSelf.StepIndex.Should().Be(1);

        await runtime.HandleNextLlmStepAsync(firstSelf);

        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].Tools.Should().ContainSingle().Which.Name.Should().Be("lookup");
        provider.Requests[1].Tools.Should().BeNull();
        provider.Requests[1].ToolContext!.Request.CallId.Should().Be($"{request.Activity.Id}:final");
        tool.Arguments.Should().ContainSingle().Which.Should().Be("""{"q":"aevatar"}""");
        runtime.State.GenerationStep!.Round.Should().Be(1);
        runtime.State.GenerationStep.FinalNoToolsStep.Should().BeTrue();
        runtime.State.GenerationStep.Messages.Should().Contain(message =>
            message.Role == "tool" &&
            message.ToolCallId == "tool-call-real" &&
            message.Content == """{"result":"tool-ok:aevatar"}""");
        runtime.State.GenerationStep.Messages.Should().Contain(message =>
            message.Role == "tool" &&
            message.ToolCallId == "tool-call-real" &&
            message.Content == ToolCallLoop.BuildToolResultMessage(
                "tool-call-real",
                """{"result":"tool-ok:aevatar"}""").Content);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.GenerationStep!.AccumulatedText.Should().Be("final after tool");
        runtime.State.GenerationStep.LastFinishReason.Should().Be("stop");
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
        var ready = handled.Single(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor))
            .Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("final after tool");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_ShouldRejectMismatchedAttemptAndOutOfWindowStep()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new RecordingStepGenerationExecutor();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            DeliverSelf = false,
        };
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-step-reconcile",
            RunId = "run-step-reconcile",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-step-reconcile",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-step-reconcile",
            CorrelationId = "corr-step-reconcile",
            TargetActorId = "actor-1",
            Attempt = 2,
            StepIndex = 1,
            Request = request.Clone(),
        });

        generationExecutor.LlmSteps.Should().BeEmpty();

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-step-reconcile",
            CorrelationId = "corr-step-reconcile",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 1,
            Request = request.Clone(),
        });

        generationExecutor.LlmSteps.Should().ContainSingle();

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-step-reconcile",
            CorrelationId = "corr-step-reconcile",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 3,
            Request = request.Clone(),
        });

        generationExecutor.LlmSteps.Should().ContainSingle(
            "a self-message may only reconcile the current or immediately completed next step");

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-step-reconcile",
            CorrelationId = "corr-step-reconcile",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = request.Clone(),
            StepState = NewStepState(request, nextStepIndex: 2, pendingTool: true),
        });

        generationExecutor.ToolSteps.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleReplyGenerationTimedOutAsync_WhenSchedulerBeatsExecutor_NotifiesConversationAndIgnoresLateLlmStep()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var generationExecutor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
                ResponseTimeoutSeconds = 1,
            },
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-generation-timeout-race",
            RunId = "run-generation-timeout-race",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-generation-timeout-race",
        });

        var timeout = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunReplyGenerationTimedOut.Descriptor)).Subject;

        await runtime.HandleReplyGenerationTimedOutAsync(
            timeout.TriggerEnvelope.Payload.Unpack<AgentRunReplyGenerationTimedOut>());

        runtime.State.Status.Should().Be(AgentRunStatus.Failed);
        runtime.State.ErrorCode.Should().Be("llm_reply_timeout");
        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
        var dropped = handled.Single().Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-generation-timeout-race");
        dropped.Reason.Should().Be("llm_reply_timeout");

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-generation-timeout-race",
            CorrelationId = "corr-generation-timeout-race",
            TargetActorId = "actor-1",
            Attempt = generationExecutor.InitialSteps.Single().Attempt,
            Request = generationExecutor.InitialSteps.Single().Request.Clone(),
            StepIndex = 2,
            StepState = new AgentRunReplyStepState
            {
                RunId = "run-generation-timeout-race",
                CorrelationId = "corr-generation-timeout-race",
                TargetActorId = "actor-1",
                Attempt = generationExecutor.InitialSteps.Single().Attempt,
                NextStepIndex = 2,
                AccumulatedText = "late executor reply",
                MaxToolRounds = 40,
            },
        });

        runtime.State.Status.Should().Be(AgentRunStatus.Failed);
        runtime.State.ProducedReplyText.Should().BeEmpty();
        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
        handled.Should().NotContain(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task ProduceAndDispatch_WhenPersistDispatchedFails_DoesNotDeliverDuplicateFallbackReply()
    {
        // Once DispatchReadyEventAsync delivers the reply to the conversation actor, the user
        // has the response. If PersistReplyDispatchedAsync then fails, the actor MUST swallow
        // that error locally — otherwise HandleStartAsync's outer `catch (Exception)` would
        // call FailAfterUnexpectedExceptionAsync, which would re-enter ProduceAndDispatchAsync
        // with the "Sorry, I couldn't complete this reply" fallback and deliver a SECOND
        // user-visible message on top of the real one.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "the real reply" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });
        // Inject a transient failure on the AgentRunReplyDispatchedEvent persist only.
        runtime.EventSourcing = new FailOnEventTypeSourcing<AgentRunGAgentState, AgentRunReplyDispatchedEvent>(
            (current, evt) => InvokeAgentTransition(runtime, current, evt));

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-dispatched-persist-fail",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-dispatched-persist-fail",
        });

        // Exactly one reply delivered to the conversation actor — the real one. No duplicate
        // fallback was emitted.
        handled.Should().HaveCount(1);
        var ready = handled[0].Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("the real reply");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        replyGenerator.CallCount.Should().Be(1);

        // State stays at REPLY_PRODUCED (the Dispatched event failed to persist, so
        // status is NOT promoted to REPLY_HANDED_OFF). The actor lingers until idle
        // eviction — acceptable trade-off vs. delivering a duplicate user-visible fallback.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        runtime.State.ProducedReplyText.Should().Be("the real reply");
    }

    [Fact]
    public async Task HandleStartAsync_WhenTargetRefForwardsToGAgent_OverridesTargetActorId()
    {
        // Regression: NeedsLlmReplyEvent.TargetRef carries the chat-route
        // boundary decision from ConversationGAgent into the run actor.
        // Before this fix the field was written + persisted but no consumer
        // read it — Forward* actions silently no-op'd on the relay path.
        // ForwardToGAgent.actor_id must redirect the reply target so per-bot
        // routing rules (e.g. /daily → specialized agent X) actually take effect.
        var originalTarget = Substitute.For<IActor>();
        originalTarget.Id.Returns("conversation:original");
        var forwardedTarget = Substitute.For<IActor>();
        forwardedTarget.Id.Returns("conversation:forwarded");
        var forwardedHandled = new List<EventEnvelope>();
        forwardedTarget.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => forwardedHandled.Add(call.Arg<EventEnvelope>()));
        var originalHandled = new List<EventEnvelope>();
        originalTarget.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => originalHandled.Add(call.Arg<EventEnvelope>()));

        var actorRuntime = new DispatchingActorRuntime(
            ("conversation:original", originalTarget),
            ("conversation:forwarded", forwardedTarget));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-forward-gagent",
            TargetActorId = "conversation:original",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-forward-gagent",
            TargetRef = new ChatRouteAction
            {
                ForwardToGagent = new ForwardToGAgent { ActorId = "conversation:forwarded" },
            },
        });

        forwardedHandled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor),
            "ForwardToGAgent.actor_id must redirect the reply target");
        originalHandled.Should().BeEmpty(
            "the original conversation actor must not receive the reply when the route override fires");
        runtime.State.TargetActorId.Should().Be("conversation:forwarded",
            "the persisted run state must reflect the override, otherwise replay/retry undoes it");
    }

    [Fact]
    public async Task HandleStartAsync_WhenTargetRefForwardsToModel_InjectsModelOverrideMetadata()
    {
        // Regression: ForwardToModel.model_name from the chat-route policy
        // must flow through the typed LLM control carrier so the LLM provider
        // sees the policy-chosen model. Bot-owner default model
        // intentionally loses to the chat-route override — chat route is
        // the more specific decision (caller-scope + rule match).
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("conversation:c");
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", actor));
        LLMControlContext? observedControl = null;
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ok",
            LlmControlObserver = control => observedControl = control,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-forward-model",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-forward-model",
            TargetRef = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = "anthropic/claude-sonnet-4-6" },
            },
        });

        observedControl.Should().NotBeNull("the LLM provider must have been invoked");
        observedControl!.ModelOverride.Should().Be(
            "anthropic/claude-sonnet-4-6",
            "ForwardToModel.model_name must reach the LLM provider via the typed llm_control field");
    }

    [Fact]
    public async Task HandleStartAsync_WhenTargetRefForwardsToModel_OverridesBotOwnerDefaultModel()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("conversation:c");
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", actor));
        LLMControlContext? observedControl = null;
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ok",
            LlmControlObserver = control => observedControl = control,
        };

        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-bot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("scope-bot-owner"));
        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-bot-owner", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11)));

        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            scopeResolver,
            userConfigQueryPort);

        var activity = BuildRelayActivity();
        activity.Bot = BotInstanceId.From("api-key-bot");

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-forward-model-owner",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-forward-model-owner",
            TargetRef = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = "anthropic/claude-sonnet-4-6" },
            },
        });

        observedControl.Should().NotBeNull("the LLM provider must have been invoked");
        observedControl!.ModelOverride.Should().Be(
            "anthropic/claude-sonnet-4-6",
            "chat-route policy is more specific than the bot owner's default model");
        observedControl.NyxIdRoutePreference.Should().Be(
            "/api/v1/proxy/s/anthropic-via-bot-owner",
            "the route preference is independent from the model override");
    }

    [Fact]
    public async Task HandleStartAsync_WhenTargetRefIsNullOrNone_LeavesRequestUnchanged()
    {
        // Defense-in-depth: turns without a chat-route policy match must
        // behave exactly like pre-PR code. No actor redirect, no model
        // override metadata injection.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("conversation:c");
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", actor));
        IReadOnlyDictionary<string, string>? observedMetadata = null;
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ok",
            MetadataObserver = m => observedMetadata = m,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-targetref",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-no-targetref",
            // TargetRef intentionally not set
        });

        runtime.State.TargetActorId.Should().Be("conversation:c");
        observedMetadata.Should().NotBeNull();
        observedMetadata!.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride,
            "ModelOverride metadata must only appear when TargetRef.ForwardToModel was set");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldIgnoreDuplicateStart_AfterReadyAcceptedAndTerminalPersisted()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-duplicate",
            RunId = "run-duplicate",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-duplicate",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        // First call ran the LLM and dispatched the ready event, promoting status to
        // REPLY_HANDED_OFF (ADR-0021). The duplicate start must short-circuit on
        // terminal-status check and NOT re-run the LLM or re-dispatch.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.RunId.Should().Be("run-duplicate");
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_ShouldScheduleTerminalCleanupAfterReplyProduced()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup-schedule",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup-schedule",
        });

        var cleanup = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunCleanupRequested.Descriptor)).Subject;
        cleanup.ActorId.Should().Be(runtime.Id);
        cleanup.DueTime.Should().Be(AgentRunGAgent.TerminalCleanupDelay);
        var cleanupCommand = cleanup.TriggerEnvelope.Payload.Unpack<AgentRunCleanupRequested>();
        cleanupCommand.RunId.Should().Be(runtime.State.RunId);
    }

    [Fact]
    public async Task HandleStartAsync_TerminalRun_ShouldNotEmitDuplicateReadyEvent()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-terminal-idempotent",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-terminal-idempotent",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        replyGenerator.CallCount.Should().Be(1);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
    }

    [Fact]
    public async Task HandleCleanupAsync_ShouldDestroyTerminalRunActor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup",
        });
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = runtime.State.RunId,
        });

        actorRuntime.DestroyedIds.Should().Contain(runtime.Id);
    }

    // ───────────────────────────────────────────────────────────────
    // ADR-0021 §6 / canon §9 #649 — absorbing-terminal regressions
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleCleanupAsync_TwiceAfterTerminal_ShouldDestroyOnceAndPersistCompletion()
    {
        // #649 regression: cleanup is an absorbing operation. A duplicate
        // cleanup callback (e.g. retry from a scheduler outage) must short-circuit
        // on cleanup_completed_at_unix_ms != 0 instead of re-destroying the actor.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup-dup",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup-dup",
        });
        var cleanup = new AgentRunCleanupRequested { RunId = runtime.State.RunId };
        await runtime.HandleCleanupAsync(cleanup);
        await runtime.HandleCleanupAsync(cleanup);

        actorRuntime.DestroyedIds.Should().ContainSingle(id => id == runtime.Id);
        runtime.State.CleanupCompletedAtUnixMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleCleanupAsync_StaleRunId_ShouldNoOp()
    {
        // #649 regression: a cleanup callback that references a different RunId
        // (e.g. an older grain run after grain identity churn) must NOT destroy
        // the current actor, even if the current actor is terminal.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale-cleanup",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale",
        });
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = "corr-different-run",
        });

        actorRuntime.DestroyedIds.Should().BeEmpty();
        runtime.State.CleanupCompletedAtUnixMs.Should().Be(0);
    }

    [Fact]
    public async Task HandleCleanupAsync_BeforeTerminal_ShouldNoOp()
    {
        // #649 regression: a cleanup callback that fires while the run is still
        // STARTED (e.g. scheduler clock skew) must NOT destroy the actor mid-run.
        // IsTerminal short-circuit blocks the path.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var hangingGenerator = new RecordingReplyGenerator(() => false)
        {
            HangUntilCancelled = true,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            hangingGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        // Fire a cleanup before any HandleStartAsync has even run — state is
        // STATUS_UNSPECIFIED (treated as non-terminal), so cleanup must no-op.
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = "corr-pre-terminal",
        });

        actorRuntime.DestroyedIds.Should().BeEmpty();
        runtime.State.CleanupCompletedAtUnixMs.Should().Be(0);
    }

    [Fact]
    public async Task HandleStartAsync_AfterCleanupCompleted_ShouldNotReScheduleCleanup()
    {
        // #649 regression: once chain.finalized is established (terminal status +
        // cleanup_completed_at != 0), a late duplicate start must NOT re-schedule
        // a fresh cleanup callback. Otherwise a flaky retry could pile up
        // callbacks indefinitely on a dead actor.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-resched",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-no-resched",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = runtime.State.RunId,
        });
        var cleanupCountAfterFirst = scheduler.Timeouts
            .Count(t => t.TriggerEnvelope.Payload.Is(AgentRunCleanupRequested.Descriptor));

        // Late duplicate start after chain.finalized.
        await runtime.HandleStartAsync(request.Clone());

        replyGenerator.CallCount.Should().Be(1);
        scheduler.Timeouts
            .Count(t => t.TriggerEnvelope.Payload.Is(AgentRunCleanupRequested.Descriptor))
            .Should().Be(cleanupCountAfterFirst, "cleanup_completed_at gates duplicate scheduling");
    }

    [Fact]
    public async Task HandleStartAsync_AfterDropped_ShouldNotReRunLlmOrPersistAdditionalEvents()
    {
        // #649 regression: stale-gate drop is itself an absorbing terminal state.
        // A second start with the same (still stale) request must short-circuit on
        // IsTerminal — neither replay the LLM nor persist additional drop events.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should-not-be-invoked" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        // First start: ages out via the stale gate (>5min request age) -> DROPPED.
        var staleRequest = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale-drop",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale-drop",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds(),
        };
        await runtime.HandleStartAsync(staleRequest);
        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        var droppedDispatchCount = handled.Count;

        // Duplicate stale start: IsTerminal short-circuit blocks LLM/dispatch.
        await runtime.HandleStartAsync(staleRequest.Clone());

        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        replyGenerator.CallCount.Should().Be(0);
        handled.Count.Should().Be(droppedDispatchCount, "no additional drop events on duplicate start");
    }

    [Fact]
    public async Task HandleCleanupAsync_ShouldIgnoreNonTerminalRun()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = "corr-non-terminal-cleanup",
        });

        actorRuntime.DestroyedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCleanupAsync_ShouldIgnoreMismatchedTerminalRunId()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup-mismatch",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup-mismatch",
        });
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = "corr-some-other-run",
        });

        actorRuntime.DestroyedIds.Should().BeEmpty();
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.CleanupCompletedAtUnixMs.Should().Be(0);
    }

    [Fact]
    public async Task HandleStartAsync_TerminalDrop_ShouldNotDispatchDuplicateDropNotification()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        AgentRunStatus? statusWhenNotified = null;
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                handled.Add(call.Arg<EventEnvelope>());
                statusWhenNotified = runtime.State.Status;
            });

        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-terminal-drop-idempotent",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            // Relay request with no command-carried ReplyToken should drop before LLM execution.
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        statusWhenNotified.Should().Be(AgentRunStatus.Dropped, "AgentRunDroppedEvent must be persisted before notifying");
        runtime.State.DropNotificationDispatchedAtUnixMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleStartAsync_TerminalFailure_ShouldNotDispatchDuplicateFailureReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-terminal-failed-idempotent",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-terminal-failed-idempotent",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedTerminalState.Should().Be(LlmReplyTerminalState.Failed);
    }

    [Fact]
    public async Task HandleStartAsync_OnOutputDispatchFailure_PersistsProducedReply_AndRetryReDispatchesWithoutRerunningLlm()
    {
        // Iron rule: output-dispatch failure must NOT replay the LLM/tool chain. The first
        // turn produces the reply, persists it to state, and only then attempts dispatch.
        // The retry must read from state and only re-deliver — repeating the LLM call could
        // repeat tool side effects (SSH exec, external API calls) and incur duplicate billing.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-retry-ready",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-retry-ready",
        };

        await runtime.HandleStartAsync(request);

        // After the first call the LLM ran once and the produced payload is persisted, but
        // dispatch failed so status stayed at REPLY_PRODUCED (no promotion to REPLY_HANDED_OFF).
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        runtime.State.ProducedReplyText.Should().Be("ok");
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().BeEmpty();

        var retry = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor)).Subject;
        retry.ActorId.Should().Be(runtime.Id);
        retry.DueTime.Should().Be(AgentRunGAgent.OutputDispatchRetryDelay);
        var retryCommand = retry.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>();
        retryCommand.RunId.Should().Be(runtime.State.RunId);
        retryCommand.CorrelationId.Should().Be("corr-retry-ready");
        retryCommand.TargetActorId.Should().Be("actor-1");
        Encoding.UTF8.GetString(retry.TriggerEnvelope.ToByteArray()).Should().NotContain("relay-token-retry-ready");

        await runtime.HandleOutputDispatchRetryAsync(retryCommand);

        // Durable retry cannot rehydrate runtime-only relay reply_token, so it is
        // explicitly non-retryable after reconciling the produced reply from state.
        runtime.State.Status.Should().Be(AgentRunStatus.Failed);
        runtime.State.ErrorCode.Should().Be("missing_relay_reply_token_for_durable_retry");
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleOutputDispatchRetryAsync_ForNonRelay_ReDispatchesPersistedReplyWithoutRerunningLlm()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-nonrelay-retry-ready",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-nonrelay-retry-ready",
                Content = new MessageContent { Text = "hello" },
            },
        };

        await runtime.HandleStartAsync(request);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().BeEmpty();

        var retryCommand = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>();

        await runtime.HandleOutputDispatchRetryAsync(retryCommand);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task HandleOutputDispatchRetryAsync_WhenTargetActorIdOrGenerationDoesNotMatch_DropsStaleRetry()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale-retry-ready",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-stale-retry-ready",
                Content = new MessageContent { Text = "hello" },
            },
        };

        await runtime.HandleStartAsync(request);

        var retryCommand = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>();

        var wrongTarget = retryCommand.Clone();
        wrongTarget.TargetActorId = "actor-2";
        await runtime.HandleOutputDispatchRetryAsync(wrongTarget);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        handled.Should().BeEmpty();

        var wrongGeneration = retryCommand.Clone();
        wrongGeneration.Generation = retryCommand.Generation + 1;
        await runtime.HandleOutputDispatchRetryAsync(wrongGeneration);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        handled.Should().BeEmpty();

        await runtime.HandleOutputDispatchRetryAsync(retryCommand);

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_ShouldScheduleDropNotificationRetry_WhenDropSignalIsNotAccepted()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-retry-drop",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        };

        await runtime.HandleStartAsync(request);

        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        runtime.State.PendingDropNotificationRunId.Should().Be(runtime.State.RunId);
        runtime.State.PendingDropNotificationCorrelationId.Should().Be("corr-retry-drop");
        runtime.State.PendingDropNotificationTargetActorId.Should().Be("actor-1");
        runtime.State.PendingDropNotificationReason.Should().Be("missing_relay_reply_token");
        runtime.State.DropNotificationDispatchedAtUnixMs.Should().Be(0);
        handled.Should().BeEmpty();
        replyGenerator.CallCount.Should().Be(0);

        scheduler.Timeouts.Should().NotContain(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor),
            "drop notification retry must stay separate from ready-output retry");
        var retry = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunDropNotificationRetryRequested.Descriptor))
            .Subject;
        retry.ActorId.Should().Be(runtime.Id);
        retry.DueTime.Should().Be(AgentRunGAgent.DropNotificationRetryDelay);
        var retryCommand = retry.TriggerEnvelope.Payload.Unpack<AgentRunDropNotificationRetryRequested>();
        retryCommand.RunId.Should().Be(runtime.State.RunId);
        retryCommand.CorrelationId.Should().Be("corr-retry-drop");
        retryCommand.TargetActorId.Should().Be("actor-1");

        await runtime.HandleDropNotificationRetryAsync(retryCommand);

        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        runtime.State.DropNotificationDispatchedAtUnixMs.Should().BeGreaterThan(0);
        replyGenerator.CallCount.Should().Be(0);
        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
        var dropped = handled.Single().Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-retry-drop");
        dropped.Reason.Should().Be("missing_relay_reply_token");
        dropped.DroppedAtUnixMs.Should().Be(runtime.State.PendingDropNotificationDroppedAtUnixMs);
    }

    [Fact]
    public async Task HandleDropNotificationRetryAsync_WhenTargetDoesNotMatch_DropsStaleRetry()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "should not run" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale-drop-retry",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        var retryCommand = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunDropNotificationRetryRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunDropNotificationRetryRequested>();

        var wrongTarget = retryCommand.Clone();
        wrongTarget.TargetActorId = "actor-2";
        await runtime.HandleDropNotificationRetryAsync(wrongTarget);

        handled.Should().BeEmpty();
        runtime.State.DropNotificationDispatchedAtUnixMs.Should().Be(0);

        await runtime.HandleDropNotificationRetryAsync(retryCommand);

        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
        runtime.State.DropNotificationDispatchedAtUnixMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleStartAsync_OnUnexpectedException_PersistsFailedProducedReply_AndDispatchesFallback()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new FailingOnceGetActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-unexpected",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-unexpected",
        });

        // The unhandled exception fires the persist-before-dispatch path: the failure
        // terminal state lands as ProducedTerminalState=Failed with a user-visible fallback,
        // and dispatch succeeds so status is promoted to REPLY_HANDED_OFF (ADR-0021).
        // The LLM was never invoked.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedTerminalState.Should().Be(LlmReplyTerminalState.Failed);
        runtime.State.ErrorCode.Should().Be("agent_run_unhandled_exception");
        replyGenerator.CallCount.Should().Be(0);
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("agent_run_unhandled_exception");
    }

    [Fact]
    public async Task HandleStartAsync_RelayTurnCapturesInteractiveIntentIntoReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() =>
        {
            var intent = new MessageContent
            {
                Text = "Choose one",
            };
            intent.Actions.Add(new ActionElement
            {
                Kind = ActionElementKind.Button,
                ActionId = "confirm",
                Label = "Confirm",
                IsPrimary = true,
            });
            return collector.Capture(intent);
        });
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        });

        replyGenerator.CaptureSucceeded.Should().BeTrue();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("Choose one");
        ready.Outbound.Actions.Should().ContainSingle();
        ready.Outbound.Actions[0].ActionId.Should().Be("confirm");
    }

    [Fact]
    public async Task HandleStartAsync_NonRelayTurnDoesNotEnableInteractiveScope()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => collector.Capture(new MessageContent { Text = "ignored" }))
        {
            ReplyText = "plain reply",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-2",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-2",
                Content = new MessageContent { Text = "hello" },
            },
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("plain reply");
        ready.Outbound.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitFailedReply_WhenGeneratorThrows()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-throw",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-throw",
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_failed");
        ready.ErrorSummary.Should().Be("boom");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitTimeoutFallbackReply_WhenGeneratorHangsPastBudget()
    {
        // Per-step execution records generation intent and relies on the actor-owned
        // timeout callback to terminate stale runs. The callback is explicit here so
        // the test does not block inside a fake hanging LLM provider.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            HangUntilCancelled = true,
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            DeliverSelf = false,
        };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                ResponseTimeoutSeconds = 1,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-timeout",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-timeout",
        });

        replyGenerator.WasCancelled.Should().BeFalse();
        var timeout = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunReplyGenerationTimedOut.Descriptor)).Subject;

        await runtime.HandleReplyGenerationTimedOutAsync(
            timeout.TriggerEnvelope.Payload.Unpack<AgentRunReplyGenerationTimedOut>());

        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.Reason.Should().Be("llm_reply_timeout");
        runtime.State.Status.Should().Be(AgentRunStatus.Failed);
        runtime.State.ErrorCode.Should().Be("llm_reply_timeout");
        runtime.State.ErrorSummary.Should().Contain("1s budget");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitFailedReply_WhenGeneratorReturnsEmpty()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "   ",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-empty",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-empty",
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEchoReplyTokenIntoLlmReplyReadyEvent()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-echo",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-echo",
            ReplyTokenExpiresAtUnixMs = expiresAtUnixMs,
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.RunId.Should().Be(runtime.State.RunId);
        ready.ReplyToken.Should().Be("relay-token-echo");
        ready.ReplyTokenExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropRelayRequest_WhenRunCommandCarriesNoReplyToken()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        // Relay activity but no command-carried ReplyToken — simulates a request rehydrated
        // from persisted state after a pod restart, where the original token capture is gone.
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-token",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-token");
        dropped.Reason.Should().Be("missing_relay_reply_token");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropRequest_WhenOlderThanMaxAge()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var requestedAtUnixMs = DateTimeOffset.UtcNow
            .AddMilliseconds(-(AgentRunGAgent.MaxRunRequestAgeMs + 60_000))
            .ToUnixTimeMilliseconds();
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale",
            RunId = "run-stale",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale",
            RequestedAtUnixMs = requestedAtUnixMs,
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        runtime.State.RunId.Should().Be("run-stale");
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-stale");
        dropped.Reason.Should().Be("stale_agent_run_request_dropped");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropSilently_WhenTargetActorIdMissing()
    {
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-missing",
            TargetActorId = string.Empty,
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        await actorRuntime.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleStartAsync_ShouldNotifyActor_WhenActivityMissing()
    {
        // Malformed payload (no Activity) should still tell the actor to retire its
        // pending entry — the actor decides whether to clean up. Otherwise the entry
        // accumulates silently in State.PendingLlmReplyRequests until rehydration.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-activity",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
        });

        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-activity");
        dropped.Reason.Should().Be("malformed_deferred_llm_reply_request");
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabled_DispatchesChunkEventAndReadyEvent()
    {
        // Pin the legacy edit-message path explicitly: card-mode is now the default
        // (StreamingCardKitEnabled=true) and emits a structurally distinct
        // LlmReplyCardStreamChunkEvent. This test specifically exercises the
        // text-edit chunk shape, so opt out of card mode here.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "streamed reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        const long replyTokenExpiresAtUnixMs = 1770000000000;
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingFlushIntervalMs = 0,
                StreamingCardKitEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stream",
            ReplyTokenExpiresAtUnixMs = replyTokenExpiresAtUnixMs,
        });

        handled.Any(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor)).Should().BeTrue();
        handled.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = handled.First(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("streamed reply");
        chunk.CorrelationId.Should().Be("corr-stream");
        chunk.ReplyToken.Should().Be("relay-token-stream");
        chunk.ReplyTokenExpiresAtUnixMs.Should().Be(replyTokenExpiresAtUnixMs);
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabled_CoalescesDuplicateAndThrottledSnapshotsUntilFinal()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "abc",
            StreamingSnapshots = ["a", "a", "ab", "abc"],
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_stream_coalesce");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingFlushIntervalMs = 750,
                StreamingMaxInterimChunks = 10,
                StreamingCardKitEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stream-coalesce",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stream-coalesce",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        var chunks = handled
            .Where(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor))
            .Select(e => e.Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText)
            .ToList();
        chunks.Should().Equal("a", "abc");
        handled.Last().Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabled_InterimCapDoesNotSuppressFinalChunk()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "first second final",
            StreamingSnapshots = ["first", "first second", "first second final"],
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_stream_cap");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingFlushIntervalMs = 0,
                StreamingMaxInterimChunks = 1,
                StreamingCardKitEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stream-cap",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stream-cap",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        var chunks = handled
            .Where(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor))
            .Select(e => e.Payload.Unpack<LlmReplyStreamChunkEvent>().AccumulatedText)
            .ToList();
        chunks.Should().Equal("first", "first second final");
        handled.Last().Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabledWithDefaultCardMode_DispatchesCardChunkEvent()
    {
        // Pinning the new default: StreamingCardKitEnabled=true causes the sink to emit
        // the card-mode chunk type, exercising the CardKit lifecycle entrypoint without
        // needing a real ChannelCardConversationTurnRunner wired up (the actor is mocked,
        // so we only verify the run actor dispatched the right proto type to the actor).
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "card streamed reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_2");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        const long replyTokenExpiresAtUnixMs = 1770000000001;
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingCardKitFlushIntervalMs = 0,
                // StreamingCardKitEnabled defaults to true.
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-card-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-card-stream",
            ReplyTokenExpiresAtUnixMs = replyTokenExpiresAtUnixMs,
        });

        handled.Any(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor)).Should().BeTrue();
        handled.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = handled.First(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyCardStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("card streamed reply");
        chunk.CorrelationId.Should().Be("corr-card-stream");
        chunk.ReplyToken.Should().Be("relay-token-card-stream");
        chunk.ReplyTokenExpiresAtUnixMs.Should().Be(replyTokenExpiresAtUnixMs);
    }

    [Fact]
    public async Task HandleStartAsync_StreamingDisabledFlag_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = false });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-legacy",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-legacy",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        handled.Should().ContainSingle();
        handled[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabledButNonRelay_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:dm:user");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-nonrelay",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-nonrelay",
                Content = new MessageContent { Text = "hello" },
                // No OutboundDelivery → not a relay turn
            },
        });

        handled.Should().ContainSingle();
        handled[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldApplyBotOwnerLlmConfig_FromUserConfigQueryPort()
    {
        // Bot owner's LLM model + route comes from UserConfig (the same store that backs
        // their nyxid-chat preferences), looked up by the scope id resolved from the
        // bot registration. The relay turn uses the inbound user-token as the bearer
        // (it is the bot owner's own NyxID session, freshly issued per callback) while
        // taking model / route / max-tool-rounds from the owner's pre-configured
        // UserConfig.
        LLMControlContext? capturedControl = null;
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            LlmControlObserver = control => capturedControl = control,
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));

        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-bot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("scope-bot-owner"));

        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-bot-owner", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11)));

        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            scopeResolver,
            userConfigQueryPort);

        var activity = BuildRelayActivity();
        activity.Bot = BotInstanceId.From("api-key-bot");
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bot-owner",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-bot-owner",
        });

        capturedControl.Should().NotBeNull();
        capturedControl!.ModelOverride.Should().Be("gpt-4o-bot-owner");
        capturedControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/anthropic-via-bot-owner");
        capturedControl.MaxToolRoundsOverride.Should().Be(11);
        capturedControl.NyxIdAccessToken.Should().Be("bot-owner-session-jwt");
        capturedControl.NyxIdOrgToken.Should().Be("bot-owner-session-jwt");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldThreadBotOwnerSessionTokenAsLlmBearer()
    {
        // The inbound X-NyxID-User-Token is the bot owner's own NyxID session JWT.
        // It is the credential that would authorize the owner's LLM calls in
        // nyxid-chat, so it is also the correct credential for the bot's relay
        // LLM call. The stale-pending GC plus the direct-enqueue + run-echoed
        // token flow keeps it fresh through the window where the LLM call actually
        // fires.
        LLMControlContext? capturedControl = null;
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            LlmControlObserver = control => capturedControl = control,
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));

        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var activity = BuildRelayActivity();
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bearer",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-1",
        });

        capturedControl.Should().NotBeNull();
        capturedControl!.NyxIdAccessToken.Should().Be("bot-owner-session-jwt");
        capturedControl.NyxIdOrgToken.Should().Be("bot-owner-session-jwt");
    }

    private static AgentRunGAgent CreateRunAgent(
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? collector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        IEventPublisher? eventPublisher = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var dispatchPort = actorRuntime as IActorDispatchPort ?? Substitute.For<IActorDispatchPort>();
        var generationExecutor = new RecordingReplyGenerationExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            relayOptions,
            scopeResolver,
            userConfigQueryPort);
        var agent = new AgentRunGAgent(
            actorRuntime,
            generationExecutor,
            relayOptions,
            NullLogger<AgentRunGAgent>.Instance,
            callbackScheduler);
        SetId(agent, AgentRunActorIds.ForRun(AgentRunId.New()));
        if (actorRuntime is DispatchingActorRuntime dispatchingActorRuntime)
            dispatchingActorRuntime.Register(agent.Id, new AgentActorAdapter(agent));
        generationExecutor.Bind(agent);
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeAgentTransition(agent, current, evt));
        agent.EventPublisher = eventPublisher ?? new DispatchingEventPublisher(actorRuntime);
        return agent;
    }

    private static AgentRunGAgent CreateRunAgentWithExecutor(
        IActorRuntime actorRuntime,
        IAgentRunReplyGenerationExecutorPort generationExecutor,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
        IEventPublisher? eventPublisher = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var agent = new AgentRunGAgent(
            actorRuntime,
            generationExecutor,
            relayOptions,
            NullLogger<AgentRunGAgent>.Instance,
            callbackScheduler);
        SetId(agent, AgentRunActorIds.ForRun(AgentRunId.New()));
        if (actorRuntime is DispatchingActorRuntime dispatchingActorRuntime)
            dispatchingActorRuntime.Register(agent.Id, new AgentActorAdapter(agent));
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeAgentTransition(agent, current, evt));
        agent.EventPublisher = eventPublisher ?? new DispatchingEventPublisher(actorRuntime);
        return agent;
    }

    private static void AttachScheduler(AgentRunGAgent agent, RecordingCallbackScheduler scheduler)
    {
        agent.Services = new ServiceCollection()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();
    }

    private static AgentRunGAgentState InvokeAgentTransition(
        AgentRunGAgent agent,
        AgentRunGAgentState current,
        IMessage evt)
    {
        var currentType = agent.GetType();
        while (currentType is not null)
        {
            var transitionMethod = currentType.GetMethod(
                "TransitionState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (transitionMethod is not null)
                return (AgentRunGAgentState)transitionMethod.Invoke(agent, [current, evt])!;

            currentType = currentType.BaseType;
        }

        throw new InvalidOperationException("Unable to invoke AgentRunGAgent transition via reflection.");
    }

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private static ChatActivity BuildRelayActivity(string id = "msg-1", string text = "hello") =>
        new()
        {
            Id = id,
            ChannelId = ChannelId.From("lark"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Content = new MessageContent { Text = text },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-1",
            },
        };

    private static AgentRunReplyStepState NewStepState(
        NeedsLlmReplyEvent request,
        int nextStepIndex,
        int attempt = 1,
        bool pendingTool = false)
    {
        var state = new AgentRunReplyStepState
        {
            RunId = request.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Attempt = attempt,
            NextStepIndex = nextStepIndex,
            MaxToolRounds = 40,
            Messages =
            {
                new AgentRunChatMessage { Role = "system", Content = "system" },
                new AgentRunChatMessage { Role = "user", Content = "hello" },
            },
        };
        if (pendingTool)
        {
            state.PendingToolCalls.Add(new AgentRunToolCall
            {
                Id = "tool-call-1",
                Name = "lookup",
                ArgumentsJson = "{}",
            });
        }

        return state;
    }

    private sealed class DispatchingActorRuntime(params (string Id, IActor Actor)[] actors) :
        IActorRuntime,
        IActorDispatchPort
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(
            static pair => pair.Id,
            static pair => pair.Actor,
            StringComparer.Ordinal);
        private IActor? _runActor;

        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public List<string> DestroyedIds { get; } = [];

        public void Register(string id, IActor actor)
        {
            _actors[id] = actor;
            _runActor = actor;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            if (_actors.TryGetValue(actorId, out var existing))
                return Task.FromResult(existing);

            var actor = Substitute.For<IActor>();
            actor.Id.Returns(actorId);
            _actors[actorId] = actor;
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<ConversationGAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedIds.Add(id);
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : _runActor);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            if (!_actors.TryGetValue(actorId, out var actor))
                throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class AgentActorAdapter(AgentRunGAgent agent) : IActor
    {
        public string Id => agent.Id;

        public IAgent Agent => agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload is not null)
            {
                if (envelope.Payload.Is(AgentRunNextLlmStepRequestedEvent.Descriptor))
                    return agent.HandleNextLlmStepAsync(envelope.Payload.Unpack<AgentRunNextLlmStepRequestedEvent>());
                if (envelope.Payload.Is(AgentRunNextToolStepRequestedEvent.Descriptor))
                    return agent.HandleNextToolStepAsync(envelope.Payload.Unpack<AgentRunNextToolStepRequestedEvent>());
                if (envelope.Payload.Is(AgentRunReplyGenerationFailed.Descriptor))
                    return agent.HandleReplyGenerationFailedAsync(envelope.Payload.Unpack<AgentRunReplyGenerationFailed>());
            }

            return agent.HandleEventAsync(envelope, ct);
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class PausedReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public List<AgentRunReplyGenerationExecutionRequest> InitialSteps { get; } = [];

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            InitialSteps.Add(request with { Request = request.Request.Clone() });
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 40,
            });
        }

        public Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });
    }

    private class RecordingStepGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public List<AgentRunReplyGenerationExecutionRequest> InitialSteps { get; } = [];

        public List<AgentRunReplyStepExecutionRequest> LlmSteps { get; } = [];

        public List<AgentRunReplyStepExecutionRequest> ToolSteps { get; } = [];

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            InitialSteps.Add(request with { Request = request.Request.Clone() });
            return Task.FromResult(NewStepState(request.Request, nextStepIndex: 1, attempt: request.Attempt));
        }

        public virtual Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            LlmSteps.Add(Clone(request));
            return Task.CompletedTask;
        }

        public virtual Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            ToolSteps.Add(Clone(request));
            return Task.CompletedTask;
        }

        protected static AgentRunReplyStepExecutionRequest Clone(AgentRunReplyStepExecutionRequest request) =>
            request with
            {
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            };

        public virtual Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });

        public virtual Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });
    }

    private sealed class ScriptedStepGenerationExecutor : RecordingStepGenerationExecutor
    {
        private AgentRunGAgent? _agent;

        public void Bind(AgentRunGAgent agent) => _agent = agent;

        public override async Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            await base.ExecuteLlmStepAsync(request, ct);
            var agent = _agent ?? throw new InvalidOperationException("AgentRunGAgent test executor was not bound.");
            var nextState = request.StepState.Clone();
            nextState.NextStepIndex = request.StepIndex + 1;
            nextState.PendingToolCalls.Clear();

            if (LlmSteps.Count == 1)
            {
                nextState.Messages.Add(new AgentRunChatMessage
                {
                    Role = "assistant",
                    ToolCalls =
                    {
                        new AgentRunToolCall { Id = "tool-call-1", Name = "lookup", ArgumentsJson = "{}" },
                    },
                });
                nextState.PendingToolCalls.Add(new AgentRunToolCall
                {
                    Id = "tool-call-1",
                    Name = "lookup",
                    ArgumentsJson = "{}",
                });
            }
            else
            {
                nextState.AccumulatedText = "final answer after tool";
                nextState.Messages.Add(new AgentRunChatMessage
                {
                    Role = "assistant",
                    Content = "final answer after tool",
                });
            }

            await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = nextState,
            });
        }

        public override async Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            await base.ExecuteToolStepAsync(request, ct);
            var agent = _agent ?? throw new InvalidOperationException("AgentRunGAgent test executor was not bound.");
            var nextState = request.StepState.Clone();
            nextState.NextStepIndex = request.StepIndex + 1;
            nextState.Round++;
            nextState.PendingToolCalls.Clear();
            nextState.Messages.Add(new AgentRunChatMessage
            {
                Role = "tool",
                ToolCallId = "tool-call-1",
                Content = """{"result":"tool-ok"}""",
            });

            await agent.HandleNextToolStepAsync(new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                StepState = nextState,
            });
        }
    }

    private sealed class RecordingReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        private readonly AgentRunReplyGenerationExecutor _inner;
        private AgentRunGAgent? _agent;

        public RecordingReplyGenerationExecutor(
            IActorDispatchPort dispatchPort,
            IConversationReplyGenerator replyGenerator,
            IInteractiveReplyCollector? collector,
            Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
            INyxIdRelayScopeResolver? scopeResolver,
            IUserConfigQueryPort? userConfigQueryPort)
        {
            _inner = new AgentRunReplyGenerationExecutor(
                dispatchPort,
                new ImmediateBusinessIoExecutor(),
                replyGenerator,
                collector,
                relayOptions,
                NullLogger<AgentRunReplyGenerationExecutor>.Instance,
                scopeResolver,
                userConfigQueryPort);
            DispatchPort = dispatchPort;
        }

        public IActorDispatchPort DispatchPort { get; }

        public List<AgentRunReplyGenerationExecutionRequest> InitialSteps { get; } = [];

        public void Bind(AgentRunGAgent agent) => _agent = agent;

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            InitialSteps.Add(request with { Request = request.Request.Clone() });
            return _inner.BuildInitialStepStateAsync(request, ct);
        }

        public async Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            var agent = _agent ?? throw new InvalidOperationException("AgentRunGAgent test executor was not bound.");
            AgentRunNextLlmStepRequestedEvent next;
            try
            {
                next = await BuildLlmStepContinuationAsync(request, ct);
            }
            catch (Exception ex)
            {
                await agent.HandleReplyGenerationFailedAsync(BuildFailure(request, ex));
                return;
            }

            await agent.HandleNextLlmStepAsync(next);
        }

        public async Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct)
        {
            var agent = _agent ?? throw new InvalidOperationException("AgentRunGAgent test executor was not bound.");
            AgentRunNextToolStepRequestedEvent next;
            try
            {
                next = await BuildToolStepContinuationAsync(request, ct);
            }
            catch (Exception ex)
            {
                await agent.HandleReplyGenerationFailedAsync(BuildFailure(request, ex));
                return;
            }

            await agent.HandleNextToolStepAsync(next);
        }

        public Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            _inner.BuildLlmStepContinuationAsync(request, ct);

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            _inner.BuildToolStepContinuationAsync(request, ct);

        private static AgentRunReplyGenerationFailed BuildFailure(
            AgentRunReplyStepExecutionRequest request,
            Exception ex) =>
            new()
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                ErrorCode = "llm_reply_failed",
                ErrorSummary = ex.Message,
                Attempt = request.Attempt,
                Request = request.Request.Clone(),
            };
    }

    private sealed class RealExecutorStepReplyGenerator : IAgentRunStepConversationReplyGenerator
    {
        private readonly DeterministicLookupTool _tool;

        public RealExecutorStepReplyGenerator(DeterministicToolRoundProvider provider, DeterministicLookupTool tool)
        {
            _tool = tool;
            var tools = new ToolManager();
            tools.Register(tool);
            var runtime = new ChatRuntime(
                () => provider,
                new Aevatar.AI.Core.Chat.ChatHistory(),
                new ToolCallLoop(tools),
                hooks: null,
                requestBuilder: () => new LLMRequest
                {
                    Messages = [ChatMessage.System("system")],
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["test"] = "real-executor",
                    },
                    ToolContext = AgentToolExecutionContext.Empty,
                    LlmControl = LLMControlContext.Empty,
                    Tools = [tool],
                });
            StepExecutor = runtime.CreateStepExecutor();
            Executor = new AgentRunReplyGenerationExecutor(
                new RecordingActorDispatchPort(),
                new ImmediateBusinessIoExecutor(),
                this,
                new AsyncLocalInteractiveReplyCollector(),
                new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
                {
                    InteractiveRepliesEnabled = true,
                    StreamingRepliesEnabled = false,
                },
                NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        }

        public ChatRuntimeStepExecutor StepExecutor { get; }

        public AgentRunReplyGenerationExecutor Executor { get; }

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            Task.FromResult(new ConversationReplyResult(string.Empty, Usage: null, FinishReason: null));

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            Task.FromResult(new ConversationReplyResult(string.Empty, Usage: null, FinishReason: null));

        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunReplyStepPlan(
                StepExecutor,
                new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                [
                    ChatMessage.System("system"),
                    ChatMessage.User([ContentPart.TextPart(activity.Content.Text)], activity.Content.Text),
                ],
                1));

        public MessageContent? TryTakeOutboundIntent() => null;
    }

    private sealed class DeterministicToolRoundProvider : ILLMProvider
    {
        public string Name => "deterministic-tool-round";

        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (request.Tools is null)
            {
                yield return new LLMStreamChunk { DeltaContent = "final after tool" };
                yield return new LLMStreamChunk
                {
                    IsLast = true,
                    FinishReason = "stop",
                    Usage = new TokenUsage(3, 4, 7),
                };
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "tool-call-real",
                    Name = "lookup",
                    ArgumentsJson = """{"q":"aevatar"}""",
                },
            };
            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = "tool_calls",
                Usage = new TokenUsage(1, 2, 3),
            };
        }
    }

    private sealed class DeterministicLookupTool : IAgentTool
    {
        public string Name => "lookup";

        public string Description => "deterministic lookup";

        public string ParametersSchema => "{}";

        public List<string> Arguments { get; } = [];

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Arguments.Add(argumentsJson);
            return Task.FromResult("""{"result":"tool-ok:aevatar"}""");
        }
    }

    private sealed class ImmediateBusinessIoExecutor : ILongRunningBusinessIoExecutor
    {
        public async Task SubmitAsync(LongRunningBusinessIoWorkItem workItem, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (workItem.Timeout > TimeSpan.Zero)
                timeoutCts.CancelAfter(workItem.Timeout);
            await workItem.ExecuteAsync(timeoutCts.Token);
        }
    }

    private sealed class ThrowingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            throw new InvalidOperationException("simulated enqueue failure");
        }
    }

    private sealed class FailingOnceGetActorRuntime(params (string Id, IActor Actor)[] actors) : IActorRuntime
    {
        private readonly DispatchingActorRuntime _inner = new(actors);
        private bool _failNextGet = true;

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            _inner.CreateAsync<TAgent>(id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            _inner.CreateAsync(agentType, id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            _inner.DestroyAsync(id, ct);

        public Task<IActor?> GetAsync(string id)
        {
            if (_failNextGet)
            {
                _failNextGet = false;
                throw new InvalidOperationException("actor runtime lookup failed");
            }

            return _inner.GetAsync(id);
        }

        public Task<bool> ExistsAsync(string id) => _inner.ExistsAsync(id);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            _inner.LinkAsync(parentId, childId, ct);

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            _inner.UnlinkAsync(childId, ct);
    }

    /// <summary>
    /// Test stub that fails <see cref="ConfirmEventsAsync"/> only when an event of type
    /// <typeparamref name="TFailEvent"/> is in the pending list. Used to simulate
    /// "persistence succeeded for produced event but failed for dispatched event" so we
    /// can verify the actor does NOT escalate that into a duplicate fallback reply.
    /// </summary>
    private sealed class FailOnEventTypeSourcing<TState, TFailEvent>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
        where TFailEvent : IMessage
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            if (_pending.OfType<TFailEvent>().Any())
            {
                _pending.Clear();
                throw new InvalidOperationException(
                    $"Simulated persistence failure for event type {typeof(TFailEvent).Name}");
            }
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult { LatestVersion = CurrentVersion });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents() => _pending.Clear();

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
    }

    private sealed class StateTransitionEventSourcing<TState>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                LatestVersion = CurrentVersion,
            });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public List<RuntimeCallbackTimerRequest> Timers { get; } = [];

        public List<RuntimeCallbackLease> Cancelled { get; } = [];

        public List<string> PurgedActorIds { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            Timers.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timers.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            Cancelled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            PurgedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class DispatchingEventPublisher(IActorRuntime actorRuntime) : IEventPublisher
    {
        public bool FailNextSend { get; set; }

        public bool DeliverSelf { get; set; } = true;

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public List<(TopologyAudience Audience, IMessage Event)> Published { get; } = [];

        public async Task PublishAsync<T>(
            T e,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            Published.Add((audience, e));
            if (audience is not TopologyAudience.Self)
                return;
            if (!DeliverSelf)
                return;

            var targetActorId = sourceEnvelope?.Route?.PublisherActorId;
            if (string.IsNullOrWhiteSpace(targetActorId))
            {
                targetActorId = e switch
                {
                    AgentRunNextLlmStepRequestedEvent llm => AgentRunActorIds.ForRun(AgentRunId.Parse(llm.RunId)),
                    AgentRunNextToolStepRequestedEvent tool => AgentRunActorIds.ForRun(AgentRunId.Parse(tool.RunId)),
                    _ => null,
                };
            }

            if (string.IsNullOrWhiteSpace(targetActorId))
                return;

            var actor = await actorRuntime.GetAsync(targetActorId)
                        ?? throw new InvalidOperationException($"Actor {targetActorId} not found.");
            await actor.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(e),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(targetActorId, TopologyAudience.Self),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = sourceEnvelope?.Propagation?.CorrelationId ?? string.Empty,
                },
            }, c);
        }

        public async Task SendToAsync<T>(
            string targetActorId,
            T e,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new InvalidOperationException("send not accepted");
            }

            Sent.Add((targetActorId, e));
            var actor = await actorRuntime.GetAsync(targetActorId)
                        ?? throw new InvalidOperationException($"Actor {targetActorId} not found.");
            await actor.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(e),
                Route = EnvelopeRouteSemantics.CreateDirect("agent-run-test-publisher", targetActorId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = sourceEnvelope?.Propagation?.CorrelationId ?? string.Empty,
                },
            }, c);
        }
    }

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : IAgentRunStepConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public int CallCount { get; private set; }

        public bool CaptureSucceeded { get; private set; }

        public Action<IReadOnlyDictionary<string, string>>? MetadataObserver { get; init; }

        public Action<LLMControlContext>? LlmControlObserver { get; init; }

        public Action<AgentToolExecutionContext>? ToolContextObserver { get; init; }

        public IReadOnlyList<string>? StreamingSnapshots { get; init; }

        public Exception? ExceptionToThrow { get; init; }

        public bool HangUntilCancelled { get; init; }

        public bool WasCancelled { get; private set; }

        public MessageContent? CapturedIntent { get; private set; }

        public async Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            await GenerateReplyAsync(activity, metadata, null, null, streamingSink, ct);

        public async Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;
            if (HangUntilCancelled)
            {
                var pendingReply = new TaskCompletionSource<ConversationReplyResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellationRegistration = ct.Register(() =>
                {
                    WasCancelled = true;
                    pendingReply.TrySetCanceled(ct);
                });

                return await pendingReply.Task;
            }

            CallCount++;
            CaptureSucceeded = captureAction();
            MetadataObserver?.Invoke(metadata);
            if (llmControl is not null)
                LlmControlObserver?.Invoke(llmControl);
            if (toolContext is not null)
                ToolContextObserver?.Invoke(toolContext);
            if (streamingSink is not null)
            {
                if (StreamingSnapshots is { Count: > 0 })
                {
                    foreach (var snapshot in StreamingSnapshots)
                        await streamingSink.OnDeltaAsync(snapshot, ct);
                }
                else if (!string.IsNullOrEmpty(ReplyText))
                {
                    await streamingSink.OnDeltaAsync(ReplyText, ct);
                }
            }
            if (CaptureInteractiveIntent(ShouldCaptureInteractiveReply(activity)))
                CaptureSucceeded = true;
            return new ConversationReplyResult(ReplyText, Usage: null, FinishReason: null);
        }

        public async Task<AgentRunTestReplyStepResult> ExecuteStepReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            bool allowInteractiveCapture,
            CancellationToken ct)
        {
            CapturedIntent = null;
            var result = await GenerateReplyAsync(activity, metadata, llmControl, toolContext, streamingSink, ct);
            CaptureSucceeded = allowInteractiveCapture && CapturedIntent is not null;
            return new AgentRunTestReplyStepResult(
                result.Text ?? string.Empty,
                CapturedIntent?.Clone(),
                result.FinishReason,
                result.Usage is null
                    ? null
                    : new TokenUsage(
                        result.Usage.PromptTokens,
                        result.Usage.CompletionTokens,
                        result.Usage.TotalTokens));
        }

        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            CancellationToken ct)
        {
            MetadataObserver?.Invoke(metadata);
            if (llmControl is not null)
                LlmControlObserver?.Invoke(llmControl);
            if (toolContext is not null)
                ToolContextObserver?.Invoke(toolContext);

            var provider = new RecordingReplyStepProvider(this, activity);
            var tools = new Aevatar.AI.Core.Tools.ToolManager();
            var runtime = new Aevatar.AI.Core.Chat.ChatRuntime(
                () => provider,
                new Aevatar.AI.Core.Chat.ChatHistory(),
                new Aevatar.AI.Core.Tools.ToolCallLoop(tools),
                hooks: null,
                requestBuilder: () => new LLMRequest
                {
                    Messages = [ChatMessage.System("system")],
                    Metadata = metadata,
                    ToolContext = toolContext,
                    LlmControl = llmControl,
                });
            return Task.FromResult(new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(),
                metadata,
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                [
                    ChatMessage.System("system"),
                    ChatMessage.User([ContentPart.TextPart(activity.Content.Text)], activity.Content.Text),
                ],
                llmControl?.MaxToolRoundsOverride is > 0 ? llmControl.MaxToolRoundsOverride.Value : 40));
        }

        public MessageContent? TryTakeOutboundIntent()
        {
            var intent = CapturedIntent;
            CapturedIntent = null;
            return intent?.Clone();
        }

        private bool CaptureInteractiveIntent(bool allowInteractiveCapture)
        {
            var captured = captureAction();
            if (allowInteractiveCapture && captured)
            {
                CapturedIntent = new MessageContent { Text = "Choose one" };
                CapturedIntent.Actions.Add(new ActionElement
                {
                    Kind = ActionElementKind.Button,
                    ActionId = "confirm",
                    Label = "Confirm",
                    IsPrimary = true,
                });
            }

            return allowInteractiveCapture && captured;
        }

        private static bool ShouldCaptureInteractiveReply(ChatActivity activity) =>
            activity.OutboundDelivery is
            {
                ReplyMessageId.Length: > 0,
                CorrelationId.Length: > 0,
            };

        private sealed class RecordingReplyStepProvider(RecordingReplyGenerator owner, ChatActivity activity) : ILLMProvider
        {
            public string Name => "recording-reply-step";

            public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                var streamingState = new RecordingStepStreamingSink();
                var result = await owner.ExecuteStepReplyAsync(
                        activity,
                        request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
                        request.LlmControl,
                        request.ToolContext,
                        streamingState,
                        ShouldCaptureInteractiveReply(activity),
                        ct)
                    .ConfigureAwait(false);

                var snapshots = streamingState.Snapshots.Count > 0
                    ? streamingState.Snapshots
                    : string.IsNullOrEmpty(result.ReplyText) ? [] : new List<string> { result.ReplyText };
                var previous = string.Empty;
                foreach (var snapshot in snapshots)
                {
                    var delta = snapshot.StartsWith(previous, StringComparison.Ordinal)
                        ? snapshot[previous.Length..]
                        : snapshot;
                    previous = snapshot;
                    yield return new LLMStreamChunk { DeltaContent = delta };
                }

                yield return new LLMStreamChunk { IsLast = true, FinishReason = result.FinishReason };
            }
        }

        private sealed class RecordingStepStreamingSink : IStreamingReplySink
        {
            public List<string> Snapshots { get; } = [];

            public Task OnDeltaAsync(string accumulatedText, CancellationToken ct)
            {
                Snapshots.Add(accumulatedText);
                return Task.CompletedTask;
            }
        }
    }

    private sealed record AgentRunTestReplyStepResult(
        string ReplyText,
        MessageContent? OutboundIntent,
        string? FinishReason,
        TokenUsage? Usage);
}

internal static class AgentRunGAgentTestExtensions
{
    public static Task HandleStartAsync(this AgentRunGAgent agent, NeedsLlmReplyEvent request) =>
        agent.HandleStartAsync(new AgentRunStartRequested
        {
            Request = WithRunId(agent, request),
        });

    private static NeedsLlmReplyEvent WithRunId(AgentRunGAgent agent, NeedsLlmReplyEvent request)
    {
        var clone = request.Clone();
        if (string.IsNullOrWhiteSpace(clone.RunId))
        {
            clone.RunId = AgentRunActorIds.TryGetRunId(agent.Id, out var runId)
                ? runId.Value
                : "run-" + (string.IsNullOrWhiteSpace(clone.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : clone.CorrelationId.Trim());
        }

        return clone;
    }
}
