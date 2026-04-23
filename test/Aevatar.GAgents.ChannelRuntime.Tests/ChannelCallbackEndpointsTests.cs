using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCallbackEndpointsTests
{
    [Fact]
    public void MapChannelCallbackEndpoints_ShouldRequireAuthorization_ForDiagnosticErrors()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannelCallbackEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => string.Equals(route.RoutePattern.RawText, "/api/channels/diagnostics/errors", StringComparison.Ordinal));

        endpoint.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleRegisterAsync_RejectsUnsupportedPlatform()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        var result = await InvokeAsync(
            "HandleRegisterAsync",
            CreateJsonHttpContext("""{"platform":"telegram"}"""),
            new[] { provisioningService },
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        response.Body.Should().Contain("supported production contract");
        response.Body.Should().Contain("lark");
        await provisioningService.DidNotReceive().ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_ProvisionsLarkViaNyx()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                RelayCallbackUrl: "https://aevatar.example.com/api/webhooks/nyxid-relay",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            new[] { provisioningService },
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"registration_id\":\"reg-1\"");
        response.Body.Should().Contain("\"relay_callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        await provisioningService.Received(1).ProvisionAsync(
            Arg.Is<NyxChannelBotProvisioningRequest>(request =>
                request.Platform == "lark" &&
                request.AccessToken == "test-token" &&
                request.WebhookBaseUrl == "https://aevatar.example.com" &&
                request.Lark != null &&
                request.Lark.AppId == "cli_123" &&
                request.Lark.AppSecret == "secret"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadGateway_WhenNyxProvisioningFails()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: false,
                Status: "error",
                Platform: "lark",
                Error: "channel_bot_id_request_failed nyx_status=401 body=invalid app secret")));

        var http = CreateJsonHttpContext(
            """{"platform":"lark","app_id":"cli_123","app_secret":"bad-secret","webhook_base_url":"https://aevatar.example.com"}""");
        http.Request.Headers.Authorization = "Bearer test-token";

        var result = await InvokeAsync(
            "HandleRegisterAsync",
            http,
            new[] { provisioningService },
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        response.Body.Should().Contain("\"status\":\"error\"");
        response.Body.Should().Contain("invalid app secret");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_ReturnsRelayModeOnly()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                    NyxProviderSlug = "api-lark-bot",
                    ScopeId = "scope-1",
                    NyxChannelBotId = "bot-1",
                },
            ]));

        var result = await InvokeAsync("HandleListRegistrationsAsync", queryPort, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"registration_mode\":\"nyx_relay_webhook\"");
        response.Body.Should().Contain("\"callback_url\":\"\"");
    }

    [Fact]
    public async Task HandleDeleteRegistrationAsync_DispatchesUnregisterCommand()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
            }));

        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var result = await InvokeAsync("HandleDeleteRegistrationAsync", "reg-1", actorRuntime, queryPort, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotUnregisterCommand>().RegistrationId.Should().Be("reg-1");
    }

    [Fact]
    public async Task HandleTestReplyAsync_ReturnsGone()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxProviderSlug = "api-lark-bot",
            }));

        var result = await InvokeAsync("HandleTestReplyAsync", "reg-1", queryPort, CancellationToken.None);
        var response = await ExecuteResultAsync(result);

        response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        response.Body.Should().Contain("Direct platform reply diagnostics are retired");
    }

    private static async Task<IResult> InvokeAsync(string methodName, params object[] parameters)
    {
        var method = typeof(ChannelCallbackEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        var task = method!.Invoke(null, parameters);
        task.Should().NotBeNull();
        return await (Task<IResult>)task!;
    }

    private static HttpContext CreateHttpContext()
    {
        var builder = WebApplication.CreateBuilder();
        var context = new DefaultHttpContext();
        context.RequestServices = builder.Services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static HttpContext CreateJsonHttpContext(string json)
    {
        var context = CreateHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        return context;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        var builder = WebApplication.CreateBuilder();
        context.RequestServices = builder.Services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
