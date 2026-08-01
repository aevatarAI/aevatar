using System.Text;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.Skills;
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
    private static readonly ConditionalWeakTable<AgentRunGAgent, RecordingReplyGenerationExecutor> RecordingExecutors = new();
    private static readonly IBuiltInPromptFloorProvider BuiltInPromptFloorProvider =
        new ConversationReplyGeneratorTests.StubBuiltInPromptFloorProvider("built-in prompt floor");

    internal static Task DrainRecordingExecutorAsync(AgentRunGAgent agent) =>
        RecordingExecutors.TryGetValue(agent, out var executor)
            ? executor.DrainAsync(agent.State)
            : Task.CompletedTask;

    [Fact]
    public void StripInlineMediaPayloads_ShouldRemoveMediaDataBase64FromDurableStepState()
    {
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-media",
        };
        stepState.Messages.Add(new AgentRunChatMessage
        {
            Role = "user",
            ContentParts =
            {
                new Aevatar.AI.Abstractions.ChatContentPart
                {
                    Kind = Aevatar.AI.Abstractions.ChatContentPartKind.Text,
                    Text = "describe",
                },
                new Aevatar.AI.Abstractions.ChatContentPart
                {
                    Kind = Aevatar.AI.Abstractions.ChatContentPartKind.Text,
                    Text = "large extracted document text",
                    FileRef = new Aevatar.AI.Abstractions.ChatFileRef
                    {
                        ArtifactId = "workflow-file://wf-file-document",
                    },
                },
                new Aevatar.AI.Abstractions.ChatContentPart
                {
                    Kind = Aevatar.AI.Abstractions.ChatContentPartKind.Image,
                    DataBase64 = "large-image-base64",
                    FileRef = new Aevatar.AI.Abstractions.ChatFileRef
                    {
                        ArtifactId = "workflow-file://wf-file-1",
                    },
                },
            },
        });
        stepState.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "user",
            ContentParts =
            {
                new Aevatar.AI.Abstractions.ChatContentPart
                {
                    Kind = Aevatar.AI.Abstractions.ChatContentPartKind.Audio,
                    DataBase64 = "large-audio-base64",
                },
                new Aevatar.AI.Abstractions.ChatContentPart
                {
                    Kind = Aevatar.AI.Abstractions.ChatContentPartKind.Unspecified,
                    DataBase64 = "large-attachment-base64",
                },
            },
        });

        var sanitized = AgentRunGAgent.StripInlineMediaPayloads(stepState);

        sanitized.Messages.Single().ContentParts[0].Text.Should().Be("describe");
        sanitized.Messages.Single().ContentParts[1].Text.Should().BeEmpty();
        sanitized.Messages.Single().ContentParts[1].FileRef.ArtifactId.Should().Be("workflow-file://wf-file-document");
        sanitized.Messages.Single().ContentParts[2].DataBase64.Should().BeEmpty();
        sanitized.Messages.Single().ContentParts[2].FileRef.ArtifactId.Should().Be("workflow-file://wf-file-1");
        sanitized.AppendedHistory.Single().ContentParts.Should().OnlyContain(part => part.DataBase64.Length == 0);
        stepState.Messages.Single().ContentParts[1].Text.Should().Be("large extracted document text");
        stepState.Messages.Single().ContentParts[2].DataBase64.Should().Be("large-image-base64");
    }

    [Fact]
    public async Task HandleNextToolStepAsync_MergesToolStepOutboundIntentIntoStepState()
    {
        // Regression for the relay interactive-reply scope gap: reply_with_interaction
        // executes during the tool step, so its captured intent arrives on
        // AgentRunToolStepResult.outbound_intent and must be merged into the persisted
        // step state for the finalize path to dispatch the card.
        var actorRuntime = new DispatchingActorRuntime();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            new PausedReplyGenerationExecutor(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-tool-intent",
            CorrelationId = "corr-tool-intent",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-tool-intent",
                CorrelationId = "corr-tool-intent",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 2,
                MaxToolRounds = 4,
                PendingToolCalls =
                {
                    new AgentRunToolCall
                    {
                        Id = "call-1",
                        Name = "reply_with_interaction",
                        ArgumentsJson = "{}",
                    },
                },
            },
        });

        await runtime.HandleNextToolStepAsync(new AgentRunNextToolStepRequestedEvent
        {
            RunId = "run-tool-intent",
            CorrelationId = "corr-tool-intent",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 3,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-tool-intent",
                RunId = "run-tool-intent",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            ToolStepResult = new AgentRunToolStepResult
            {
                AdvanceRound = true,
                OutboundIntent = new MessageContent
                {
                    Text = "确认部署到 staging?",
                    Actions =
                    {
                        new ActionElement { ActionId = "confirm_deploy", Label = "确认部署" },
                    },
                },
            },
        });

        runtime.State.GenerationStep.Should().NotBeNull();
        runtime.State.GenerationStep!.OutboundIntent.Should().NotBeNull();
        runtime.State.GenerationStep.OutboundIntent.Actions.Should()
            .ContainSingle(action => action.ActionId == "confirm_deploy");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_WithToolCalls_ShouldPersistLlmFactsBeforeRequestingToolStep()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-reconciled-tool",
            CorrelationId = "corr-reconciled-tool",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-reconciled-tool",
                CorrelationId = "corr-reconciled-tool",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-reconciled-tool",
            CorrelationId = "corr-reconciled-tool",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-reconciled-tool",
                RunId = "run-reconciled-tool",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                ToolCalls =
                {
                    new AgentRunToolCall
                    {
                        Id = "call-1",
                        Name = "use_skill",
                        ArgumentsJson = "{}",
                    },
                },
            },
        });

        var step = runtime.State.GenerationStep;
        step.Should().NotBeNull();
        step!.NextStepIndex.Should().Be(2);
        step.Round.Should().Be(0);
        step.PendingToolCalls.Should().ContainSingle(call => call.Id == "call-1");
        step.Messages.Should().Contain(message => message.Role == "assistant" && message.ToolCalls.Count == 1);
        step.Messages.Should().NotContain(message => message.Role == "tool");
        executor.ToolStepExecutions.Should().ContainSingle();
        executor.ToolStepExecutions.Single().StepState.NextStepIndex.Should().Be(2,
            "the LLM waterline must be committed before the actor starts any tool side effect");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_ReasoningOnlyEmptyStep_RetriesOnceKeepingToolsInsteadOfFailing()
    {
        // Regression for the prod incident where a reasoning model spent the whole
        // step on reasoning tokens (content empty, reasoning_content set, no tool
        // calls, finishReason=stop) and the run terminated as empty_reply with the
        // generic apology. A reasoning-only step must get exactly one bounded
        // no-tools retry before failing.
        var actorRuntime = new DispatchingActorRuntime();
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-reasoning-only",
            CorrelationId = "corr-reasoning-only",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-reasoning-only",
                CorrelationId = "corr-reasoning-only",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-reasoning-only",
            CorrelationId = "corr-reasoning-only",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-reasoning-only",
                RunId = "run-reasoning-only",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty,
                Content = string.Empty,
                ReasoningContent = "internal chain of thought without an answer",
                FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        var step = runtime.State.GenerationStep;
        step.Should().NotBeNull();
        step!.EmptyReplyRetry.Should().BeTrue("a reasoning-only step must advance to the bounded empty-reply retry");
        step.FinalNoToolsStep.Should().BeFalse("the empty-reply retry keeps tools — it must NOT be a no-tools step");
        step.NextStepIndex.Should().Be(3);
        executor.LlmStepExecutions.Should().ContainSingle("the run must re-dispatch one LLM retry step");
        executor.LlmStepExecutions[0].StepState.EmptyReplyRetry.Should().BeTrue();
        executor.LlmStepExecutions[0].StepState.FinalNoToolsStep.Should().BeFalse();
        var nudge = step.Messages[^1];
        nudge.Role.Should().Be("user");
        nudge.Content.Should().NotBeNullOrWhiteSpace();
        step.AppendedHistory.Should().NotContain(
            entry => entry.Content == nudge.Content,
            "the synthetic nudge must not leak into the durable conversation history");
        runtime.State.Status.Should().Be(
            AgentRunStatus.ReplyGenerationRequested,
            "the run must not terminate while the retry step is in flight");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_EmptyStepWithoutCapturedReasoning_StillRetriesOnce()
    {
        // Regression for the 2026-06-12 prod incident: deepseek slash-skill turns
        // completed with finishReason=stop, no text, no tool calls AND no captured
        // ReasoningContent (reasoning deltas are not guaranteed to survive the
        // provider boundary). The reasoning-gated retry refused to fire and every
        // run terminated as the generic apology. The recovery gate must not require
        // an observed reasoning trace.
        var actorRuntime = new DispatchingActorRuntime();
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-empty-no-reasoning",
            CorrelationId = "corr-empty-no-reasoning",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-empty-no-reasoning",
                CorrelationId = "corr-empty-no-reasoning",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-empty-no-reasoning",
            CorrelationId = "corr-empty-no-reasoning",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-empty-no-reasoning",
                RunId = "run-empty-no-reasoning",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty,
                Content = string.Empty,
                ReasoningContent = string.Empty,
                FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        var step = runtime.State.GenerationStep;
        step.Should().NotBeNull();
        step!.EmptyReplyRetry.Should().BeTrue(
            "an empty completed step must advance to the bounded empty-reply retry even when no reasoning trace was captured");
        step.FinalNoToolsStep.Should().BeFalse("the empty-reply retry keeps tools");
        executor.LlmStepExecutions.Should().ContainSingle("the run must re-dispatch one LLM retry step");
        runtime.State.Status.Should().Be(
            AgentRunStatus.ReplyGenerationRequested,
            "the run must not terminate while the retry step is in flight");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_EmptyStepWithLongHistory_TrimsRetryToRecentMessages()
    {
        // Regression: when a conversation has a very long history the first LLM attempt
        // may return empty because the context overflows the model's context window.
        // The empty-reply recovery retry must trim the history to a recent-messages floor
        // so the retry has room to produce a real answer instead of overflowing again.
        var actorRuntime = new DispatchingActorRuntime();
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime, executor, new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());

        var longStep = new AgentRunReplyStepState
        {
            RunId = "run-long", CorrelationId = "corr-long", TargetActorId = "actor-1",
            Attempt = 1, NextStepIndex = 1, MaxToolRounds = 4,
        };
        longStep.Messages.Add(new AgentRunChatMessage { Role = "system", Content = "system prompt" });
        for (var i = 0; i < 40; i++)
            longStep.Messages.Add(new AgentRunChatMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant", Content = $"m{i}",
            });

        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-long", CorrelationId = "corr-long", TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested, GenerationAttempt = 1,
            GenerationStep = longStep,
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-long", CorrelationId = "corr-long", TargetActorId = "actor-1",
            Attempt = 1, StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-long", RunId = "run-long", TargetActorId = "actor-1",
                RegistrationId = "reg-1", Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty, Content = string.Empty,
                ReasoningContent = string.Empty, FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        var retry = executor.LlmStepExecutions.Should().ContainSingle().Subject;
        retry.StepState.EmptyReplyRetry.Should().BeTrue();
        retry.StepState.FinalNoToolsStep.Should().BeFalse("the empty-reply retry keeps tools");
        retry.StepState.Messages.Count(m => m.Role == "system").Should().BeGreaterThan(0);
        // system (1) + recent floor (10) + recovery nudge (1) = 12 upper bound
        retry.StepState.Messages.Count.Should().BeLessThanOrEqualTo(12);
        retry.StepState.Messages.Count(m => m.Role != "system").Should().BeLessThanOrEqualTo(11,
            "non-system history must be trimmed to the recent floor (10) plus the recovery nudge");
        retry.StepState.Messages.Should().NotContain(m => m.Content == "m0",
            "the oldest non-system messages must be dropped to fit within the recent floor");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_ReasoningOnlyResult_StaysInStepMessagesButOutOfDurableHistory()
    {
        // Reasoning-only results keep their intra-run record (step messages feed the
        // retry request and diagnostics) but must not enter AppendedHistory: providers
        // drop bare reasoning on assistant history messages, so a persisted
        // reasoning-only entry replays as an empty assistant turn that poisons every
        // later request in the conversation.
        var actorRuntime = new DispatchingActorRuntime();
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-reasoning-history",
            CorrelationId = "corr-reasoning-history",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-reasoning-history",
                CorrelationId = "corr-reasoning-history",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-reasoning-history",
            CorrelationId = "corr-reasoning-history",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-reasoning-history",
                RunId = "run-reasoning-history",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty,
                Content = string.Empty,
                ReasoningContent = "internal chain of thought without an answer",
                FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        var step = runtime.State.GenerationStep;
        step.Should().NotBeNull();
        step!.Messages.Should().Contain(
            message => message.Role == "assistant" &&
                       message.ReasoningContent == "internal chain of thought without an answer",
            "the intra-run step record keeps the reasoning-only result");
        step.AppendedHistory.Should().NotContain(
            entry => entry.Role == "assistant",
            "reasoning-only assistant turns must not be persisted into durable conversation history");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_ReasoningOnlyEmptyStep_OnFinalNoToolsStep_FailsWithEmptyReply()
    {
        // The reasoning-only retry is bounded: when the final no-tools step itself
        // comes back reasoning-only, the run must fail as empty_reply exactly like
        // before instead of looping.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-reasoning-final",
            CorrelationId = "corr-reasoning-final",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-reasoning-final",
                CorrelationId = "corr-reasoning-final",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 3,
                MaxToolRounds = 4,
                FinalNoToolsStep = true,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-reasoning-final",
            CorrelationId = "corr-reasoning-final",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 4,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-reasoning-final",
                RunId = "run-reasoning-final",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty,
                Content = string.Empty,
                ReasoningContent = "still only reasoning on the retry step",
                FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        executor.LlmStepExecutions.Should().BeEmpty("the bounded retry must not re-dispatch a second time");
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.ErrorSummary.Should().Contain("reasoningOnly=True");
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_EmptyReplyRetryStep_FailsWithoutSecondRetry()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var executor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions());
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-empty-retry-used",
            CorrelationId = "corr-empty-retry-used",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-empty-retry-used",
                CorrelationId = "corr-empty-retry-used",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 3,
                MaxToolRounds = 4,
                EmptyReplyRetry = true,
            },
        });

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-empty-retry-used",
            CorrelationId = "corr-empty-retry-used",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 4,
            Request = new NeedsLlmReplyEvent
            {
                CorrelationId = "corr-empty-retry-used",
                RunId = "run-empty-retry-used",
                TargetActorId = "actor-1",
                RegistrationId = "reg-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = string.Empty,
                Content = string.Empty,
                ReasoningContent = string.Empty,
                FinishReason = "stop",
                HasStreamedTextContent = false,
            },
        });

        executor.LlmStepExecutions.Should().BeEmpty("EmptyReplyRetry is the one-shot recovery gate");
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.ErrorSummary.Should().Contain("Reply generator returned an empty response");
    }

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
        actorId.Should().Be(ExpectedRunActorId("run-dispatch"));
        envelope.Id.Should().Be("agent-run-start:run-dispatch");
        envelope.Runtime.DeliveryIdentity.OperationId.Should().Be("agent-run-start:run-dispatch");
        envelope.Propagation.CorrelationId.Should().Be("corr-dispatch");
        var command = envelope.Payload.Unpack<AgentRunStartRequested>();
        command.Request.RunId.Should().Be("run-dispatch");
        command.Request.CorrelationId.Should().Be("corr-dispatch");
        command.Request.TargetActorId.Should().Be("conversation-actor");
        command.Request.ReplyToken.Should().Be("relay-token-dispatch");
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
            .Should().OnlyContain(id => id == ExpectedRunActorId("run-duplicate-dispatch"));
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
        dispatchPort.Dispatches.Single().ActorId.Should().Be(ExpectedRunActorId("run-stale-dispatch"));
        (await actorRuntime.ExistsAsync(ExpectedRunActorId("run-stale-dispatch"))).Should().BeTrue();
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
    public void ApplyReplyProduced_ShouldPersistTypedToolReceiptsToRunState()
    {
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var receipt = NewPublishReceipt(status: Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success);
        var evt = new AgentRunReplyProducedEvent
        {
            RunId = "run-receipts",
            CorrelationId = "corr-receipts",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyText = "done",
            ToolReceipts = { receipt },
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), evt);

        next.Status.Should().Be(AgentRunStatus.ReplyProduced);
        next.ToolReceipts.Should().ContainSingle(x =>
            x.CallId == "call-1" &&
            x.Status == Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success &&
            x.SubjectId == "skill-1" &&
            x.SubjectHash == "hash-1");
    }

    [Fact]
    public async Task HandleStartAsync_WhenAccepted_PersistsGenerationRequestedAndHandsOffToExecutor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

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
        generationExecutor.Starts.Should().ContainSingle();
        generationExecutor.Starts[0].RunId.Should().Be("run-generation-requested");
        generationExecutor.Starts[0].RunActorId.Should().Be(runtime.Id);
    }

    [Fact]
    public async Task HandleStartAsync_WhenGenerationRequested_DoesNotStartSecondExecutor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var generationExecutor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });
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
        generationExecutor.Starts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_WhenExecutorIsSlow_ShouldKeepActorTurnUntilContinuationIsReady()
    {
        var generationExecutor = new BlockingStepExecutionExecutor();
        var runtime = CreateRunAgentWithExecutor(
            new DispatchingActorRuntime(),
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-slow-llm",
            CorrelationId = "corr-slow-llm",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-slow-llm",
                CorrelationId = "corr-slow-llm",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        var handler = runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-slow-llm",
            CorrelationId = "corr-slow-llm",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 1,
            Request = new NeedsLlmReplyEvent
            {
                RunId = "run-slow-llm",
                CorrelationId = "corr-slow-llm",
                TargetActorId = "actor-1",
                Activity = BuildRelayActivity(),
            },
        });

        await generationExecutor.LlmStarted.Task;

        handler.IsCompleted.Should().BeFalse(
            "the actor must not accept cancellation or retry between LLM capability capture and reconciliation");
        generationExecutor.CompleteLlm();
        await handler;
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_WhenToolExecutorIsSlow_ShouldKeepActorTurnUntilToolResultIsReady()
    {
        var generationExecutor = new BlockingStepExecutionExecutor();
        var runtime = CreateRunAgentWithExecutor(
            new DispatchingActorRuntime(),
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-slow-tool",
            CorrelationId = "corr-slow-tool",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-slow-tool",
                CorrelationId = "corr-slow-tool",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });

        var handler = runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-slow-tool",
            CorrelationId = "corr-slow-tool",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 2,
            Request = new NeedsLlmReplyEvent
            {
                RunId = "run-slow-tool",
                CorrelationId = "corr-slow-tool",
                TargetActorId = "actor-1",
                Activity = BuildRelayActivity(),
            },
            LlmStepResult = new AgentRunLlmStepResult
            {
                ToolCalls =
                {
                    new AgentRunToolCall
                    {
                        Id = "call-slow-tool",
                        Name = "scheduled_agent_creator",
                        ArgumentsJson = "{}",
                    },
                },
            },
        });

        await generationExecutor.ToolStarted.Task;

        handler.IsCompleted.Should().BeFalse(
            "the actor must not let cancellation or retry make an authorized side effect stale while it executes");
        generationExecutor.CompleteTool();
        await handler;
    }

    [Fact]
    public async Task HandleNextLlmStepAsync_WhenLlmContinuationIsReplayed_ShouldClearMatchingCapability()
    {
        var executor = new CapabilityTrackingReplyGenerationExecutor();
        var publisher = new RecordingSelfEventPublisher();
        var runtime = CreateCapabilityTestAgent(executor, publisher);

        await runtime.HandleNextLlmStepAsync(BuildCapabilityLlmStepRequest());
        var llmContinuation = publisher.Published.OfType<AgentRunNextLlmStepRequestedEvent>()
            .Should().ContainSingle().Subject;
        await runtime.HandleNextLlmStepAsync(llmContinuation);
        var toolRequest = publisher.Published.OfType<AgentRunNextToolStepRequestedEvent>()
            .Should().ContainSingle().Subject;

        await runtime.HandleNextLlmStepAsync(llmContinuation.Clone());
        await runtime.HandleNextToolStepAsync(toolRequest);

        executor.ToolStepAuthorizationPresence.Should().Equal(false);
        executor.ToolExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleNextToolStepAsync_WhenToolRequestArrivesBeforeLlmResult_ShouldClearMatchingCapability()
    {
        var executor = new CapabilityTrackingReplyGenerationExecutor();
        var publisher = new RecordingSelfEventPublisher();
        var runtime = CreateCapabilityTestAgent(executor, publisher);

        await runtime.HandleNextLlmStepAsync(BuildCapabilityLlmStepRequest());
        var llmContinuation = publisher.Published.OfType<AgentRunNextLlmStepRequestedEvent>()
            .Should().ContainSingle().Subject;
        var earlyToolRequest = BuildCapabilityToolStepRequest(llmContinuation);

        await runtime.HandleNextToolStepAsync(earlyToolRequest);
        await runtime.HandleNextLlmStepAsync(llmContinuation);
        var reconciledToolRequest = publisher.Published.OfType<AgentRunNextToolStepRequestedEvent>()
            .Should().ContainSingle().Subject;
        await runtime.HandleNextToolStepAsync(reconciledToolRequest);

        executor.ToolStepAuthorizationPresence.Should().Equal(false);
        executor.ToolExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleNextToolStepAsync_AfterActorRestart_ShouldRejectWhenCapabilityIsMissing()
    {
        var executor = new CapabilityTrackingReplyGenerationExecutor();
        var publisher = new RecordingSelfEventPublisher();
        var restartedRuntime = CreateCapabilityTestAgent(executor, publisher, nextStepIndex: 2);
        restartedRuntime.State.GenerationStep!.PendingToolCalls.Add(
            CapabilityTrackingReplyGenerationExecutor.ToolCall.Clone());
        var toolRequest = BuildCapabilityToolStepRequest(
            runId: restartedRuntime.State.RunId,
            correlationId: restartedRuntime.State.CorrelationId,
            stepIndex: 2);

        await restartedRuntime.HandleNextToolStepAsync(toolRequest);

        executor.ToolStepAuthorizationPresence.Should().Equal(false);
        executor.ToolExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleNextToolStepAsync_WhenRequestIsReplayed_ShouldConsumeCapabilityOnlyOnce()
    {
        var executor = new CapabilityTrackingReplyGenerationExecutor();
        var publisher = new RecordingSelfEventPublisher();
        var runtime = CreateCapabilityTestAgent(executor, publisher);

        await runtime.HandleNextLlmStepAsync(BuildCapabilityLlmStepRequest());
        var llmContinuation = publisher.Published.OfType<AgentRunNextLlmStepRequestedEvent>()
            .Should().ContainSingle().Subject;
        await runtime.HandleNextLlmStepAsync(llmContinuation);
        var toolRequest = publisher.Published.OfType<AgentRunNextToolStepRequestedEvent>()
            .Should().ContainSingle().Subject;

        await runtime.HandleNextToolStepAsync(toolRequest);
        await runtime.HandleNextToolStepAsync(toolRequest.Clone());

        executor.ToolStepAuthorizationPresence.Should().Equal(true, false);
        executor.ToolExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleReplyGenerationTimedOutAsync_ObsoleteCallback_DoesNotFailRunOrDropConversation()
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

        scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunReplyGenerationTimedOut.Descriptor));

        await runtime.HandleReplyGenerationTimedOutAsync(new AgentRunReplyGenerationTimedOut
        {
            RunId = "run-generation-timeout-race",
            CorrelationId = "corr-generation-timeout-race",
            TargetActorId = "actor-1",
            TimedOutAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attempt = generationExecutor.Starts.Single().Attempt + 1,
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        handled.Should().NotContain(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));

        await runtime.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-generation-timeout-race",
            CorrelationId = "corr-generation-timeout-race",
            TargetActorId = "actor-1",
            Attempt = generationExecutor.Starts.Single().Attempt,
            StepIndex = runtime.State.GenerationStep!.NextStepIndex + 1,
            Request = generationExecutor.Starts.Single().Request.Clone(),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "late executor reply",
                Content = "late executor reply",
                FinishReason = "stop",
            },
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Be("late executor reply");
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
        handled.Should().NotContain(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_DefaultRelayOptions_ShouldNotScheduleGenerationTimeout()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var generationExecutor = new PausedReplyGenerationExecutor();
        var runtime = CreateRunAgentWithExecutor(
            actorRuntime,
            generationExecutor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions(),
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-generation-timeout",
            RunId = "run-no-generation-timeout",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-no-generation-timeout",
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        generationExecutor.Starts.Should().ContainSingle();
        scheduler.Timeouts.Should().NotContain(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunReplyGenerationTimedOut.Descriptor));
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
    public async Task HandleStartAsync_WhenReplyProducedWithReceipt_ShouldRedispatchPersistedReceiptRenderedTextWithoutRerunningLlm()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
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
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-produced-retry",
            CorrelationId = "corr-produced-retry",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyProduced,
            ProducedReplyText = "Done.\n[tool receipt] Completed: ornn.skill skill-1 (version=1.0, hash=hash-1)",
            ProducedTerminalState = LlmReplyTerminalState.Completed,
            ToolReceipts = { NewPublishReceipt(status: Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success) },
        });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-produced-retry",
            RunId = "run-produced-retry",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-produced-retry",
        });

        replyGenerator.CallCount.Should().Be(0);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
        var ready = handled.Single().Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be(runtime.State.ProducedReplyText);
        ready.Outbound.Text.Should().Contain("[tool receipt] Completed: ornn.skill skill-1");
    }

    [Fact]
    public async Task HandleStartAsync_WhenTargetRefUsesGAgentToolHint_DoesNotOverrideTargetActorId()
    {
        // Refactor (issue1321-first): ForwardToModel.tool_choice_hint is tool prefill
        // only. actor_id inside prefilled arguments must not redirect the run target.
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-forward-gagent",
            TargetActorId = "conversation:original",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-forward-gagent",
            TargetRef = GAgentToolHint("conversation:forwarded"),
        });

        originalHandled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor),
            "tool hints are prefill and do not rewrite the run actor reply target");
        forwardedHandled.Should().BeEmpty();
        runtime.State.TargetActorId.Should().Be("conversation:original");
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
        userConfigQueryPort.GetAsync(
                UserConfigResourceKey.ForOwnerScope("scope-bot-owner"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11,
                LlmSelection: UserServiceSelection(
                    "anthropic-via-bot-owner",
                    "gpt-4o-bot-owner"))));

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

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    [Fact]
    public async Task HandleStartAsync_WhenUnboundChannelTurnRequestsToolCall_CompletesWithoutToolDispatch()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var providerFactory = new ToolCallAttemptProviderFactory();
        var toolSource = new CountingAgentRunToolSource(new AgentRunNoopTool());
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [toolSource],
            localSkillCatalog: new LocalSkillCatalog());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var activity = BuildRelayActivity();

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-unbound-no-tools",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-unbound-no-tools",
            ToolContext = AgentToolExecutionContext.Empty.ToPayload(),
            LlmControl = ControlForAgentRun("owner-model", "owner-route", 4).ToPayload(),
            Metadata =
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-unbound-no-tools",
            },
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.GenerationStep.Should().NotBeNull();
        runtime.State.GenerationStep!.FinalNoToolsStep.Should().BeTrue();
        runtime.State.GenerationStep.PendingToolCalls.Should().BeEmpty();
        runtime.State.ProducedReplyText.Should().Be("attempted tool");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.Requests[0].Tools.Should().BeNull();
        toolSource.DiscoverCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleStartAsync_PersistsStepStateWithoutRuntimeCredentials_ButStillRepliesWithLiveToken()
    {
        // Issue #2580 Item 1: the persisted per-step waterline must never carry a bearer token.
        // The inbound carries owner + sender tokens on LlmControl, a sender runtime token on the
        // Activity, and a sender binding on the tool context. After the turn, the persisted step
        // state (State.GenerationStep, rebuilt from the committed AgentRunReplyStepStateUpdatedEvent)
        // must be token-less, yet the LLM request must still have executed with a live credential
        // re-supplied from the transient request.
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var providerFactory = new SingleReplyProviderFactory("clean reply");
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [new CountingAgentRunToolSource(new AgentRunNoopTool())],
            localSkillCatalog: new LocalSkillCatalog());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var activity = BuildRelayActivity();
        activity.TransportExtras ??= new TransportExtras();
        activity.TransportExtras.NyxUserAccessToken = "sender-runtime-token";

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-strip-persisted-credentials",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-strip-persisted-credentials",
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                SenderBinding = new AgentToolSenderBindingContext("bnd-user-1"),
            }).ToPayload(),
            LlmControl = new LLMControlContext(
                "owner-token",
                "owner-token",
                "inbound-sender-token",
                "owner-model",
                "/api/v1/proxy/s/owner",
                4,
                null).ToPayload(),
            Metadata =
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-strip-persisted-credentials",
            },
        });

        // The turn completed with a live credential re-supplied from the transient request.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Be("clean reply");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.Requests[0].LlmControl!.NyxIdAccessToken.Should().NotBeNullOrEmpty(
            "the executor re-supplies a live credential from the transient request even though the persisted state is stripped");

        // The persisted per-step waterline carries no bearer token in any of the four sub-messages.
        var persisted = runtime.State.GenerationStep;
        persisted.Should().NotBeNull();
        (persisted!.LlmControl?.NyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.LlmControl?.NyxIdOrgToken ?? string.Empty).Should().BeEmpty();
        (persisted.LlmControl?.SenderNyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.ToolContext?.Credentials?.NyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.ToolContext?.Credentials?.NyxIdOrgToken ?? string.Empty).Should().BeEmpty();
        (persisted.ToolContext?.Credentials?.SenderNyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackLlmControl?.NyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackLlmControl?.NyxIdOrgToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackLlmControl?.SenderNyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackToolContext?.Credentials?.NyxIdAccessToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackToolContext?.Credentials?.NyxIdOrgToken ?? string.Empty).Should().BeEmpty();
        (persisted.OwnerFallbackToolContext?.Credentials?.SenderNyxIdAccessToken ?? string.Empty).Should().BeEmpty();

        // Belt-and-suspenders: no inbound token value survives anywhere in the committed state bytes.
        var persistedText = System.Text.Encoding.UTF8.GetString(persisted.ToByteArray());
        persistedText.Should().NotContain("owner-token");
        persistedText.Should().NotContain("inbound-sender-token");
        persistedText.Should().NotContain("sender-runtime-token");
    }

    [Fact]
    public async Task HandleStartAsync_WhenLarkImageAttachmentIsOversized_EmitsControlledVisibleFailure()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        EventEnvelope? handled = null;
        targetActor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var providerFactory = new SingleReplyProviderFactory("reply after attachment visibility warning")
        {
            Capabilities = new LLMProviderCapabilities
            {
                SupportedInputModalities = new HashSet<ContentPartKind>
                {
                    ContentPartKind.Text,
                    ContentPartKind.Image,
                },
            },
        };
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            larkClient: Substitute.For<ILarkNyxClient>());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var activity = BuildRelayActivity();
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "user-token",
            NyxPlatformMessageId = "om_oversized",
        };
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "img_oversized",
            Kind = AttachmentKind.Image,
            ContentType = "image/png",
            Name = "large.png",
            SizeBytes = 10 * 1024 * 1024 + 1,
        });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-oversized-attachment",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-oversized-attachment",
            LlmControl = ControlForAgentRun("owner-model", "owner-route", 4).ToPayload(),
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        ready.ErrorCode.Should().BeEmpty();
        ready.ErrorSummary.Should().BeEmpty();
        ready.Outbound.Text.Should().Be("reply after attachment visibility warning");
        providerFactory.Requests.Should().ContainSingle();
        providerFactory.Requests[0].Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("Attachment visibility warning")
            .And.Contain("one or more attachments could not be converted to LLM input");
    }

    [Fact]
    public async Task HandleStartAsync_WhenBoundToolSchemaIsRejected_RetriesWithOwnerNoTools()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var providerFactory = new RejectToolsThenReplyProviderFactory();
        var toolSource = new CountingAgentRunToolSource(new AgentRunNoopTool());
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [toolSource],
            localSkillCatalog: new LocalSkillCatalog());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var activity = BuildRelayActivity();
        activity.TransportExtras ??= new TransportExtras();
        activity.TransportExtras.NyxUserAccessToken = "sender-runtime-token";

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bound-schema-fallback",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-bound-schema-fallback",
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                SenderBinding = new AgentToolSenderBindingContext("bnd-user-1"),
            }).ToPayload(),
            LlmControl = new LLMControlContext(
                "owner-token",
                "owner-token",
                "sender-runtime-token",
                "owner-model",
                "/api/v1/proxy/s/owner",
                4,
                null).ToPayload(),
            Metadata =
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-bound-schema-fallback",
            },
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Be("owner fallback reply");
        runtime.State.GenerationStep.Should().NotBeNull();
        runtime.State.GenerationStep!.FinalNoToolsStep.Should().BeTrue();
        providerFactory.Requests.Should().HaveCount(2);
        providerFactory.Requests[0].Tools.Should().NotBeNull();
        providerFactory.Requests[0].ToolContext!.SenderBinding.BindingId.Should().Be("bnd-user-1");
        providerFactory.Requests[0].LlmControl!.NyxIdAccessToken.Should().Be("sender-runtime-token");

        providerFactory.Requests[1].Tools.Should().BeNull();
        providerFactory.Requests[1].ToolContext!.SenderBinding.BindingId.Should().BeNull();
        providerFactory.Requests[1].ToolContext!.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        providerFactory.Requests[1].LlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        providerFactory.Requests[1].LlmControl!.NyxIdAccessToken.Should().Be("owner-token");
        providerFactory.Requests[1].LlmControl!.ModelOverride.Should().BeNull();
        providerFactory.Requests[1].LlmControl!.NyxIdRoutePreference.Should().BeNull();
        providerFactory.Requests[1].ToolContext!.Routing.ModelOverride.Should().BeNull();
        providerFactory.Requests[1].ToolContext!.Routing.NyxIdRoutePreference.Should().BeNull();
    }

    [Fact]
    public async Task HandleStartAsync_WhenOwnerConfiguredRouteReturnsEmptyReply_RetriesKeepingOwnerRouteAndTools()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var providerFactory = new EmptyThenReplyProviderFactory();
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [],
            localSkillCatalog: new LocalSkillCatalog());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-owner-empty-fallback",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-owner-empty-fallback",
            LlmControl = new LLMControlContext(
                "owner-token",
                "owner-token",
                SenderNyxIdAccessToken: null,
                "gpt-5.5",
                "/api/v1/proxy/s/chrono-llm",
                40,
                null).ToPayload(),
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Be("server default fallback reply");
        providerFactory.Requests.Should().HaveCount(2);
        providerFactory.Requests[0].LlmControl!.ModelOverride.Should().Be("gpt-5.5");
        providerFactory.Requests[0].LlmControl!.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        // New behavior: the empty-reply retry KEEPS the owner's route + tools + context and only trims
        // history — it no longer strips to a no-tools server-default route. This is the fix for the
        // "no Lark context / no tools" apology on big-history conversations: the retry must still be
        // able to do the task.
        providerFactory.Requests[1].LlmControl!.NyxIdAccessToken.Should().Be("owner-token");
        providerFactory.Requests[1].LlmControl!.ModelOverride.Should().Be("gpt-5.5");
        providerFactory.Requests[1].LlmControl!.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        providerFactory.Requests[1].Tools.Should().BeEquivalentTo(providerFactory.Requests[0].Tools);
    }

    [Fact]
    public async Task HandleStartAsync_WhenSenderHasLlmConfig_UsesSenderConfigBeforeBotOwnerConfig()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var providerFactory = new SingleReplyProviderFactory("sender-config reply");
        var preferencesStore = new AgentRunStubPreferencesStore
        {
            ByBinding =
            {
                ["bnd-user-1"] = SenderPreferences(7),
            },
        };
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources: [],
            preferencesStore: preferencesStore);
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));

        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-bot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("scope-bot-owner"));
        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync(
                UserConfigResourceKey.ForOwnerScope("scope-bot-owner"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "owner-model",
                PreferredLlmRoute: "/api/v1/proxy/s/owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11,
                LlmSelection: UserServiceSelection("owner", "owner-model"))));

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
            CorrelationId = "corr-sender-config-priority",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-sender-config-priority",
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                SenderBinding = new AgentToolSenderBindingContext("bnd-user-1"),
            }).ToPayload(),
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: "sender-session-jwt",
                ModelOverride: null,
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null).ToPayload(),
            Metadata =
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-sender-config-priority",
            },
        });

        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Be("sender-config reply");
        preferencesStore.Lookups.Should().HaveCount(2);
        preferencesStore.Lookups.Should().OnlyContain(bindingId => bindingId == "bnd-user-1");
        providerFactory.Requests.Should().ContainSingle();
        var request = providerFactory.Requests[0];
        request.LlmControl!.ModelOverride.Should().Be("sender-model");
        request.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/sender");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(7);
        request.LlmControl.NyxIdAccessToken.Should().Be("sender-session-jwt");
        request.ToolContext!.Routing.ModelOverride.Should().Be("sender-model");
        request.ToolContext.Routing.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/sender");
        request.ToolContext.Credentials.NyxIdAccessToken.Should().Be("sender-session-jwt");
    }

    [Fact]
    public async Task HandleStartAsync_WhenLongRunningLarkAutomation_ShouldRunInteractionPublishThenScheduleTools()
    {
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("conversation:c");
        targetActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var toolOrder = new List<string>();
        var providerFactory = new OrderedAutomationToolProviderFactory();
        var replyGenerator = new NyxIdConversationReplyGenerator(
            providerFactory,
            BuiltInPromptFloorProvider,
            toolSources:
            [
                new StaticAgentRunToolSource(
                [
                    new RecordingAgentTool("reply_with_interaction", toolOrder, """{"status":"queued"}"""),
                    new RecordingAgentTool("ornn_publish_skill", toolOrder, """{"skill_ref":"daily-lark-digest"}"""),
                    new RecordingAgentTool("scheduled_agent_creator", toolOrder, """{"accepted":true,"agent_id":"agent-1"}"""),
                ]),
            ],
            localSkillCatalog: new LocalSkillCatalog(),
            toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort());
        var actorRuntime = new DispatchingActorRuntime(("conversation:c", targetActor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var activity = BuildRelayActivity();
        activity.Content.Text = "每天早上把 GitHub 进展总结发到这个 Lark 群";

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-long-lark-automation",
            TargetActorId = "conversation:c",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-long-lark-automation",
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                SenderBinding = new AgentToolSenderBindingContext("bnd-user-1"),
            }).ToPayload(),
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: "owner-token",
                NyxIdOrgToken: "owner-token",
                SenderNyxIdAccessToken: "sender-session-jwt",
                ModelOverride: null,
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: 6,
                UserMemoryPrompt: null).ToPayload(),
            Metadata =
            {
                [ChannelMetadataKeys.Platform] = "lark",
                [ChannelMetadataKeys.SenderId] = "ou_user_1",
                [ChannelMetadataKeys.MessageId] = "msg-long-lark-automation",
            },
        });

        toolOrder.Should().Equal("reply_with_interaction", "ornn_publish_skill", "scheduled_agent_creator");
        providerFactory.RoundToolNames.Should().Equal("reply_with_interaction", "ornn_publish_skill", "scheduled_agent_creator", "<final>");
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedReplyText.Should().Contain("scheduled");

        // #2580 regression net for round 2+: every continuation step reads the persisted TOKEN-LESS
        // step state and must re-supply live credentials from the transient request
        // (ReSupplyRuntimeCredentialsAsync). With a bound sender, sender-priority promotion
        // (BuildEffectiveReplyPlanAsync) then makes the sender token the LLM credential on EVERY
        // round. If the re-supply seam regresses, rounds 2..N silently lose the sender credential
        // and drift back to owner credentials — assert every round, not just round 1.
        providerFactory.Requests.Should().HaveCount(4);
        for (var round = 0; round < providerFactory.Requests.Count; round++)
        {
            var roundRequest = providerFactory.Requests[round];
            roundRequest.LlmControl!.SenderNyxIdAccessToken.Should().Be(
                "sender-session-jwt", $"round {round} must carry the re-supplied sender token");
            roundRequest.LlmControl!.NyxIdAccessToken.Should().Be(
                "sender-session-jwt", $"round {round} must keep the sender as the LLM credential (no owner drift)");
            roundRequest.ToolContext!.Credentials.SenderNyxIdAccessToken.Should().Be(
                "sender-session-jwt", $"round {round} tool credentials must carry the sender token");
            roundRequest.ToolContext!.Credentials.NyxIdAccessToken.Should().NotBeNullOrEmpty(
                $"round {round} tool credentials must not be stripped");
        }
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
        cleanupCommand.RunId.Should().Be("corr-cleanup-schedule");
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
            RunId = "corr-cleanup",
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
        var cleanup = new AgentRunCleanupRequested { RunId = "corr-cleanup-dup" };
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
        var hangingGenerator = new HangingReplyGenerator();
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
            RunId = "corr-no-resched",
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
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
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
    }

    [Fact]
    public async Task HandleStartAsync_TerminalFailure_ShouldNotDispatchDuplicateFailureReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new ThrowingReplyGenerator(new InvalidOperationException("boom"));
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
        _ = await scheduler.NextTimeoutAsync();

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
        retryCommand.RunId.Should().Be("corr-retry-ready");
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
        _ = await scheduler.NextTimeoutAsync();

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
        _ = await scheduler.NextTimeoutAsync();

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
    public async Task HandleStartAsync_ShouldScheduleRetry_WhenDropSignalIsNotAccepted()
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

        // Sync (PR #1106 r2): drop notification now persists an actor-owned drop outbox before retry.
        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        handled.Should().BeEmpty();
        replyGenerator.CallCount.Should().Be(0);

        var retryCommand = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunDropNotificationRetryRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunDropNotificationRetryRequested>();

        await runtime.HandleDropNotificationRetryAsync(retryCommand);

        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        replyGenerator.CallCount.Should().Be(0);
        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
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
        var replyGenerator = new ThrowingReplyGenerator(new InvalidOperationException("boom"));
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
    public async Task HandleStartAsync_ResponseTimeoutSeconds_ShouldNotCancelReplyGeneration()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "slow but valid",
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                ResponseTimeoutSeconds = 1,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-timeout",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-timeout",
        });

        replyGenerator.CancellationTokenObserved.Should().BeFalse();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        ready.ErrorCode.Should().BeEmpty();
        ready.Outbound.Text.Should().Be("slow but valid");
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
        // The empty-reply failure must carry diagnostic context (finish reason,
        // reasoning-only flag, token usage) so the otherwise-opaque
        // "couldn't generate a response" outcome is diagnosable from the terminal event.
        ready.ErrorSummary.Should().Contain("finishReason=");
        ready.ErrorSummary.Should().Contain("reasoningOnly=");
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
        userConfigQueryPort.GetAsync(
                UserConfigResourceKey.ForOwnerScope("scope-bot-owner"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11,
                LlmSelection: UserServiceSelection(
                    "anthropic-via-bot-owner",
                    "gpt-4o-bot-owner"))));

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
        SetId(agent, ExpectedRunActorId(Guid.NewGuid().ToString("N")));
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeAgentTransition(agent, current, evt));
        var publisher = eventPublisher ?? new DispatchingEventPublisher(actorRuntime);
        if (publisher is DispatchingEventPublisher dispatchingPublisher)
            dispatchingPublisher.SelfTarget = agent;
        agent.EventPublisher = publisher;
        RecordingExecutors.Add(agent, generationExecutor);
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
        SetId(agent, ExpectedRunActorId(Guid.NewGuid().ToString("N")));
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeAgentTransition(agent, current, evt));
        var publisher = eventPublisher ?? new DispatchingEventPublisher(actorRuntime);
        if (publisher is DispatchingEventPublisher dispatchingPublisher)
            dispatchingPublisher.SelfTarget = agent;
        agent.EventPublisher = publisher;
        return agent;
    }

    private static AgentRunGAgent CreateCapabilityTestAgent(
        CapabilityTrackingReplyGenerationExecutor executor,
        RecordingSelfEventPublisher publisher,
        int nextStepIndex = 1)
    {
        var runtime = CreateRunAgentWithExecutor(
            new DispatchingActorRuntime(),
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions(),
            publisher);
        SetState(runtime, new AgentRunGAgentState
        {
            RunId = "run-capability",
            CorrelationId = "corr-capability",
            TargetActorId = "actor-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-capability",
                CorrelationId = "corr-capability",
                TargetActorId = "actor-1",
                Attempt = 1,
                NextStepIndex = nextStepIndex,
                MaxToolRounds = 4,
            },
        });
        return runtime;
    }

    private static AgentRunNextLlmStepRequestedEvent BuildCapabilityLlmStepRequest() =>
        new()
        {
            RunId = "run-capability",
            CorrelationId = "corr-capability",
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = 1,
            Request = new NeedsLlmReplyEvent
            {
                RunId = "run-capability",
                CorrelationId = "corr-capability",
                TargetActorId = "actor-1",
                Activity = BuildRelayActivity(),
            },
        };

    private static AgentRunNextToolStepRequestedEvent BuildCapabilityToolStepRequest(
        AgentRunNextLlmStepRequestedEvent llmContinuation) =>
        BuildCapabilityToolStepRequest(
            llmContinuation.RunId,
            llmContinuation.CorrelationId,
            llmContinuation.StepIndex,
            llmContinuation.Request);

    private static AgentRunNextToolStepRequestedEvent BuildCapabilityToolStepRequest(
        string runId,
        string correlationId,
        int stepIndex,
        NeedsLlmReplyEvent? request = null) =>
        new()
        {
            RunId = runId,
            CorrelationId = correlationId,
            TargetActorId = "actor-1",
            Attempt = 1,
            StepIndex = stepIndex,
            Request = request?.Clone() ?? new NeedsLlmReplyEvent
            {
                RunId = runId,
                CorrelationId = correlationId,
                TargetActorId = "actor-1",
                Activity = BuildRelayActivity(),
            },
        };

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

    private static void SetState(AgentRunGAgent agent, AgentRunGAgentState state)
    {
        var stateField = typeof(Aevatar.Foundation.Core.GAgentBase<AgentRunGAgentState>).GetField(
            "_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        stateField.Should().NotBeNull();
        stateField!.SetValue(agent, state);
    }

    private static string ExpectedRunActorId(string runId) => $"channel-agent-run:{runId}";

    private static ChatActivity BuildRelayActivity() =>
        new()
        {
            Id = "msg-1",
            ChannelId = ChannelId.From("lark"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Content = new MessageContent { Text = "hello" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-1",
            },
        };

    private static Aevatar.AI.Abstractions.AgentToolReceipt NewPublishReceipt(
        Aevatar.AI.Abstractions.AgentToolReceiptStatus status) =>
        new()
        {
            CallId = "call-1",
            ToolName = "ornn_publish_skill",
            Status = status,
            ApprovalMode = Aevatar.AI.Abstractions.AgentToolReceiptApprovalMode.AlwaysRequire,
            SideEffectKind = "ornn.publish.skill",
            SubjectKind = "ornn.skill",
            SubjectId = "skill-1",
            SubjectVersion = "1.0",
            SubjectHash = "hash-1",
            ResultJson = """{"status":"spoofed","subject_id":"wrong"}""",
        };

    private static LLMControlContext ControlForAgentRun(
        string? model = null,
        string? route = null,
        int? rounds = null) =>
        new(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: model,
            NyxIdRoutePreference: route,
            MaxToolRoundsOverride: rounds,
            UserMemoryPrompt: null);

    private static ChatRouteAction GAgentToolHint(string actorId)
    {
        var arguments = new Struct();
        arguments.Fields["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId);

        return new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                    PrefilledArguments = arguments,
                },
            },
        };
    }

    private sealed class DispatchingActorRuntime(params (string Id, IActor Actor)[] actors) :
        IActorRuntime,
        IActorDispatchPort
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(
            static pair => pair.Id,
            static pair => pair.Actor,
            StringComparer.Ordinal);

        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public List<string> DestroyedIds { get; } = [];

        public bool FailNextDispatch { get; set; }

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
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            if (FailNextDispatch)
            {
                FailNextDispatch = false;
                throw new InvalidOperationException("simulated dispatch failure");
            }

            if (!_actors.TryGetValue(actorId, out var actor))
                throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
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

    private static AgentRunLlmStepExecution BuildStaleLlmExecution(
        AgentRunReplyStepExecutionRequest request) =>
        new(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = request.RunId,
            CorrelationId = request.Request.CorrelationId,
            TargetActorId = request.Request.TargetActorId,
            Attempt = request.Attempt + 1,
            StepIndex = request.StepIndex + 1,
            Request = request.Request.Clone(),
            LlmStepResult = new AgentRunLlmStepResult(),
        }, null);

    private static AgentRunNextToolStepRequestedEvent BuildStaleToolContinuation(
        AgentRunReplyStepExecutionRequest request) =>
        new()
        {
            RunId = request.RunId,
            CorrelationId = request.Request.CorrelationId,
            TargetActorId = request.Request.TargetActorId,
            Attempt = request.Attempt + 1,
            StepIndex = request.StepIndex + 1,
            Request = request.Request.Clone(),
            ToolStepResult = new AgentRunToolStepResult(),
        };

    private sealed class PausedReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public List<AgentRunReplyGenerationExecutionRequest> Starts { get; } = [];

        public List<AgentRunReplyStepExecutionRequest> LlmStepExecutions { get; } = [];

        public List<AgentRunReplyStepExecutionRequest> ToolStepExecutions { get; } = [];

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            Starts.Add(request with { Request = request.Request.Clone() });
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
            });
        }

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            LlmStepExecutions.Add(request with
            {
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });
            return Task.FromResult(BuildStaleLlmExecution(request));
        }

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ToolStepExecutions.Add(request with
            {
                Request = request.Request.Clone(),
                StepState = request.StepState.Clone(),
            });
            return Task.FromResult(BuildStaleToolContinuation(request));
        }
    }

    private sealed class BlockingStepExecutionExecutor : IAgentRunReplyGenerationExecutorPort
    {
        private readonly TaskCompletionSource _llmRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _toolRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AgentRunReplyStepExecutionRequest> LlmStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AgentRunReplyStepExecutionRequest> ToolStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            });

        public async Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            LlmStarted.TrySetResult(request);
            await _llmRelease.Task;
            return BuildStaleLlmExecution(request);
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ToolStarted.TrySetResult(request);
            await _toolRelease.Task;
            return BuildStaleToolContinuation(request);
        }

        public void CompleteLlm() => _llmRelease.TrySetResult();

        public void CompleteTool() => _toolRelease.TrySetResult();
    }

    private sealed class CapabilityTrackingReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public static AgentRunToolCall ToolCall { get; } = new()
        {
            Id = "call-capability",
            Name = "use_skill",
            ArgumentsJson = "{}",
        };

        public int ToolExecutionCount { get; private set; }

        public List<bool> ToolStepAuthorizationPresence { get; } = [];

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            var result = new AgentRunLlmStepResult();
            result.ToolCalls.Add(ToolCall.Clone());
            var continuation = new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                LlmStepResult = result,
            };
            var authorizedToolStep = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [ToolCall],
                _ =>
                {
                    ToolExecutionCount++;
                    return Task.FromResult(new AgentRunToolStepResult { AdvanceRound = true });
                });
            return Task.FromResult(new AgentRunLlmStepExecution(continuation, authorizedToolStep));
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ToolStepAuthorizationPresence.Add(authorizedToolStep is not null);
            var result = authorizedToolStep?.Matches(request) == true
                ? await authorizedToolStep.ExecuteAsync(ct)
                : new AgentRunToolStepResult { AdvanceRound = true };
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            };
        }
    }

    private sealed class RecordingSelfEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<T>(
            T e,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            audience.Should().Be(TopologyAudience.Self);
            Published.Add(e);
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(
            string targetActorId,
            T e,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        private readonly AgentRunReplyGenerationExecutor _inner;
        private readonly IActorDispatchPort _dispatchPort;
        private readonly IConversationReplyGenerator _replyGenerator;
        private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions _relayOptions;
        private readonly INyxIdRelayScopeResolver? _scopeResolver;
        private readonly IUserConfigQueryPort? _userConfigQueryPort;
        private readonly IInteractiveReplyCollector? _interactiveReplyCollector;

        public RecordingReplyGenerationExecutor(
            IActorDispatchPort dispatchPort,
            IConversationReplyGenerator replyGenerator,
            IInteractiveReplyCollector? collector,
            Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
            INyxIdRelayScopeResolver? scopeResolver,
            IUserConfigQueryPort? userConfigQueryPort)
        {
            _dispatchPort = dispatchPort;
            _replyGenerator = replyGenerator;
            _relayOptions = relayOptions;
            _scopeResolver = scopeResolver;
            _userConfigQueryPort = userConfigQueryPort;
            _interactiveReplyCollector = collector;
            _inner = new AgentRunReplyGenerationExecutor(
                dispatchPort,
                replyGenerator,
                collector,
                relayOptions,
                NullLogger<AgentRunReplyGenerationExecutor>.Instance,
                scopeResolver,
                userConfigQueryPort);
        }

        public IActorDispatchPort DispatchPort => _dispatchPort;

        public List<AgentRunReplyGenerationExecutionRequest> Starts { get; } = [];

        public async Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            Starts.Add(request with { Request = request.Request.Clone() });
            if (_replyGenerator is not IAgentRunStepConversationReplyGenerator)
            {
                var llmControl = await BuildTestLlmControlAsync(request.Request, ct);
                return new AgentRunReplyStepState
                {
                    RunId = request.RunId,
                    CorrelationId = request.Request.CorrelationId,
                    TargetActorId = request.Request.TargetActorId,
                    Attempt = request.Attempt,
                    NextStepIndex = 1,
                    MaxToolRounds = llmControl.MaxToolRoundsOverride ?? 0,
                    LlmControl = llmControl.ToPayload(),
                    ToolContext = AgentToolExecutionContext.Empty.ToPayload(),
                };
            }

            return await _inner.BuildInitialStepStateAsync(request, ct);
        }

        public async Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            if (_replyGenerator is IAgentRunStepConversationReplyGenerator)
                return await _inner.BuildLlmStepExecutionAsync(request, ct);

            var continuation = await BuildLegacyLlmStepContinuationAsync(request, ct);
            return new AgentRunLlmStepExecution(continuation, null);
        }

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct) =>
            _inner.BuildToolStepContinuationAsync(request, authorizedToolStep, ct);

        public Task DrainAsync(AgentRunGAgentState state) => Task.CompletedTask;

        private async Task<AgentRunNextLlmStepRequestedEvent> BuildLegacyLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            using var streamingSink = TryBuildStreamingSink(request.Request);
            var streamingState = TryBuildStreamingReplyState(streamingSink);
            MessageContent? outboundIntent;
            ConversationReplyResult reply;
            using (TryBeginInteractiveScope(request.Request))
            {
                reply = _replyGenerator is ITypedConversationReplyGenerator typed
                    ? await typed.GenerateReplyAsync(
                        request.Request.Activity!,
                        request.Request.Metadata,
                        await BuildTestLlmControlAsync(request.Request, ct),
                        AgentToolExecutionContext.Empty,
                        streamingState,
                        ct)
                    : await _replyGenerator.GenerateReplyAsync(
                        request.Request.Activity!,
                        request.Request.Metadata,
                        streamingState,
                        ct);
                if (streamingState is not null)
                    await streamingState.FinalizeAsync(reply.Text ?? string.Empty, ct);
                outboundIntent = _interactiveReplyCollector?.TryTake();
            }

            var result = new AgentRunLlmStepResult
            {
                AccumulatedText = reply.Text ?? string.Empty,
                Content = reply.Text ?? string.Empty,
                FinishReason = reply.FinishReason ?? string.Empty,
                HasStreamedTextContent = !string.IsNullOrEmpty(reply.Text),
            };
            if (ToProto(reply.Usage) is { } usage)
                result.Usage = usage;
            if (outboundIntent is not null)
                result.OutboundIntent = outboundIntent;

            return new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                LlmStepResult = result,
            };
        }

        private TurnStreamingReplySink? TryBuildStreamingSink(NeedsLlmReplyEvent request)
        {
            if (_relayOptions is not { StreamingRepliesEnabled: true })
                return null;
            if (request.Activity?.OutboundDelivery is not
                {
                    ReplyMessageId.Length: > 0,
                    CorrelationId.Length: > 0,
                })
                return null;
            if (string.IsNullOrWhiteSpace(request.CorrelationId))
                return null;

            return new TurnStreamingReplySink(
                _dispatchPort,
                request.TargetActorId,
                request.CorrelationId,
                request.RegistrationId,
                request.Activity.Clone(),
                request.ReplyToken,
                request.ReplyTokenExpiresAtUnixMs,
                request.RunId,
                TimeProvider.System,
                cardMode: _relayOptions.StreamingCardKitEnabled);
        }

        private TestStreamingReplyRunState? TryBuildStreamingReplyState(TurnStreamingReplySink? sink)
        {
            if (sink is null)
                return null;

            var cardMode = _relayOptions.StreamingCardKitEnabled;
            var throttle = TimeSpan.FromMilliseconds(Math.Max(0, cardMode
                ? _relayOptions.StreamingCardKitFlushIntervalMs
                : _relayOptions.StreamingFlushIntervalMs));
            var maxInterimChunks = cardMode
                ? int.MaxValue
                : Math.Max(0, _relayOptions.StreamingMaxInterimChunks);
            return new TestStreamingReplyRunState(sink, throttle, maxInterimChunks);
        }

        private IDisposable? TryBeginInteractiveScope(NeedsLlmReplyEvent request)
        {
            if (_interactiveReplyCollector is null)
                return null;
            if (_relayOptions is not { InteractiveRepliesEnabled: true })
                return null;
            if (request.Activity?.OutboundDelivery is not
                {
                    ReplyMessageId.Length: > 0,
                    CorrelationId.Length: > 0,
                })
                return null;

            return _interactiveReplyCollector.BeginScope();
        }

        private async Task<LLMControlContext> BuildTestLlmControlAsync(
            NeedsLlmReplyEvent request,
            CancellationToken ct)
        {
            var control = LLMControlContextMapper.FromPayload(request.LlmControl);
            if (_scopeResolver is not null && _userConfigQueryPort is not null)
            {
                var apiKeyId = request.Activity?.Bot?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(apiKeyId))
                {
                    var scopeId = await _scopeResolver.ResolveScopeIdByApiKeyAsync(apiKeyId, ct);
                    if (!string.IsNullOrWhiteSpace(scopeId))
                    {
                        var config = await _userConfigQueryPort.GetAsync(
                            UserConfigResourceKey.ForOwnerScope(scopeId),
                            ct);
                        control = new OwnerLlmConfig(
                                config.LlmSelection?.Clone() ?? LLMSelectionPolicy.SystemDefaultSelection(),
                                LLMSelectionPolicy.ClassifyPersisted(
                                    config.LlmSelection,
                                    config.PreferredLlmRoute,
                                    config.DefaultModel),
                                config.MaxToolRounds)
                            .ApplyTo(control);
                    }
                }
            }

            var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
            if (!string.IsNullOrWhiteSpace(userAccessToken))
            {
                control = control with
                {
                    NyxIdAccessToken = userAccessToken,
                    NyxIdOrgToken = userAccessToken,
                };
            }

            var routedModel = request.TargetRef?.ForwardToModel?.ModelName;
            return string.IsNullOrWhiteSpace(routedModel)
                ? control
                : control with { ModelOverride = routedModel.Trim() };
        }

        private static AgentRunReplyTokenUsage? ToProto(ReplyTokenUsage? source) =>
            source is null
                ? null
                : new AgentRunReplyTokenUsage
                {
                    PromptTokens = source.PromptTokens,
                    CompletionTokens = source.CompletionTokens,
                    TotalTokens = source.TotalTokens,
                };

        private sealed class TestStreamingReplyRunState(
            TurnStreamingReplySink sink,
            TimeSpan throttle,
            int maxInterimChunks) : IStreamingReplySink
        {
            private string _lastEmittedText = string.Empty;
            private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
            private int _chunksEmitted;
            private string _pendingText = string.Empty;

            public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
                TryDispatchAsync(accumulatedText, isFinal: false, ct);

            public Task FinalizeAsync(string finalText, CancellationToken ct) =>
                TryDispatchAsync(finalText, isFinal: true, ct);

            private async Task TryDispatchAsync(string text, bool isFinal, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return;

                if (string.Equals(text, _lastEmittedText, StringComparison.Ordinal))
                {
                    if (isFinal || string.Equals(text, _pendingText, StringComparison.Ordinal))
                        _pendingText = string.Empty;
                    return;
                }

                if (!isFinal && _chunksEmitted >= maxInterimChunks)
                {
                    _pendingText = text;
                    return;
                }

                if (!isFinal)
                {
                    var elapsed = DateTimeOffset.UtcNow - _lastEmitAt;
                    if (elapsed < throttle)
                    {
                        _pendingText = text;
                        return;
                    }
                }

                await sink.DispatchAsync(text, isFinal, ct);
                if (sink.ChunksEmitted > _chunksEmitted)
                {
                    _lastEmittedText = text;
                    _lastEmitAt = DateTimeOffset.UtcNow;
                    _chunksEmitted = sink.ChunksEmitted;
                    if (isFinal || string.Equals(_pendingText, text, StringComparison.Ordinal))
                        _pendingText = string.Empty;
                }
            }
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

            var result = BuildCommitResult(_pending, CurrentVersion);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
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
            var result = BuildCommitResult(_pending, CurrentVersion);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
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

    private static EventStoreCommitResult BuildCommitResult(
        IEnumerable<IMessage> pending,
        long currentVersion)
    {
        var result = new EventStoreCommitResult();
        foreach (var evt in pending)
        {
            currentVersion++;
            result.CommittedEvents.Add(new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = currentVersion,
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
            });
        }

        result.LatestVersion = currentVersion;
        return result;
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly Queue<RuntimeCallbackTimeoutRequest> _timeoutSignals = new();
        private TaskCompletionSource<RuntimeCallbackTimeoutRequest>? _waitingTimeout;

        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public List<RuntimeCallbackTimerRequest> Timers { get; } = [];

        public List<RuntimeCallbackLease> Cancelled { get; } = [];

        public List<string> PurgedActorIds { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            if (_waitingTimeout is { Task.IsCompleted: false } waiting)
            {
                _waitingTimeout = null;
                waiting.TrySetResult(request);
            }
            else
            {
                _timeoutSignals.Enqueue(request);
            }

            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackTimeoutRequest> NextTimeoutAsync()
        {
            if (_timeoutSignals.TryDequeue(out var request))
                return Task.FromResult(request);

            _waitingTimeout = new TaskCompletionSource<RuntimeCallbackTimeoutRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _waitingTimeout.Task;
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
        public AgentRunGAgent? SelfTarget { get; set; }

        public bool FailNextSend { get; set; }

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Task PublishAsync<T>(
            T e,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            // Sync (PR #1106 r2): AgentRun reply generation now advances through actor self-messages.
            if (audience == TopologyAudience.Self && SelfTarget is not null)
            {
                if (e is AgentRunNextLlmStepRequestedEvent llmStep)
                    return SelfTarget.HandleNextLlmStepAsync(llmStep);
                if (e is AgentRunNextToolStepRequestedEvent toolStep)
                    return SelfTarget.HandleNextToolStepAsync(toolStep);
                if (e is AgentRunOwnerFallbackStepRequested fallbackStep)
                    return SelfTarget.HandleOwnerFallbackStepAsync(fallbackStep);
                if (e is AgentRunReplyGenerationFailed failed)
                    return SelfTarget.HandleReplyGenerationFailedAsync(failed);
            }

            return Task.CompletedTask;
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

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : ITypedConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public int CallCount { get; private set; }

        public bool CaptureSucceeded { get; private set; }

        public bool CancellationTokenObserved { get; private set; }

        public Action<IReadOnlyDictionary<string, string>>? MetadataObserver { get; init; }

        public Action<LLMControlContext>? LlmControlObserver { get; init; }

        public Action<AgentToolExecutionContext>? ToolContextObserver { get; init; }

        public IReadOnlyList<string>? StreamingSnapshots { get; init; }

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
            CallCount++;
            CancellationTokenObserved = ct.IsCancellationRequested;
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
            return new ConversationReplyResult(ReplyText, Usage: null, FinishReason: null);
        }
    }

    private sealed class AgentRunStubPreferencesStore : INyxIdUserLlmPreferencesStore
    {
        public Dictionary<string, NyxIdUserLlmPreferences> ByBinding { get; } = new(StringComparer.Ordinal);

        public List<string?> Lookups { get; } = [];

        public Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default)
        {
            Lookups.Add(null);
            return Task.FromResult(NyxIdUserLlmPreferences.Empty);
        }

        public Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default)
        {
            Lookups.Add(bindingId);
            return Task.FromResult(ByBinding.TryGetValue(bindingId, out var prefs)
                ? prefs
                : NyxIdUserLlmPreferences.Empty);
        }
    }

    private static NyxIdUserLlmPreferences SenderPreferences(int maxToolRounds) => new(
        UserServiceSelection("sender", "sender-model"),
        LLMSelectionPersistenceStatus.Ready,
        maxToolRounds);

    private static LLMSelection UserServiceSelection(string serviceSlug, string modelId) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = $"/api/v1/proxy/s/{serviceSlug}",
        NyxIdUserServiceId = $"us-{serviceSlug}",
        ServiceSlugSnapshot = serviceSlug,
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId,
        },
    };

    private sealed class SingleReplyProviderFactory(string replyText) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "single-reply";

        public List<LLMRequest> Requests { get; } = [];

        public LLMProviderCapabilities Capabilities { get; init; } = LLMProviderCapabilities.TextOnly;

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = replyText };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ToolCallAttemptProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-call-attempt";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = "attempted tool" };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-unbound",
                    Name = AgentRunNoopTool.ToolName,
                    ArgumentsJson = "{}",
                },
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class RejectToolsThenReplyProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "reject-tools-then-reply";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.Tools is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    "Invalid schema for function 'aevatar_observe_run': schema must have type 'object' and not have 'oneOf' at the top level (HTTP 400).");
            }

            yield return new LLMStreamChunk { DeltaContent = "owner fallback reply" };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class EmptyThenReplyProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "empty-then-reply";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                await Task.CompletedTask;
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                yield break;
            }

            yield return new LLMStreamChunk { DeltaContent = "server default fallback reply" };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class OrderedAutomationToolProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        private static readonly string[] ToolNames =
        [
            "reply_with_interaction",
            "ornn_publish_skill",
            "scheduled_agent_creator",
        ];

        private int _round;

        public string Name => "ordered-automation-tool-provider";

        public List<string> RoundToolNames { get; } = [];

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var round = _round++;
            if (round < ToolNames.Length)
            {
                var toolName = ToolNames[round];
                RoundToolNames.Add(toolName);
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = $"call-{round + 1}",
                        Name = toolName,
                        ArgumentsJson = "{}",
                    },
                };
                await Task.CompletedTask;
                yield return new LLMStreamChunk { IsLast = true };
                yield break;
            }

            RoundToolNames.Add("<final>");
            yield return new LLMStreamChunk { DeltaContent = "scheduled" };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class CountingAgentRunToolSource(IAgentTool tool) : IAgentToolSource
    {
        public int DiscoverCount { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            DiscoverCount++;
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class StaticAgentRunToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(tools);
        }
    }

    private sealed class RecordingAgentTool(
        string name,
        List<string> order,
        string resultJson) : IAgentTool
    {
        public string Name => name;

        public string Description => $"Test tool {name}.";

        public string ParametersSchema => """{"type":"object","additionalProperties":true}""";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            order.Add(name);
            return Task.FromResult(resultJson);
        }
    }

    private sealed class AgentRunNoopTool : IAgentTool
    {
        public const string ToolName = "agent_run_noop_tool";

        public string Name => ToolName;

        public string Description => "No-op test tool.";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"executed":true}""");
    }

    private sealed class ThrowingReplyGenerator(Exception exception) : IConversationReplyGenerator
    {
        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => Task.FromException<ConversationReplyResult>(exception);
    }

    /// <summary>Generator that never completes on its own; only ends when the runtime cancels it.</summary>
    private sealed class HangingReplyGenerator : IConversationReplyGenerator
    {
        public bool WasCancelled { get; private set; }

        public async Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
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
    }

    [Theory]
    [InlineData(
        "Upstream LLM route '/api/v1/proxy/s/chrono-llm' rejected the request with HTTP 401 for model 'gpt-5.5'. Your session may have expired — try signing in again. Upstream said: {\"error\":\"token_expired\",\"error_code\":2001}",
        true)]
    [InlineData("Upstream said: {\"error\":\"token_expired\"}", true)]
    [InlineData("Reply generator returned an empty response (finishReason=length).", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ResolveTerminalFailureReply_SurfacesReauthHintOnlyForExpiredSession(
        string? errorSummary,
        bool expectReauth)
    {
        const string generic = "Sorry, I wasn't able to generate a response. Please try again.";
        var reply = AgentRunGAgent.ResolveTerminalFailureReply(errorSummary);

        if (expectReauth)
        {
            reply.Should().NotBe(generic);
            reply.Should().Contain("sign in to NyxID again");
            reply.Should().Contain("重新登录");
        }
        else
        {
            reply.Should().Be(generic);
        }
    }

}

internal static class AgentRunGAgentTestExtensions
{
    public static async Task HandleStartAsync(this AgentRunGAgent agent, NeedsLlmReplyEvent request)
    {
        // Sync (PR #1106 r2): AgentRun admission now requires the outer typed run_id command field.
        var normalized = request.Clone();
        if (string.IsNullOrWhiteSpace(normalized.RunId))
            normalized.RunId = normalized.CorrelationId;

        await agent.HandleStartAsync(new AgentRunStartRequested
        {
            RunId = normalized.RunId,
            Request = normalized,
        });
        await AgentRunGAgentTests.DrainRecordingExecutorAsync(agent);
    }
}
