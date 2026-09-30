using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConnectLinkCallbackEndpointsTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("untrusted-nonsense")]
    public async Task BrowserStatus_IsOnlyWakeupHint_AndReceiptPromisesAccepted(string status)
    {
        var callbacks = Substitute.For<IExternalCallbackCommandPort>();
        var context = Context($"?connect_link_id=link-exact&status={status}");

        var result = await ConnectLinkCallbackEndpoints.HandleAsync(context, callbacks, CancellationToken.None);
        await result.ExecuteAsync(context);

        await callbacks.Received(1).HintAsync(null, "link-exact", Arg.Any<CancellationToken>());
        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Theory]
    [InlineData("?status=completed")]
    [InlineData("?connect_link_id=first&connect_link_id=second")]
    [InlineData("?connect_link_id=link-exact&callback_id=first&callback_id=second")]
    [InlineData("?connect_link_id=%0A")]
    public async Task MalformedHint_IsRejectedWithoutDispatch(string query)
    {
        var callbacks = Substitute.For<IExternalCallbackCommandPort>();
        var context = Context(query);

        await (await ConnectLinkCallbackEndpoints.HandleAsync(context, callbacks, CancellationToken.None)).ExecuteAsync(context);

        await callbacks.DidNotReceiveWithAnyArgs().HintAsync(default, default!, default);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task MissingProjection_IsHonestNotFound_AndDoesNotTreatBrowserSuccessAsFact()
    {
        var callbacks = Substitute.For<IExternalCallbackCommandPort>();
        callbacks.HintAsync(null, "link-missing", Arg.Any<CancellationToken>()).Returns(Task.FromException(new KeyNotFoundException()));
        var context = Context("?connect_link_id=link-missing&status=completed");

        await (await ConnectLinkCallbackEndpoints.HandleAsync(context, callbacks, CancellationToken.None)).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    private static DefaultHttpContext Context(string query) => new()
    {
        Request = { QueryString = new QueryString(query) },
        Response = { Body = new MemoryStream() },
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
    };
}
