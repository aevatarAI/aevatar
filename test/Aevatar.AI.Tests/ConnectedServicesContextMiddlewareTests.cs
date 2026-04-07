using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Studio.Infrastructure.Middleware;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public class ConnectedServicesContextMiddlewareTests
{
    private readonly ConnectedServicesContextMiddleware _middleware =
        new(NullLogger<ConnectedServicesContextMiddleware>.Instance);

    [Fact]
    public async Task InvokeAsync_InjectsConnectedServicesIntoSystemMessage()
    {
        var servicesContext = "<connected-services>\nTest services\n</connected-services>";
        var context = BuildContext("You are an assistant.", servicesContext);
        var nextCalled = false;

        await _middleware.InvokeAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        var systemMsg = context.Request.Messages.First(m => m.Role == "system");
        systemMsg.Content.Should().Contain("<connected-services>");
        systemMsg.Content.Should().StartWith("You are an assistant.");
    }

    [Fact]
    public async Task InvokeAsync_NoMetadata_PassesThrough()
    {
        var context = BuildContext("You are an assistant.", connectedServices: null);
        var nextCalled = false;

        await _middleware.InvokeAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        var systemMsg = context.Request.Messages.First(m => m.Role == "system");
        systemMsg.Content.Should().Be("You are an assistant.");
    }

    [Fact]
    public async Task InvokeAsync_EmptyServices_PassesThrough()
    {
        var context = BuildContext("You are an assistant.", connectedServices: "  ");
        var nextCalled = false;

        await _middleware.InvokeAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        var systemMsg = context.Request.Messages.First(m => m.Role == "system");
        systemMsg.Content.Should().Be("You are an assistant.");
    }

    [Fact]
    public async Task InvokeAsync_PreventDoubleInjection()
    {
        var servicesContext = "<connected-services>\nTest\n</connected-services>";
        var context = BuildContext("Base prompt.", servicesContext);

        await _middleware.InvokeAsync(context, () => Task.CompletedTask);
        var afterFirst = context.Request.Messages.First(m => m.Role == "system").Content;

        // Call again on the same context
        await _middleware.InvokeAsync(context, () => Task.CompletedTask);
        var afterSecond = context.Request.Messages.First(m => m.Role == "system").Content;

        afterSecond.Should().Be(afterFirst, "second injection should be a no-op");
    }

    [Fact]
    public async Task InvokeAsync_NoSystemMessage_PassesThrough()
    {
        var metadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.ConnectedServicesContext] = "some context"
        };
        var context = new LLMCallContext
        {
            Request = new LLMRequest
            {
                Messages = [ChatMessage.User("hello")],
                Metadata = metadata,
            },
            Provider = null!,
        };
        var nextCalled = false;

        await _middleware.InvokeAsync(context, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
    }

    private static LLMCallContext BuildContext(string systemPrompt, string? connectedServices)
    {
        var metadata = new Dictionary<string, string>();
        if (connectedServices is not null)
            metadata[LLMRequestMetadataKeys.ConnectedServicesContext] = connectedServices;

        return new LLMCallContext
        {
            Request = new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(systemPrompt),
                    ChatMessage.User("hello"),
                ],
                Metadata = metadata,
            },
            Provider = null!,
        };
    }
}
