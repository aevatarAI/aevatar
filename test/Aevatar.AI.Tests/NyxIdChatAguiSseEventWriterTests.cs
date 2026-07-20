using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
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
            ToolCallStart = new ToolCallStartEvent { ToolName = "web.search", ToolCallId = "call-1" },
        }, "message-1");
        await sink.WriteAsync(new AGUIEvent
        {
            ToolCallEnd = new ToolCallEndEvent { ToolCallId = "call-1", Result = "done" },
        }, "message-1");

        var frames = sink.ReadFrames();
        frames.Should().HaveCount(2);
        frames[0].GetProperty("type").GetString().Should().Be("TOOL_CALL_START");
        frames[0].GetProperty("toolCallStart").GetProperty("toolName").GetString().Should().Be("web.search");
        frames[0].GetProperty("toolCallStart").GetProperty("toolCallId").GetString().Should().Be("call-1");
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
