using System.Text;
using Aevatar.Mainnet.Host.Api.Chat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetChatEndpointsTests
{
    [Theory]
    [InlineData("{}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"prompt\":\"hello\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflow\":\"direct\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflowYamls\":[\"name: inline\"]}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"type\":\"text\"}", "Assistant")]
    [InlineData("{\"type\":\"input.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"text\",\"workflow\":\"studio\"}", "Assistant")]
    [InlineData("{\"type\":\"action.continue\"}", "Assistant")]
    [InlineData("{\"type\":\"approval.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"plan.resolve\"}", "Unsupported")]
    [InlineData("{\"type\":\"task.stop\"}", "Assistant")]
    [InlineData("{\"type\":\"task.steer\"}", "Assistant")]
    [InlineData("{\"type\":\"step.retry\"}", "Assistant")]
    [InlineData("{\"type\":\"step.skip\"}", "Assistant")]
    [InlineData("{\"type\":\"future.type\"}", "Unsupported")]
    public async Task RequestShape_ShouldSelectOneExplicitBoundary(
        string json,
        string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.ToString().Should().Be(expected);
        if (expected == "ExternalWorkflowCompatibility")
        {
            http.Request.Body.Position.Should().Be(0);
            using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
            (await reader.ReadToEndAsync()).Should().Be(json);
        }
    }

    [Fact]
    public async Task ExplicitStudioWorkflowJson_ShouldUseFrozenCompatibilityAdapter()
    {
        const string json = "{\"commandId\":\"cmd-1\",\"conversation\":{\"conversationId\":null},\"prompt\":\"hello\",\"sessionId\":\"session-1\",\"workflow\":\"studio\"}";
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Be(json);
    }

    [Fact]
    public async Task Multipart_ShouldRemainInFrozenExternalCompatibilityAdapterWithoutReadingBody()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("unchanged"));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("text/plain", "hello")]
    [InlineData("application/json", "not-json")]
    [InlineData("application/json", "[]")]
    public async Task UnsupportedOrMalformedInput_ShouldNotFallThroughToWorkflow(
        string contentType,
        string body)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.Unsupported);
    }
}
