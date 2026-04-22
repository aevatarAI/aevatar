using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCallbackEndpointsTests
{
    private static readonly System.Type EndpointsType = typeof(ChannelCallbackEndpoints);

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
    public void ResolveUpdatedRefreshToken_Preserves_Existing_Value_When_Request_Omits_RefreshToken()
    {
        var resolved = ChannelCallbackEndpoints.ResolveUpdatedRefreshToken(null, "refresh-old");

        resolved.Should().Be("refresh-old");
    }

    [Fact]
    public void ResolveUpdatedRefreshToken_Uses_Explicit_Value_When_Request_Provides_RefreshToken()
    {
        var resolved = ChannelCallbackEndpoints.ResolveUpdatedRefreshToken("refresh-new", "refresh-old");

        resolved.Should().Be("refresh-new");
    }

    [Fact]
    public async Task HandleCallbackAsync_ReturnsGone_ForRetiredLarkDirectCallbacks()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();

        var result = await InvokeResultAsync(
            "HandleCallbackAsync",
            CreateHttpContext(),
            "lark",
            "reg-lark",
            runtimeQueryPort,
            Array.Empty<IPlatformAdapter>(),
            Substitute.For<IActorRuntime>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        response.Body.Should().Contain("Lark direct callback is retired");
        response.Body.Should().Contain("reg-lark");
        await runtimeQueryPort.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_ReturnsWorkflowResumeServiceUnavailable_ForTextWorkflowCommands()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-telegram",
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
        };
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("reg-telegram", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

        var adapter = CreateAdapter(
            "telegram",
            new InboundMessage
            {
                Platform = "telegram",
                ConversationId = "chat-1",
                SenderId = "user-1",
                SenderName = "User One",
                Text = "/approve actor_id=actor-1 run_id=run-1 step_id=step-1",
                MessageId = "msg-1",
                ChatType = "private",
            });

        var http = CreateHttpContext(new ServiceCollection().BuildServiceProvider());
        var result = await InvokeResultAsync(
            "HandleCallbackAsync",
            http,
            "telegram",
            "reg-telegram",
            runtimeQueryPort,
            new[] { adapter },
            Substitute.For<IActorRuntime>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        response.Body.Should().Contain("workflow_resume_service_unavailable");
    }

    [Fact]
    public async Task HandleCallbackAsync_EnqueuesChannelInboundEvent_UsingDirectCallbackToken()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-telegram",
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
        };
        registration.ApplyDirectCallbackBinding(new ChannelBotDirectCallbackBinding
        {
            NyxUserToken = "token-direct",
            NyxRefreshToken = "refresh-direct",
        });

        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("reg-telegram", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

        var adapter = CreateAdapter(
            "telegram",
            new InboundMessage
            {
                Platform = "telegram",
                ConversationId = "chat-42",
                SenderId = "user-42",
                SenderName = "User Forty Two",
                Text = "hello",
                MessageId = "msg-42",
                ChatType = "group",
                Extra = new Dictionary<string, string>
                {
                    ["thread_id"] = "thread-1",
                },
            });

        var userActorId = "channel-user-telegram-reg-telegram-user-42";
        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(userActorId);
        actor.HandleEventAsync(Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(userActorId).Returns(Task.FromResult<IActor?>(actor));

        var result = await InvokeResultAsync(
            "HandleCallbackAsync",
            CreateHttpContext(),
            "telegram",
            "reg-telegram",
            runtimeQueryPort,
            new[] { adapter },
            actorRuntime,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"status\":\"accepted\"");
        capturedEnvelope.Should().NotBeNull();

        var inboundEvent = capturedEnvelope!.Payload.Unpack<ChannelInboundEvent>();
        inboundEvent.RegistrationId.Should().Be("reg-telegram");
        inboundEvent.RegistrationToken.Should().Be("token-direct");
        inboundEvent.ConversationId.Should().Be("chat-42");
        inboundEvent.Extra["thread_id"].Should().Be("thread-1");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenPlatformIsMissing()
    {
        var result = await InvokeResultAsync(
            "HandleRegisterAsync",
            CreateJsonHttpContext("{}"),
            Substitute.For<IActorRuntime>(),
            Array.Empty<IPlatformAdapter>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("platform is required");
    }

    [Fact]
    public async Task HandleRegisterAsync_ReturnsBadRequest_WhenDirectCallbackCredentialsAreMissing()
    {
        var adapter = CreateAdapter("telegram", inbound: null);
        var result = await InvokeResultAsync(
            "HandleRegisterAsync",
            CreateJsonHttpContext("""{"platform":"telegram"}"""),
            Substitute.For<IActorRuntime>(),
            new[] { adapter },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("nyx_provider_slug and nyx_user_token are required for direct-callback registration");
    }

    [Fact]
    public async Task HandleRegisterAsync_AcceptsDirectCallbackRegistration_AndPersistsDirectBinding()
    {
        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(actor));

        var adapter = CreateAdapter("telegram", inbound: null);
        var result = await InvokeResultAsync(
            "HandleRegisterAsync",
            CreateJsonHttpContext(
                """
                {
                  "platform":"telegram",
                  "nyx_provider_slug":"api-telegram-bot",
                  "nyx_user_token":"token-1",
                  "nyx_refresh_token":"refresh-1",
                  "verification_token":"verify-1",
                  "scope_id":"scope-1",
                  "webhook_base_url":"https://aevatar.example.com/",
                  "credential_ref":"vault://channels/telegram/reg-1"
                }
                """),
            actorRuntime,
            new[] { adapter },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"status\":\"accepted\"");
        response.Body.Should().Contain("\"platform\":\"telegram\"");
        response.Body.Should().Contain("\"refresh_token_present\":true");

        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>();
        command.Platform.Should().Be("telegram");
        command.NyxProviderSlug.Should().Be("api-telegram-bot");
        command.ScopeId.Should().Be("scope-1");
        command.WebhookUrl.Should().Be("https://aevatar.example.com/api/channels/telegram/callback");
        command.DirectCallbackBinding.Should().NotBeNull();
        command.DirectCallbackBinding!.NyxUserToken.Should().Be("token-1");
        command.DirectCallbackBinding.NyxRefreshToken.Should().Be("refresh-1");
        command.DirectCallbackBinding.VerificationToken.Should().Be("verify-1");
        command.DirectCallbackBinding.CredentialRef.Should().Be("vault://channels/telegram/reg-1");
    }

    [Fact]
    public async Task HandleListRegistrationsAsync_UsesDirectCallbackRegistrationMode()
    {
        var directCallback = new ChannelBotRegistrationEntry
        {
            Id = "telegram-1",
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
        };
        var nyxRelay = new ChannelBotRegistrationEntry
        {
            Id = "lark-1",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxAgentApiKeyId = "agent-key-1",
        };

        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([directCallback, nyxRelay]));

        var result = await InvokeResultAsync(
            "HandleListRegistrationsAsync",
            queryPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("\"registration_mode\":\"direct_callback\"");
        response.Body.Should().Contain("\"registration_mode\":\"nyx_relay_webhook\"");
    }

    [Fact]
    public async Task HandleUpdateTokenAsync_ReturnsConflict_ForLarkRelayRegistrations()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("lark-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "lark-1",
                Platform = "lark",
            }));

        var result = await InvokeResultAsync(
            "HandleUpdateTokenAsync",
            "lark-1",
            CreateJsonHttpContext("""{"nyx_user_token":"fresh"}"""),
            Substitute.For<IActorRuntime>(),
            queryPort,
            Substitute.For<IChannelBotRegistrationRuntimeQueryPort>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        response.Body.Should().Contain("do not use persisted Nyx session tokens");
    }

    [Fact]
    public async Task HandleUpdateTokenAsync_AcceptsDirectCallbackUpdate_AndPreservesExistingSecretFields()
    {
        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(actor));

        var publicRegistration = new ChannelBotRegistrationEntry
        {
            Id = "telegram-1",
            Platform = "telegram",
        };

        var runtimeRegistration = new ChannelBotRegistrationEntry
        {
            Id = "telegram-1",
            Platform = "telegram",
        };
        runtimeRegistration.ApplyDirectCallbackBinding(new ChannelBotDirectCallbackBinding
        {
            NyxUserToken = "old-token",
            NyxRefreshToken = "refresh-old",
            VerificationToken = "verify-old",
            CredentialRef = "vault://channels/telegram/reg-1",
            EncryptKey = "encrypt-old",
        });

        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("telegram-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(publicRegistration));
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("telegram-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(runtimeRegistration));

        var result = await InvokeResultAsync(
            "HandleUpdateTokenAsync",
            "telegram-1",
            CreateJsonHttpContext("""{"nyx_user_token":"fresh-token"}"""),
            actorRuntime,
            queryPort,
            runtimeQueryPort,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("\"status\":\"accepted\"");

        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<ChannelBotUpdateTokenCommand>();
        command.RegistrationId.Should().Be("telegram-1");
        command.DirectCallbackBinding.Should().NotBeNull();
        command.DirectCallbackBinding!.NyxUserToken.Should().Be("fresh-token");
        command.DirectCallbackBinding.NyxRefreshToken.Should().Be("refresh-old");
        command.DirectCallbackBinding.VerificationToken.Should().Be("verify-old");
        command.DirectCallbackBinding.CredentialRef.Should().Be("vault://channels/telegram/reg-1");
        command.DirectCallbackBinding.EncryptKey.Should().Be("encrypt-old");
    }

    [Fact]
    public async Task HandleTestReplyAsync_ReturnsAdapterError_WithDirectCallbackDiagnostics()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "telegram-1",
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
            ScopeId = "scope-1",
        };
        registration.ApplyDirectCallbackBinding(new ChannelBotDirectCallbackBinding
        {
            NyxUserToken = "token-123",
            NyxRefreshToken = "refresh-456",
        });

        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("telegram-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

        var result = await InvokeResultAsync(
            "HandleTestReplyAsync",
            CreateJsonHttpContext("""{"chat_id":"oc_test_1","message":"hello"}"""),
            "telegram-1",
            runtimeQueryPort,
            Array.Empty<IPlatformAdapter>(),
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("No adapter for platform: telegram");
        response.Body.Should().Contain("\"nyx_user_token_present\":true");
        response.Body.Should().Contain("\"nyx_refresh_token_present\":true");
        response.Body.Should().Contain("\"nyx_user_token_length\":9");
        response.Body.Should().Contain("\"nyx_refresh_token_length\":11");
    }

    private static IPlatformAdapter CreateAdapter(string platform, InboundMessage? inbound)
    {
        var adapter = Substitute.For<IPlatformAdapter>();
        adapter.Platform.Returns(platform);
        adapter.TryHandleVerificationAsync(Arg.Any<HttpContext>(), Arg.Any<ChannelBotRegistrationEntry>())
            .Returns(Task.FromResult<IResult?>(null));
        adapter.ParseInboundAsync(Arg.Any<HttpContext>(), Arg.Any<ChannelBotRegistrationEntry>())
            .Returns(Task.FromResult(inbound));
        adapter.SendReplyAsync(
                Arg.Any<string>(),
                Arg.Any<InboundMessage>(),
                Arg.Any<ChannelBotRegistrationEntry>(),
                Arg.Any<NyxIdApiClient>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlatformReplyDeliveryResult(true, "ok")));
        return adapter;
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider? services = null)
    {
        return new DefaultHttpContext
        {
            RequestServices = services ?? new ServiceCollection().BuildServiceProvider(),
            Request =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static DefaultHttpContext CreateJsonHttpContext(string json, IServiceProvider? services = null)
    {
        var context = CreateHttpContext(services);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context;
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object?[] args)
    {
        var method = EndpointsType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, args);
        return result switch
        {
            Task<IResult> task => await task,
            ValueTask<IResult> valueTask => await valueTask,
            _ => throw new InvalidOperationException($"Unexpected return type: {result?.GetType().FullName}"),
        };
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
