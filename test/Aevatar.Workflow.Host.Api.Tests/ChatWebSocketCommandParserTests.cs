using System.Net.WebSockets;
using System.Text;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ChatWebSocketCommandParserTests
{
    [Fact]
    public void TryParse_EmptyCommand_ShouldReturnError()
    {
        var ok = ChatWebSocketCommandParser.TryParse((ChatWebSocketInboundFrame?)null, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("EMPTY_COMMAND");
        error.ResponseMessageType.Should().Be(WebSocketMessageType.Text);
    }

    [Fact]
    public void TryParse_InvalidShape_ShouldReturnError()
    {
        var frame = TextFrame("""{"type":"unknown","payload":{"prompt":"hi"}}""");

        var ok = ChatWebSocketCommandParser.TryParse(frame, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_COMMAND");
    }

    [Fact]
    public void TryParse_BinaryInvalidShape_ShouldReturnErrorAndKeepBinaryResponseType()
    {
        var frame = new ChatWebSocketInboundFrame(
            WebSocketMessageType.Binary,
            Encoding.UTF8.GetBytes("""{"type":"unknown","payload":{"prompt":"hi"}}"""));

        var ok = ChatWebSocketCommandParser.TryParse(frame, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_COMMAND");
        error.ResponseMessageType.Should().Be(WebSocketMessageType.Binary);
    }

    [Fact]
    public void TryParse_EmptyPrompt_ShouldReturnPromptErrorWithRequestId()
    {
        var frame = TextFrame("""{"type":"chat.command","requestId":"req-1","payload":{"prompt":""}}""");

        var ok = ChatWebSocketCommandParser.TryParse(frame, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_PROMPT");
        error.RequestId.Should().Be("req-1");
    }

    [Fact]
    public void TryParse_ValidCommand_ShouldReturnEnvelope()
    {
        var frame = TextFrame("""{"type":"chat.command","requestId":"req-9","payload":{"prompt":"hello","workflow":"direct","agentId":"a-1"}}""");

        var ok = ChatWebSocketCommandParser.TryParse(frame, out var envelope, out _);

        ok.Should().BeTrue();
        envelope.RequestId.Should().Be("req-9");
        envelope.Input.Prompt.Should().Be("hello");
        envelope.Input.Workflow.Should().Be("direct");
        envelope.Input.AgentId.Should().Be("a-1");
    }

    [Fact]
    public void TryParse_BinaryFrame_ShouldKeepBinaryResponseType()
    {
        var frame = new ChatWebSocketInboundFrame(
            WebSocketMessageType.Binary,
            Encoding.UTF8.GetBytes("""{"type":"chat.command","requestId":"req-b","payload":{"prompt":"hello"}}"""));

        var ok = ChatWebSocketCommandParser.TryParse(frame, out var envelope, out _);

        ok.Should().BeTrue();
        envelope.RequestId.Should().Be("req-b");
        envelope.ResponseMessageType.Should().Be(WebSocketMessageType.Binary);
        envelope.Input.Prompt.Should().Be("hello");
    }

    [Fact]
    public void TryParse_BinaryFrameWithInvalidUtf8_ShouldReturnEncodingError()
    {
        var frame = new ChatWebSocketInboundFrame(
            WebSocketMessageType.Binary,
            new byte[] { 0xC3, 0x28 });

        var ok = ChatWebSocketCommandParser.TryParse(frame, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_COMMAND_ENCODING");
        error.ResponseMessageType.Should().Be(WebSocketMessageType.Binary);
    }

    private static ChatWebSocketInboundFrame TextFrame(string json) =>
        new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));
}
