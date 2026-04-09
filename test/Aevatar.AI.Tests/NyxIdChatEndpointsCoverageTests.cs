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
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
