using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ChatWebSocketCommandParserTests
{
    [Fact]
    public void TryParse_EmptyCommand_ShouldReturnError()
    {
        var ok = ChatWebSocketCommandParser.TryParse(null, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("EMPTY_COMMAND");
    }

    [Fact]
    public void TryParse_InvalidShape_ShouldReturnError()
    {
        const string payload = """{"type":"unknown","payload":{"prompt":"hi"}}""";

        var ok = ChatWebSocketCommandParser.TryParse(payload, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_COMMAND");
    }

    [Fact]
    public void TryParse_EmptyPrompt_ShouldReturnPromptErrorWithRequestId()
    {
        const string payload = """{"type":"chat.command","requestId":"req-1","payload":{"prompt":""}}""";

        var ok = ChatWebSocketCommandParser.TryParse(payload, out _, out var error);

        ok.Should().BeFalse();
        error.Code.Should().Be("INVALID_PROMPT");
        error.RequestId.Should().Be("req-1");
    }

    [Fact]
    public void TryParse_ValidCommand_ShouldReturnEnvelope()
    {
        const string payload = """{"type":"chat.command","requestId":"req-9","payload":{"prompt":"hello","workflow":"direct","agentId":"a-1"}}""";

        var ok = ChatWebSocketCommandParser.TryParse(payload, out var envelope, out _);

        ok.Should().BeTrue();
        envelope.RequestId.Should().Be("req-9");
        envelope.Input.Prompt.Should().Be("hello");
        envelope.Input.Workflow.Should().Be("direct");
        envelope.Input.AgentId.Should().Be("a-1");
    }
}
