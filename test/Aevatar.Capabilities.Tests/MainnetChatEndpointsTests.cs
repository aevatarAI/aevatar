using System.Text;
using Aevatar.Mainnet.Host.Api.Chat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetChatEndpointsTests
{
    [Theory]
    [InlineData("{}", "Workflow")]
    [InlineData("{\"prompt\":\"hello\"}", "Workflow")]
    [InlineData("{\"type\":\"text\"}", "Assistant")]
    [InlineData("{\"type\":\"action.continue\"}", "Assistant")]
    [InlineData("{\"type\":\"approval.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"task.stop\"}", "Assistant")]
    [InlineData("{\"type\":\"task.steer\"}", "Assistant")]
    [InlineData("{\"type\":\"step.retry\"}", "Assistant")]
    [InlineData("{\"type\":\"step.skip\"}", "Assistant")]
    [InlineData("{\"type\":\"future.type\"}", "Unsupported")]
    public async Task JsonDiscriminator_ShouldSelectOneExplicitSurface(
        string json,
        string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.ToString().Should().Be(expected);
        if (expected == "Workflow")
        {
            http.Request.Body.Position.Should().Be(0);
            using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
            (await reader.ReadToEndAsync()).Should().Be(json);
        }
    }

    [Fact]
    public async Task Multipart_ShouldRemainWorkflowChatWithoutReadingBody()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("unchanged"));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.Workflow);
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
