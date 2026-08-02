using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Aevatar.Foundation.Abstractions.Tools;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using AguiTextMessageContentEvent = Aevatar.AGUI.Contracts.TextMessageContentEvent;
using AguiTextMessageEndEvent = Aevatar.AGUI.Contracts.TextMessageEndEvent;
using AguiTextMessageStartEvent = Aevatar.AGUI.Contracts.TextMessageStartEvent;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.AI.Tests;

public class NyxIdChatAguiSseEventWriterTests
{
    [Fact]
    public async Task WriteAsync_ShouldMapTextFrames()
    {
        var sink = new SseFrameSink();

        await sink.WriteAsync(new AGUIEvent { TextMessageStart = new AguiTextMessageStartEvent { MessageId = "" } }, "fallback-message");
        await sink.WriteAsync(new AGUIEvent { TextMessageContent = new AguiTextMessageContentEvent { Delta = "hello" } }, "fallback-message");
        await sink.WriteAsync(new AGUIEvent { TextMessageEnd = new AguiTextMessageEndEvent { MessageId = "message-2" } }, "fallback-message");

        var frames = sink.ReadFrames();
        frames.Should().HaveCount(3);
        frames[0].GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_START");
        frames[0].GetProperty("textMessageStart").GetProperty("messageId").GetString().Should().Be("fallback-message");
        frames[0].GetProperty("textMessageStart").GetProperty("role").GetString().Should().Be("assistant");
        frames[1].GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_CONTENT");
        frames[1].GetProperty("textMessageContent").GetProperty("delta").GetString().Should().Be("hello");
        frames[2].GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_END");
        frames[2].GetProperty("textMessageEnd").GetProperty("messageId").GetString().Should().Be("message-2");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapToolCallFrames()
    {
        var sink = new SseFrameSink();

        await sink.WriteAsync(new AGUIEvent
        {
            ToolCallStart = new ToolCallStartEvent
            {
                ToolName = "nyxid_api-github-work__get_repository",
                ToolCallId = "call-1",
                Presentation = new ToolPresentationDescriptor
                {
                    InvocationName = "nyxid_api-github-work__get_repository",
                    DisplayName = "Work GitHub - Get repository",
                    Description = "Gets one repository.",
                    Kind = ToolPresentationKind.NyxIdOperation,
                    Availability = ToolAvailability.Available,
                    IconUrl = "https://cdn.example.test/github.png",
                    NyxIdOperation = new NyxIdOperationRef
                    {
                        ConnectedServiceId = "connected-service-github",
                        ServiceSlug = "api-github-work",
                        CatalogServiceSlug = "github",
                        ConnectionLabel = "Work GitHub",
                        ConnectorDisplayName = "GitHub",
                        OperationId = "get_repository",
                    },
                },
            },
        }, "message-1");
        await sink.WriteAsync(new AGUIEvent
        {
            ToolCallEnd = new ToolCallEndEvent { ToolCallId = "call-1", Result = "done" },
        }, "message-1");

        var frames = sink.ReadFrames();
        frames.Should().HaveCount(2);
        frames[0].GetProperty("type").GetString().Should().Be("TOOL_CALL_START");
        var start = frames[0].GetProperty("toolCallStart");
        start.GetProperty("toolName").GetString().Should().Be("nyxid_api-github-work__get_repository");
        start.GetProperty("toolCallId").GetString().Should().Be("call-1");
        var presentation = start.GetProperty("presentation");
        presentation.GetProperty("invocationName").GetString().Should()
            .Be("nyxid_api-github-work__get_repository");
        presentation.GetProperty("displayName").GetString().Should().Be("Work GitHub - Get repository");
        presentation.GetProperty("kind").GetString().Should().Be("nyxIdOperation");
        presentation.GetProperty("availability").GetString().Should().Be("available");
        var sourceRef = presentation.GetProperty("sourceRef");
        sourceRef.GetProperty("type").GetString().Should().Be("nyxIdOperation");
        sourceRef.GetProperty("nyxIdOperation").GetProperty("connectedServiceId").GetString().Should()
            .Be("connected-service-github");
        frames[1].GetProperty("type").GetString().Should().Be("TOOL_CALL_END");
        frames[1].GetProperty("toolCallEnd").GetProperty("toolCallId").GetString().Should().Be("call-1");
        frames[1].GetProperty("toolCallEnd").GetProperty("result").GetString().Should().Be("done");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapMediaContentCustomEvent()
    {
        var sink = new SseFrameSink();
        var mediaContent = new MediaContentEvent
        {
            Part = new ChatContentPart
            {
                Kind = ChatContentPartKind.Image,
                DataBase64 = "base64",
                MediaType = "image/png",
                Uri = "nyx://image",
                Name = "diagram.png",
                Text = "caption",
            },
        };

        await sink.WriteAsync(new AGUIEvent
        {
            Custom = new CustomEvent
            {
                Name = "MEDIA_CONTENT",
                Payload = Any.Pack(mediaContent),
            },
        }, "message-1");

        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("MEDIA_CONTENT");
        var media = frame.GetProperty("mediaContent");
        media.GetProperty("kind").GetString().Should().Be("image");
        media.GetProperty("dataBase64").GetString().Should().Be("base64");
        media.GetProperty("mediaType").GetString().Should().Be("image/png");
        media.GetProperty("uri").GetString().Should().Be("nyx://image");
        media.GetProperty("name").GetString().Should().Be("diagram.png");
        media.GetProperty("text").GetString().Should().Be("caption");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapToolApprovalRequestCustomEvent()
    {
        var sink = new SseFrameSink();
        var payload = new Struct
        {
            Fields =
            {
                ["requestId"] = ProtobufValue.ForString("request-1"),
                ["toolName"] = ProtobufValue.ForString("shell"),
                ["toolCallId"] = ProtobufValue.ForString("call-1"),
                ["argumentsJson"] = ProtobufValue.ForString("{\"cmd\":\"pwd\"}"),
                ["isDestructive"] = ProtobufValue.ForBool(true),
                ["timeoutSeconds"] = ProtobufValue.ForNumber(30),
            },
        };

        await sink.WriteAsync(new AGUIEvent
        {
            Custom = new CustomEvent
            {
                Name = "TOOL_APPROVAL_REQUEST",
                Payload = Any.Pack(payload),
            },
        }, "message-1");

        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("TOOL_APPROVAL_REQUEST");
        var approval = frame.GetProperty("toolApprovalRequest");
        approval.GetProperty("requestId").GetString().Should().Be("request-1");
        approval.GetProperty("toolName").GetString().Should().Be("shell");
        approval.GetProperty("toolCallId").GetString().Should().Be("call-1");
        approval.GetProperty("argumentsJson").GetString().Should().Be("{\"cmd\":\"pwd\"}");
        approval.GetProperty("isDestructive").GetBoolean().Should().BeTrue();
        approval.GetProperty("timeoutSeconds").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task WriteAsync_ShouldMapTypedTaskSnapshotCustomEventToStableJson()
    {
        var sink = new SseFrameSink();
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = "step-alpha",
            ActiveOperationId = "operation-alpha",
            Steps =
            {
                new NyxIdChatTaskStepState
                {
                    StepId = "step-alpha",
                    Order = 1,
                    Kind = NyxIdChatStepKind.Tool,
                    Status = NyxIdChatStepStatus.Running,
                    Required = true,
                    ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                    Operation = new NyxIdChatOperationState
                    {
                        Key = new NyxIdChatOperationKey
                        {
                            ConversationActorId = "conversation-alpha",
                            TurnId = "turn-alpha",
                            TaskId = "task-alpha",
                            StepId = "step-alpha",
                            OperationId = "operation-alpha",
                            OperationGeneration = 1,
                        },
                        Kind = NyxIdChatStepKind.Tool,
                        Phase = NyxIdChatOperationPhase.Requested,
                    },
                },
            },
        };

        await sink.WriteAsync(new AGUIEvent
        {
            Sequence = 17,
            Custom = new CustomEvent
            {
                Name = "nyxid.task.snapshot",
                Payload = Any.Pack(task),
            },
        }, "turn-alpha");

        var frame = sink.ReadFrames().Should().ContainSingle().Which;
        frame.GetProperty("type").GetString().Should().Be("CUSTOM");
        frame.GetProperty("sequence").GetInt64().Should().Be(17);
        var custom = frame.GetProperty("custom");
        custom.GetProperty("name").GetString().Should().Be("nyxid.task.snapshot");
        var payload = custom.GetProperty("payload");
        payload.GetProperty("taskId").GetString().Should().Be("task-alpha");
        payload.GetProperty("turnId").GetString().Should().Be("turn-alpha");
        payload.GetProperty("status").GetString().Should().Be("active");
        var step = payload.GetProperty("steps")[0];
        step.GetProperty("kind").GetString().Should().Be("tool");
        step.GetProperty("status").GetString().Should().Be("running");
        step.GetProperty("externalEffect").GetString().Should().Be("not_started");
        step.GetProperty("operation").GetProperty("phase").GetString().Should().Be("requested");
        frame.GetRawText().Should().NotContain("@type");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapNeedsYouCustomEventsToStableJson()
    {
        var sink = new SseFrameSink();

        await sink.WriteAsync(new AGUIEvent
        {
            Sequence = 23,
            Custom = new CustomEvent
            {
                Name = NyxIdChatConversationAguiFrameBuilder.ApprovalRequestEventName,
                Payload = Any.Pack(new NyxIdChatPendingApprovalState
                {
                    ApprovalRequestId = "approval-alpha",
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    ToolName = "repository_delete",
                    Presentation = new NyxIdChatApprovalPresentation
                    {
                        Action = "delete",
                        Target = "repository:repo-alpha",
                        ActorLabel = "Aevatar Assistant",
                        Reversibility = NyxIdChatApprovalReversibility.Irreversible,
                        GrantBoundary = "within_grant",
                    },
                }),
            },
        }, "turn-alpha");
        await sink.WriteAsync(new AGUIEvent
        {
            Sequence = 24,
            Custom = new CustomEvent
            {
                Name = NyxIdChatConversationAguiFrameBuilder.InputChangedEventName,
                Payload = Any.Pack(new NyxIdChatInputResolutionState
                {
                    RequestId = "input-alpha",
                    ClientRequestId = "client-input-alpha",
                    Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
                }),
            },
        }, "turn-alpha");

        var frames = sink.ReadFrames();
        var approval = frames[0].GetProperty("custom");
        approval.GetProperty("name").GetString().Should().Be("nyxid.approval.request");
        approval.GetProperty("payload").GetProperty("presentation")
            .GetProperty("reversibility").GetString().Should().Be("irreversible");
        approval.GetProperty("payload").GetProperty("presentation")
            .GetProperty("grantBoundary").GetString().Should().Be("within_grant");

        var changed = frames[1].GetProperty("custom");
        changed.GetProperty("name").GetString().Should().Be("nyxid.input.changed");
        changed.GetProperty("payload").GetProperty("outcome").GetString().Should().Be("accepted");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapActionRequestToExactSchemaV4WirePayload()
    {
        var committed = new NyxIdChatActionRequestedEvent
        {
            Request = new NyxIdChatActionRequestState
            {
                SchemaVersion = 4,
                RegistryRevision = "nyxid-assistant-actions.v4",
                ConversationActorId = "conversation-alpha",
                OriginTurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                ActionRequestId = "action-alpha",
                Action = NyxIdAssistantActionKind.ServiceConnect,
                Params = new NyxIdAssistantActionParams
                {
                    CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                    {
                        ServiceSlug = "api-github",
                        RequestedScopes = { "repo" },
                    },
                },
                AdvisoryRisk = NyxIdAssistantActionRisk.Grant,
                RememberEligible = true,
                RequestedAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero)),
            },
            Task = new NyxIdChatTaskState
            {
                TaskId = "task-alpha",
                TurnId = "turn-alpha",
                Status = NyxIdChatTaskStatus.Blocked,
            },
            OriginTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Blocked,
            },
        };
        var actionFrame = NyxIdChatConversationAguiFrameBuilder.BuildActionRequested(
                "conversation-alpha",
                "turn-alpha",
                committed,
                sequence: 23)
            .Single(frame => frame.Custom?.Name ==
                             NyxIdChatConversationAguiFrameBuilder.ActionRequestEventName);
        var sink = new SseFrameSink();

        await sink.WriteAsync(actionFrame, "turn-alpha");

        var payload = sink.ReadFrames().Should().ContainSingle().Which
            .GetProperty("custom")
            .GetProperty("payload");
        var expected = JsonNode.Parse("""
        {
          "schemaVersion": 4,
          "actorId": "conversation-alpha",
          "originTurnId": "turn-alpha",
          "taskId": "task-alpha",
          "stepId": "step-alpha",
          "actionRequestId": "action-alpha",
          "action": "service.connect",
          "params": {
            "catalogService": {
              "serviceSlug": "api-github",
              "requestedScopes": ["repo"]
            }
          }
        }
        """);

        JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), expected)
            .Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_ShouldMapTypedStepControlCustomEventToStableJson()
    {
        var sink = new SseFrameSink();
        var result = new NyxIdChatStepControlResultState
        {
            Kind = NyxIdChatStepControlKind.Retry,
            RequestId = "retry-alpha",
            ClientRequestId = "client-retry-alpha",
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ExpectedOperationGeneration = 1,
            OperationGeneration = 2,
            Outcome = NyxIdChatTransitionOutcome.Accepted,
            ReasonCode = NyxIdChatControlCommands.StepRetryAccepted,
        };

        await sink.WriteAsync(new AGUIEvent
        {
            Sequence = 19,
            Custom = new CustomEvent
            {
                Name = NyxIdChatConversationAguiFrameBuilder.StepControlChangedEventName,
                Payload = Any.Pack(result),
            },
        }, "turn-alpha");

        var frame = sink.ReadFrames().Should().ContainSingle().Which;
        frame.GetProperty("type").GetString().Should().Be("CUSTOM");
        frame.GetProperty("sequence").GetInt64().Should().Be(19);
        var custom = frame.GetProperty("custom");
        custom.GetProperty("name").GetString().Should().Be("nyxid.step.control.changed");
        var payload = custom.GetProperty("payload");
        payload.GetProperty("kind").GetString().Should().Be("retry");
        payload.GetProperty("outcome").GetString().Should().Be("accepted");
        payload.GetProperty("requestId").GetString().Should().Be("retry-alpha");
        payload.GetProperty("operationGeneration").GetString().Should().Be("2");
        frame.GetRawText().Should().NotContain("@type");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapRunErrorAndReturnTerminalStatus()
    {
        var sink = new SseFrameSink();

        var status = await sink.WriteAsync(new AGUIEvent
        {
            RunError = new RunErrorEvent
            {
                Message = "tool approval denied by user bearer-secret",
                RunId = "turn-1",
                Code = "TOOL_APPROVAL_FAILED",
            },
        }, "fallback-turn");

        status.Should().Be("RUN_ERROR");
        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("RUN_ERROR");
        frame.GetProperty("turnId").GetString().Should().Be("turn-1");
        frame.GetProperty("runError").GetProperty("runId").GetString().Should().Be("turn-1");
        frame.GetProperty("runError").GetProperty("code").GetString().Should().Be("TOOL_APPROVAL_FAILED");
        frame.GetProperty("runError").GetProperty("message").GetString().Should().Be(
            "Sorry, something went wrong while generating a response.");
        frame.GetRawText().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapRunFinishedAndReturnTerminalStatus()
    {
        var sink = new SseFrameSink();

        var status = await sink.WriteAsync(new AGUIEvent { RunFinished = new RunFinishedEvent() }, "message-1");

        status.Should().Be("RUN_FINISHED");
        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("RUN_FINISHED");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapAuthorizationRequiredAndBlockedTerminal()
    {
        var sink = new SseFrameSink();
        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            UserServiceId = "us-github-alpha",
            ServiceSlug = "api-github",
            ResourceUri = "/repos/private",
            ReasonCode = "NYXID_UNAUTHORIZED",
            SafeMessage = "Connect or reauthorize api-github to continue.",
        };

        await sink.WriteAsync(new AGUIEvent
        {
            Custom = new CustomEvent
            {
                Name = "nyxid.authorization.required",
                Payload = Any.Pack(blocker),
            },
        }, "turn-blocked");
        await sink.WriteAsync(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                RunId = "turn-blocked",
                Status = RunCompletionStatus.Blocked,
            },
        }, "turn-blocked");

        var frames = sink.ReadFrames();
        frames.Select(frame => frame.GetProperty("type").GetString()).Should().Equal("CUSTOM", "RUN_FINISHED");
        frames[0].GetProperty("custom").GetProperty("name").GetString()
            .Should().Be("nyxid.authorization.required");
        var payload = frames[0].GetProperty("custom").GetProperty("payload");
        payload.GetProperty("userServiceId").GetString().Should().Be("us-github-alpha");
        payload.GetProperty("serviceSlug").GetString().Should().Be("api-github");
        payload.GetProperty("resourceUri").GetString().Should().Be("/repos/private");
        payload.GetProperty("reasonCode").GetString().Should().Be("NYXID_UNAUTHORIZED");
        frames[1].GetProperty("turnId").GetString().Should().Be("turn-blocked");
        frames[1].GetProperty("runFinished").GetProperty("status").GetString().Should().Be("blocked");
    }

    [Fact]
    public async Task WriteAsync_ShouldMapUsageFrame()
    {
        var sink = new SseFrameSink();

        var status = await sink.WriteAsync(new AGUIEvent
        {
            Usage = new UsageEvent
            {
                Available = true,
                PromptTokens = 3,
                CompletionTokens = 5,
                TotalTokens = 8,
                Model = "nyxid-model",
            },
        }, "message-1");

        status.Should().BeNull();
        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("USAGE");
        var usage = frame.GetProperty("usage");
        usage.GetProperty("available").GetBoolean().Should().BeTrue();
        usage.GetProperty("promptTokens").GetInt32().Should().Be(3);
        usage.GetProperty("completionTokens").GetInt32().Should().Be(5);
        usage.GetProperty("totalTokens").GetInt32().Should().Be(8);
        usage.GetProperty("model").GetString().Should().Be("nyxid-model");
    }

    [Fact]
    public async Task WriteKeepAliveAsync_ShouldEmitRunningCustomFrame()
    {
        var sink = new SseFrameSink();

        await sink.WriteKeepAliveAsync("actor-1", "turn-1");

        var frame = sink.ReadFrames().Should().ContainSingle().Subject;
        frame.GetProperty("type").GetString().Should().Be("CUSTOM");
        var custom = frame.GetProperty("custom");
        custom.GetProperty("name").GetString().Should().Be("aevatar.nyxid_chat.keepalive");
        var payload = custom.GetProperty("payload");
        payload.GetProperty("actorId").GetString().Should().Be("actor-1");
        payload.GetProperty("turnId").GetString().Should().Be("turn-1");
        payload.TryGetProperty("sessionId", out _).Should().BeFalse();
        payload.GetProperty("status").GetString().Should().Be("running");
    }

    private sealed class SseFrameSink
    {
        private readonly MemoryStream _body = new();
        private readonly NyxIdChatSseWriter _writer;

        public SseFrameSink()
        {
            var http = new DefaultHttpContext();
            http.Response.Body = _body;
            _writer = new NyxIdChatSseWriter(http.Response);
        }

        public ValueTask<string?> WriteAsync(AGUIEvent aguiEvent, string messageId) =>
            NyxIdChatAguiSseEventWriter.WriteAsync(aguiEvent, messageId, _writer);

        public ValueTask WriteKeepAliveAsync(string actorId, string sessionId) =>
            _writer.WriteKeepAliveAsync(actorId, sessionId, CancellationToken.None);

        public IReadOnlyList<JsonElement> ReadFrames()
        {
            _body.Position = 0;
            var body = new StreamReader(_body).ReadToEnd();
            return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(frame => frame.Trim())
                .Where(frame => frame.StartsWith("data: ", StringComparison.Ordinal))
                .Select(frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.Clone())
                .ToList();
        }
    }
}
