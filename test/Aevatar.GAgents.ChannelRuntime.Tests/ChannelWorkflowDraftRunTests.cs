using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowDraftRunTests
{
    [Theory]
    [InlineData("/workflow run daily-greeting", "daily-greeting")]
    [InlineData("/run-workflow daily.greeting", "daily.greeting")]
    [InlineData("跑一下 daily_greeting 的 workflow", "daily_greeting")]
    [InlineData("run daily-greeting workflow", "daily-greeting")]
    public void IntentParser_ShouldMatchOnlyDeterministicWorkflowRunGrammar(string text, string workflowId)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser();

        var matched = parser.TryParse(text, out var intent);

        matched.Should().BeTrue();
        intent.WorkflowId.Should().Be(workflowId);
        intent.Prompt.Should().Be(text);
    }

    [Theory]
    [InlineData("please run daily-greeting workflow")]
    [InlineData("/workflow run")]
    [InlineData("/workflow run daily greeting")]
    [InlineData("aevatar_start_workflow daily-greeting")]
    public void IntentParser_ShouldRejectAmbiguousOrToolNamedText(string text)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser();

        parser.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public async Task InteractionPort_ShouldBuildWorkflowRequestAndRenderFramesIntoConversationCarriers()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ChannelWorkflowDraftRunInteractionPort(
            dispatch,
            new WorkflowDraftRunReplyRenderer(),
            NullLogger<ChannelWorkflowDraftRunInteractionPort>.Instance,
            workflow,
            TimeProvider.System);
        var request = BuildWorkflowRequest();

        await port.DispatchAsync(request, CancellationToken.None);

        workflow.LastRequest.Should().NotBeNull();
        workflow.LastRequest!.Prompt.Should().Be("/workflow run daily-greeting");
        workflow.LastRequest.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        workflow.LastRequest.Source.ActorId.Should().Be("workflow-actor-1");
        workflow.LastRequest.ScopeId.Should().Be("scope-1");
        workflow.LastRequest.CallerCredential!.BearerToken.Should().Be("user-token-1");
        workflow.LastRequest.CommandIdSeed.Should().Be("workflow-draft-run-1");
        workflow.LastRequest.CorrelationIdSeed.Should().Be("msg-1");
        workflow.LastRequest.Headers!["registration_id"].Should().Be("reg-1");

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes[0].ActorId.Should().Be("conversation-actor-1");
        var firstChunk = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        firstChunk.AccumulatedText.Should().Be("hello");
        firstChunk.ReplyToken.Should().Be("reply-token-1");

        var secondChunk = dispatch.Envelopes[1].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        secondChunk.AccumulatedText.Should().Be("hello world");

        var ready = dispatch.Envelopes[2].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.RunId.Should().Be("workflow-draft-run-1");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        ready.Outbound.Text.Should().Be("hello world");
        ready.ReplyToken.Should().Be("reply-token-1");
    }

    [Fact]
    public void ReplyRenderer_ShouldRenderWorkflowFailuresAsTerminalReadyText()
    {
        var renderer = new WorkflowDraftRunReplyRenderer();

        var rendered = renderer.Render(new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload
            {
                Message = "boom",
                Code = "bad_input",
            },
        }, "partial");

        rendered.Should().NotBeNull();
        rendered!.IsTerminal.Should().BeTrue();
        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("bad_input");
        rendered.Text.Should().Contain("boom");
    }

    private static NeedsWorkflowDraftRunEvent BuildWorkflowRequest() =>
        new()
        {
            CorrelationId = "msg-1",
            TargetActorId = "conversation-actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-1",
                Content = new MessageContent { Text = "/workflow run daily-greeting" },
            },
            WorkflowSource = new ChannelWorkflowDraftRunSource
            {
                Kind = ChannelWorkflowDraftRunSourceKind.DefinitionActor,
                ScopeId = "scope-1",
                WorkflowId = "daily-greeting",
                WorkflowName = "daily-greeting",
                DefinitionActorId = "workflow-actor-1",
            },
            Prompt = "/workflow run daily-greeting",
            RequestedAtUnixMs = 1,
            RunId = "workflow-draft-run-1",
            ReplyToken = "reply-token-1",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            NyxUserAccessToken = "user-token-1",
            Headers =
            {
                ["registration_id"] = "reg-1",
            },
        };

    private sealed class RecordingWorkflowChatRunInteractionPort : IWorkflowChatRunInteractionPort
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            await emitAsync(new WorkflowRunEventEnvelope
            {
                TextMessageContent = new WorkflowTextMessageContentEventPayload
                {
                    Delta = "hello",
                },
            }, ct);
            await emitAsync(new WorkflowRunEventEnvelope
            {
                TextMessageContent = new WorkflowTextMessageContentEventPayload
                {
                    Delta = " world",
                },
            }, ct);
            await emitAsync(new WorkflowRunEventEnvelope
            {
                RunFinished = new WorkflowRunFinishedEventPayload(),
            }, ct);

            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                new WorkflowChatRunAcceptedReceipt("workflow-actor-1", "daily-greeting", "workflow-draft-run-1", "msg-1"),
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }
}
