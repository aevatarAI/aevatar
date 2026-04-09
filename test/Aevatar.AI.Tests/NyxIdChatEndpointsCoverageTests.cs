using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Authentication.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public class NyxIdChatEndpointsCoverageTests
{
    private static readonly System.Type EndpointsType = typeof(NyxIdChatEndpoints);

    [Fact]
    public void MapNyxIdChatEndpoints_ShouldRegisterExpectedRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapNyxIdChatEndpoints();

        var routes = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations");
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:stream");
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve");
        routes.Should().Contain("/api/webhooks/nyxid-relay");
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnCreatedConversation()
    {
        var store = new NyxIdChatActorStore();
        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            store,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.TryGetProperty("actorId", out var actorId).Should().BeTrue();
        actorId.GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.TryGetProperty("createdAt", out _).Should().BeTrue();
        (await store.ListActorsAsync("scope-a")).Should().ContainSingle();
    }

    [Fact]
    public async Task HandleListConversationsAsync_ShouldReturnList()
    {
        var store = new NyxIdChatActorStore();
        await store.CreateActorAsync("scope-a");

        var result = await InvokeResultAsync(
            "HandleListConversationsAsync",
            new DefaultHttpContext(),
            "scope-a",
            store,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldReturnNotFound_WhenMissing()
    {
        var store = new NyxIdChatActorStore();
        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "missing-id",
            store,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectWithoutAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer";
        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
            runtime,
            subscriptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectWhenNoPromptAndNoInputParts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        var runtime = new StubActorRuntime();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(null),
            runtime,
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldRejectWithoutAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer";
        var runtime = new StubActorRuntime();

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req"),
            runtime,
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldRejectWhenRequestIdMissing()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        var runtime = new StubActorRuntime();

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest(null),
            runtime,
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldDispatchChatRequest_AndWriteRunFinished()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<INyxIdUserLlmPreferencesStore>(new StubPreferencesStore("relay-model", "/relay-route", 7))
                .AddSingleton<IUserMemoryStore>(new StubUserMemoryStore("remember this"))
                .BuildServiceProvider(),
        };
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope { Payload = Any.Pack(new TextMessageStartEvent()) },
                new EventEnvelope { Payload = Any.Pack(new TextMessageContentEvent { Delta = "hello" }) },
                new EventEnvelope { Payload = Any.Pack(new TextMessageEndEvent { Content = "done" }) },
            },
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello there"),
            runtime,
            subscriptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        runtime.CreateCalls.Should().ContainSingle();
        var actor = runtime.Actors["actor-1"].Should().BeOfType<StubActor>().Subject;
        var chatRequest = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Prompt.Should().Be("hello there");
        chatRequest.ScopeId.Should().Be("scope-a");
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("valid-token");
        chatRequest.Metadata["scope_id"].Should().Be("scope-a");
        chatRequest.Metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("relay-model");
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/relay-route");
        chatRequest.Metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("7");
        chatRequest.Metadata[LLMRequestMetadataKeys.UserMemoryPrompt].Should().Be("remember this");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("TEXT_MESSAGE_START");
        body.Should().Contain("hello");
        body.Should().Contain("TEXT_MESSAGE_END");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldReturn500_WhenFailureOccursBeforeWriterStarts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
            new ThrowingActorRuntime(new InvalidOperationException("runtime failed")),
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldWriteRunError_WhenFailureOccursAfterWriterStarts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
            runtime,
            new ThrowingSubscriptionProvider(new InvalidOperationException("subscription failed")),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("subscription failed");
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldDispatchDecision_AndWriteRunFinished()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope { Payload = Any.Pack(new TextMessageStartEvent()) },
                new EventEnvelope { Payload = Any.Pack(new TextMessageEndEvent { Content = "done" }) },
            },
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req-1", Approved: false, Reason: "deny", SessionId: "session-1"),
            runtime,
            subscriptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var actor = runtime.Actors["actor-1"].Should().BeOfType<StubActor>().Subject;
        var decision = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ToolApprovalDecisionEvent>();
        decision.RequestId.Should().Be("req-1");
        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be("deny");
        decision.SessionId.Should().Be("session-1");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldReturn500_WhenFailureOccursBeforeWriterStarts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req-1"),
            new ThrowingActorRuntime(new InvalidOperationException("runtime failed")),
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldWriteRunError_WhenFailureOccursAfterWriterStarts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req-1"),
            runtime,
            new ThrowingSubscriptionProvider(new InvalidOperationException("approval subscription failed")),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("approval subscription failed");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldReturnParseError_ForInvalidJson()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{ invalid"));
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            new NyxIdChatActorStore(),
            new NyxIdRelayOptions(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("couldn't understand");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectMissingText()
    {
        var payload = """{"content":{}}""";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            new NyxIdChatActorStore(),
            new NyxIdRelayOptions(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("empty message");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenUserTokenMissing()
    {
        var payload = """{"content":{"text":"hello"}}""";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            new NyxIdChatActorStore(),
            new NyxIdRelayOptions(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("Authentication is not configured properly");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldReturnPartialResponse_WhenTimedOut()
    {
        var payload = """
            {
              "message_id":"msg-1",
              "platform":"slack",
              "agent":{"api_key_id":"scope-a"},
              "conversation":{"platform_id":"room-1"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-NyxID-User-Token"] = "not-a-jwt";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope { Payload = Any.Pack(new TextMessageContentEvent { Delta = "partial reply" }) },
            },
        };
        var store = new NyxIdChatActorStore();

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            subscriptions,
            store,
            new NyxIdRelayOptions { ResponseTimeoutSeconds = 0 },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("partial reply");
        (await store.ListActorsAsync("scope-a")).Should().ContainSingle("nyxid-relay-slack-room-1");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldClassifyError_AndAppendDiagnostics()
    {
        var payload = """
            {
              "message_id":"msg-2",
              "platform":"discord",
              "agent":{"api_key_id":"scope-b"},
              "conversation":{"id":"conv-1","platform_id":"room-2"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Aevatar:NyxId:DefaultModel"] = "server-fallback",
                    })
                    .Build())
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-NyxID-User-Token"] = "not-a-jwt";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            new StubSubscriptionProvider
            {
                Messages =
                {
                    new EventEnvelope
                    {
                        Payload = Any.Pack(new TextMessageEndEvent
                        {
                            Content = "[[AEVATAR_LLM_ERROR]]request failed with 403",
                        }),
                    },
                },
            },
            new NyxIdChatActorStore(),
            new NyxIdRelayOptions
            {
                ResponseTimeoutSeconds = 1,
                EnableDebugDiagnostics = true,
            },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Body.Should().Contain("403 Forbidden");
        response.Body.Should().Contain("[Debug]");
        response.Body.Should().Contain("Route: gateway");
        response.Body.Should().Contain("Token: present");
        response.Body.Should().Contain("Scope: scope-b");
    }

    [Fact]
    public void ExtractBearerToken_ShouldParseBearerHeaderAndIgnoreOthers()
    {
        var context = new DefaultHttpContext();
        var method = EndpointsType.GetMethod("ExtractBearerToken", BindingFlags.NonPublic | BindingFlags.Static)!;

        context.Request.Headers.Authorization = "Basic abc";
        method.Invoke(null, [context]).Should().BeNull();

        context.Request.Headers.Authorization = "Bearer my-token";
        method.Invoke(null, [context]).Should().Be("my-token");
    }

    [Fact]
    public void ComputeTokenHash_ShouldBeDeterministicShortLowercaseHex()
    {
        var method = EndpointsType.GetMethod("ComputeTokenHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = method.Invoke(null, ["abc"])!.Should().NotBeNull().And.BeOfType<string>().Subject;
        var second = method.Invoke(null, ["abc"])!.Should().NotBeNull().And.BeOfType<string>().Subject;

        first.Should().Be(second);
        first.Length.Should().Be(16);
        first.Should().MatchRegex("^[a-f0-9]{16}$");
    }

    [Fact]
    public void BuildConnectedServicesContext_ShouldRenderServiceHintsAndFallbackMessage()
    {
        var method = EndpointsType.GetMethod("BuildConnectedServicesContext", BindingFlags.NonPublic | BindingFlags.Static)!;

        var arrayPayload = """
            [
              {"slug":"calendar","label":"Calendar","base_url":"https://api.example.com"}
            ]
            """;
        var arrayContext = (string)method.Invoke(null, [arrayPayload])!;
        arrayContext.Should().Contain("calendar");
        arrayContext.Should().Contain("Use nyxid_proxy");

        var emptyContext = (string)method.Invoke(null, ["""{"services":[]}"""])!;
        emptyContext.Should().Contain("No services connected yet");
    }

    [Fact]
    public void BuildConnectedServicesContext_ShouldHandleDataShape_AndInvalidJson()
    {
        var method = EndpointsType.GetMethod("BuildConnectedServicesContext", BindingFlags.NonPublic | BindingFlags.Static)!;

        var dataPayload = """
            {
              "data":[
                {"slug":"github","name":"GitHub","endpoint_url":"https://api.github.com"}
              ]
            }
            """;
        var dataContext = (string)method.Invoke(null, [dataPayload])!;
        dataContext.Should().Contain("GitHub");
        dataContext.Should().Contain("https://api.github.com");

        var invalidContext = (string)method.Invoke(null, ["{ invalid"])!;
        invalidContext.Should().Contain("No services connected yet");
        invalidContext.Should().Contain("Use nyxid_proxy");
    }

    [Fact]
    public void ClassifyError_ShouldMapKnownCodePatterns()
    {
        var method = EndpointsType.GetMethod("ClassifyError", BindingFlags.NonPublic | BindingFlags.Static)!;

        method.Invoke(null, ["request failed with 403"])!.Should().Be(
            "Sorry, I can't reach the AI service right now (403 Forbidden).");
        method.Invoke(null, ["status=401 unauthorized"])!.Should().Be(
            "Sorry, authentication with the AI service failed (401).");
        method.Invoke(null, ["service rate limit reached"])!.Should().Be(
            "Sorry, the AI service is busy right now (429). Please wait a moment and try again.");
        method.Invoke(null, ["LLM request timeout"])!.Should().Be(
            "Sorry, the AI service took too long to respond. Please try again.");
        method.Invoke(null, ["model `gpt-5` not found"])!.Should().Be(
            "Sorry, the configured AI model is not available.");
        method.Invoke(null, ["unknown issue"])!.Should().Be(
            "Sorry, something went wrong while generating a response.");
    }

    [Fact]
    public void BuildRelayDiagnostic_ShouldUseServerDefaultsAndTokenFlag()
    {
        var method = EndpointsType.GetMethod("BuildRelayDiagnostic", BindingFlags.NonPublic | BindingFlags.Static)!;
        var metadata = new MapField<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdRoutePreference] = "direct",
            [LLMRequestMetadataKeys.ModelOverride] = "deepseek-chat",
            [AevatarStandardClaimTypes.ScopeId] = "scope-a",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "secret",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:DefaultModel"] = "fallback-model",
            })
            .Build();

        var diag = method.Invoke(null, [metadata, configuration, "LLM request failed: timeout"])!.Should()
            .NotBeNull()
            .And.BeOfType<string>()
            .Subject;

        diag.Should().Contain("Model: deepseek-chat (from config.json)");
        diag.Should().Contain("Route: direct");
        diag.Should().Contain("Scope: scope-a");
        diag.Should().Contain("Token: present");
        diag.Should().Contain("timeout");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapAndWriteEventAsync_ShouldSerializePayloads(bool hasError)
    {
        var envelope = hasError
            ? new EventEnvelope { Payload = Any.Pack(new TextMessageEndEvent { Content = hasError ? "[[AEVATAR_LLM_ERROR]]boom" : string.Empty }) }
            : new EventEnvelope { Payload = Any.Pack(new TextMessageStartEvent()) };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(NyxIdChatEndpoints).Assembly,
            "Aevatar.GAgents.NyxidChat.NyxIdChatSseWriter",
            context.Response);

        var method = EndpointsType.GetMethod("MapAndWriteEventAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var terminal = await InvokeValueTaskAsync<string?>(method, envelope, "m-1", writer);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        if (hasError)
        {
            terminal.Should().Be("RUN_ERROR");
            body.Should().Contain("RUN_ERROR");
        }
        else
        {
            terminal.Should().BeNull();
            body.Should().Contain("TEXT_MESSAGE_START");
        }
    }

    [Fact]
    public async Task MapAndWriteEventAsync_ShouldSerializeContentToolingMediaAndNormalEnd()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(NyxIdChatEndpoints).Assembly,
            "Aevatar.GAgents.NyxidChat.NyxIdChatSseWriter",
            context.Response);

        var method = EndpointsType.GetMethod("MapAndWriteEventAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope { Payload = Any.Pack(new TextMessageContentEvent { Delta = "delta-1" }) },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new ToolCallEvent
                {
                    ToolName = "search",
                    CallId = "call-1",
                    ArgumentsJson = "{\"q\":\"abc\"}",
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new ToolResultEvent
                {
                    CallId = "call-1",
                    ResultJson = "{\"ok\":true}",
                    Success = true,
                    Error = string.Empty,
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new ToolApprovalRequestEvent
                {
                    RequestId = "req-1",
                    SessionId = "s1",
                    ToolName = "connector.run",
                    ToolCallId = "call-1",
                    ArgumentsJson = "{}",
                    IsDestructive = true,
                    TimeoutSeconds = 30,
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Uri = "https://example.com/cat.png",
                        MediaType = "image/png",
                        Name = "cat",
                    },
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Audio,
                        DataBase64 = "YXVkaW8=",
                        MediaType = "audio/mpeg",
                        Name = "clip",
                    },
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Video,
                        Uri = "https://example.com/video.mp4",
                        MediaType = "video/mp4",
                        Name = "clip-video",
                    },
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Text,
                        Text = "inline note",
                    },
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                    Part = new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Unspecified,
                        Name = "mystery",
                    },
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope
            {
                Payload = Any.Pack(new MediaContentEvent
                {
                    SessionId = "session-1",
                    AgentId = "agent-1",
                }),
            },
            "m-2",
            writer)).Should().BeNull();
        (await InvokeValueTaskAsync<string?>(
            method,
            new EventEnvelope { Payload = Any.Pack(new TextMessageEndEvent { Content = "done" }) },
            "m-2",
            writer)).Should().Be("TEXT_MESSAGE_END");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("delta-1");
        body.Should().Contain("search");
        body.Should().Contain("call-1");
        body.Should().Contain("req-1");
        body.Should().Contain("cat.png");
        body.Should().Contain("\"kind\":\"audio\"");
        body.Should().Contain("\"kind\":\"video\"");
        body.Should().Contain("\"kind\":\"text\"");
        body.Should().Contain("\"kind\":\"unknown\"");
        body.Should().Contain("TEXT_MESSAGE_END");
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
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

    private static async Task InvokeTaskAsync(string methodName, params object[] args)
    {
        var method = EndpointsType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, args)!;
        switch (result)
        {
            case ValueTask valueTask:
                await valueTask;
                return;
            case Task task:
                await task;
                return;
            default:
                throw new InvalidOperationException($"Unexpected return type: {result.GetType().FullName}");
        }
    }

    private static async Task<T> InvokeValueTaskAsync<T>(MethodInfo method, params object[] args)
    {
        var result = method.Invoke(null, args)!;
        return result switch
        {
            ValueTask<T> task => await task,
            Task<T> task => await task,
            _ => throw new InvalidOperationException($"Unexpected return type: {result.GetType().FullName}"),
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

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> Actors { get; } = [];
        public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];

        public Task<IActor?> GetAsync(string id) => Task.FromResult(Actors.GetValueOrDefault(id));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actor = new StubActor(id ?? Guid.NewGuid().ToString("N"));
            Actors[id ?? actor.Id] = actor;
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor : IActor
    {
        public StubActor(string id) => Id = id;

        public string Id { get; }
        public IAgent Agent { get; } = new StubAgent();
        public List<EventEnvelope> HandledEnvelopes { get; } = [];
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public List<EventEnvelope> Messages { get; } = [];

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            _ = ct;
            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                foreach (var message in Messages)
                    handler((TMessage)(object)message).GetAwaiter().GetResult();
            }
            return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
        }
    }

    private sealed class ThrowingSubscriptionProvider(Exception exception) : IActorEventSubscriptionProvider
    {
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            _ = handler;
            _ = ct;
            throw exception;
        }
    }

    private sealed class ThrowingActorRuntime(Exception exception) : IActorRuntime
    {
        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            throw exception;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw exception;

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            _ = id;
            _ = ct;
            throw exception;
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubPreferencesStore(string model, string route, int maxToolRounds) : INyxIdUserLlmPreferencesStore
    {
        public Task<NyxIdUserLlmPreferences> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NyxIdUserLlmPreferences(model, route, maxToolRounds));
    }

    private sealed class StubUserMemoryStore(string promptSection) : IUserMemoryStore
    {
        public Task<UserMemoryDocument> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(UserMemoryDocument.Empty);

        public Task SaveAsync(UserMemoryDocument document, CancellationToken ct = default) => Task.CompletedTask;

        public Task<UserMemoryEntry> AddEntryAsync(string category, string content, string source, CancellationToken ct = default) =>
            Task.FromResult(new UserMemoryEntry("id", category, content, source, 0, 0));

        public Task<bool> RemoveEntryAsync(string id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> BuildPromptSectionAsync(int maxChars = 2000, CancellationToken ct = default) =>
            Task.FromResult(promptSection);
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
