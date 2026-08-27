using System.Reflection;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.AGUI.Contracts;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.AspNetCore.Authorization;
using AguiTextMessageContentEvent = Aevatar.AGUI.Contracts.TextMessageContentEvent;
using AguiTextMessageEndEvent = Aevatar.AGUI.Contracts.TextMessageEndEvent;
using AguiTextMessageStartEvent = Aevatar.AGUI.Contracts.TextMessageStartEvent;
using AiTextMessageContentEvent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextMessageEndEvent = Aevatar.AI.Abstractions.TextMessageEndEvent;

namespace Aevatar.AI.Tests;

using RelayOptions = Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions;

public partial class NyxIdChatEndpointsCoverageTests
{
    private static readonly System.Type EndpointsType = typeof(NyxIdChatEndpoints);
    private const string NyxRefreshTokenMetadataKey = "nyxid.refresh_token";

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
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/state");
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve");
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:stop");
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:steer");
        routes.Should().Contain(
            "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:retry");
        routes.Should().Contain(
            "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:skip");
        routes.Should().Contain("/api/webhooks/nyxid-relay");
        routes.Should().Contain("/api/webhooks/nyxid-relay/diag");
    }

    [Fact]
    public void NyxRelayDiagRoute_ShouldNotAllowAnonymous()
    {
        var endpoint = BuildRouteEndpoint("/api/webhooks/nyxid-relay/diag");

        endpoint.Metadata.OfType<IAllowAnonymous>().Should().BeEmpty();
    }

    [Fact]
    public void AgentSseEndpointSources_ShouldNotSubscribeRawEventEnvelope()
    {
        var root = GetRepositoryRoot();
        var aguiSseWriter = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs"));
        var streamingEndpoints = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs"));

        aguiSseWriter.Should().Contain("Refactor (issue1533): Old pattern:");
        aguiSseWriter.Should().NotContain("StreamingRunner");
        aguiSseWriter.Should().NotContain("SubscribeAsync<EventEnvelope>");
        streamingEndpoints.Should().NotContain("SubscribeAsync<EventEnvelope>");
        aguiSseWriter.Should().NotContain("actor.HandleEventAsync");
        aguiSseWriter.Should().NotContain(".HandleEventAsync(");
        streamingEndpoints.Should().NotContain("actor.HandleEventAsync");
        streamingEndpoints.Should().NotContain(".HandleEventAsync(");
        streamingEndpoints.Should().NotContain("INyxIdChatSessionProjectionPort");
        streamingEndpoints.Should().NotContain("[FromServices] IActorRuntime");
        aguiSseWriter.Should().NotContain("TaskCompletionSource");
        aguiSseWriter.Should().NotContain("WaitAsync(TimeSpan.FromSeconds(120))");
        streamingEndpoints.Should().NotContain("TaskCompletionSource");
        streamingEndpoints.Should().NotContain("WaitAsync(TimeSpan.FromSeconds(120))");
    }

    [Fact]
    public void NyxRelayEndpointSource_ShouldUseIngressPortInsteadOfRuntimeDispatch()
    {
        var root = GetRepositoryRoot();
        var relayEndpoints = File.ReadAllText(Path.Combine(
            root,
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs"));

        relayEndpoints.Should().Contain("INyxIdRelayIngressPort");
        relayEndpoints.Should().NotContain("[FromServices] IActorRuntime");
        relayEndpoints.Should().NotContain("[FromServices] IActorDispatchPort");
        relayEndpoints.Should().NotContain("CreateAsync<ConversationGAgent>");
        relayEndpoints.Should().NotContain("DispatchAsync(");
    }

    [Fact]
    public async Task NyxRelayDiagRoute_ShouldProxyGatewayResponse_WhenTokenIsProvided()
    {
        var endpoint = BuildRouteEndpoint("/api/webhooks/nyxid-relay/diag");
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var capturedRequest = new TaskCompletionSource<(string? Authorization, string Body, string Path)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var listenerContext = await listener.GetContextAsync();
            using var reader = new StreamReader(listenerContext.Request.InputStream);
            var requestBody = await reader.ReadToEndAsync();
            capturedRequest.TrySetResult((
                listenerContext.Request.Headers["Authorization"],
                requestBody,
                listenerContext.Request.RawUrl ?? string.Empty));

            var responseBody = new string('x', 640);
            var buffer = Encoding.UTF8.GetBytes(responseBody);
            listenerContext.Response.StatusCode = (int)HttpStatusCode.Created;
            listenerContext.Response.ContentType = "application/json";
            await listenerContext.Response.OutputStream.WriteAsync(buffer);
            listenerContext.Response.Close();
        });

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton(new NyxIdToolOptions
                {
                    BaseUrl = "http://nyxid.internal.invalid:3001",
                    InternalApiBaseUrl = "http://nyxid.internal.invalid:3001",
                    ApiBaseUrl = prefix.TrimEnd('/'),
                })
                .BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["X-Test-Token"] = "diag-token";

        var response = await ExecuteEndpointAsync(endpoint, context);
        var captured = await capturedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.Created);
        doc.RootElement.GetProperty("statusText").GetString().Should().Be("Created");
        doc.RootElement.GetProperty("responseBody").GetString()!.Length.Should().Be(500);

        captured.Authorization.Should().Be("Bearer diag-token");
        captured.Path.Should().Be("/api/v1/llm/gateway/v1/chat/completions");
        captured.Body.Should().Contain("\"model\":\"gpt-5.4\"");
        captured.Body.Should().Contain("\"content\":\"hi\"");
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnConversationReceipt()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.TryGetProperty("actorId", out var actorId).Should().BeTrue();
        doc.RootElement.GetProperty("acceptedCommandId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("statusUrl").GetString().Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        doc.RootElement.TryGetProperty("createdAt", out _).Should().BeFalse();
        var createdActorId = actorId.GetString();
        createdActorId.Should().NotBeNullOrWhiteSpace();
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == createdActorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == createdActorId);
        await AssertSingleCreationAcceptedEventAsync(runtime, createdActorId!);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenRouteHasGAgentToolHint_ShouldCreateDefaultActor()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("existing-agent-1"),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        var createdActorId = doc.RootElement.GetProperty("actorId").GetString();
        createdActorId.Should().NotBeNullOrWhiteSpace();
        createdActorId.Should().NotBe("existing-agent-1",
            "Refactor (issue1321-first): tool_choice_hint is tool prefill, not actor addressing");
        doc.RootElement.GetProperty("acceptedCommandId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("statusUrl").GetString().Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == createdActorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == createdActorId);
        await AssertSingleCreationAcceptedEventAsync(runtime, createdActorId!);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenForwardedGAgentActorIdIsEmpty_ShouldCreateLocalActor()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction(" "),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        var actorId = doc.RootElement.GetProperty("actorId").GetString();
        doc.RootElement.GetProperty("acceptedCommandId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("statusUrl").GetString().Should().Be("/api/scopes/scope-a/nyxid-chat/conversations");
        actorId.Should().NotBeNullOrWhiteSpace();
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == actorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == actorId);
        await AssertSingleCreationAcceptedEventAsync(runtime, actorId!);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenChatRouteRejects_ShouldReturnForbiddenBeforeCreatingActor()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            new ChatRouteAction { Reject = new Reject { Reason = "blocked by policy" } },
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("chat_route_rejected");
        response.Body.Should().Contain("The chat route policy rejected this request.");
        actorStore.AddedActors.Should().BeEmpty();
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldRejectScopeMismatch_BeforeCreatingActor()
    {
        var actorStore = new StubGAgentActorStore();
        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            CreateScopeGuardedContext("scope-other"),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("SCOPE_ACCESS_DENIED");
        actorStore.AddedActors.Should().BeEmpty();
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnAcceptedAck_WhenActorRegistrationFails()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorException = new InvalidOperationException("registry unavailable"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().BeEmpty();
        actorId.Should().Be(runtime.CreateCalls.Single().Id);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_failed");
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be(actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnAcceptedAck_AndUnregister_WhenRegistrationThrowsAfterCommit()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorExceptionAfterCommit = new OperationCanceledException("cancelled during admission verification"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_failed");
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be(actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnAcceptedAck_AndRollback_WhenRegistrationIsNotAdmissionVisible()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_not_admission_visible");
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be(actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnAcceptedAck_AndNotDestroy_WhenRollbackCannotUnregister()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
            RemoveActorException = new InvalidOperationException("registry unavailable"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_not_admission_visible");
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenGAgentToolHintAndRegistrationNotAdmissionVisible_ShouldRollbackCreatedActor()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("existing-agent-1"),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorId.Should().NotBe("existing-agent-1",
            "Refactor (issue1321-first): tool_choice_hint is tool prefill, not actor addressing");
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == actorId);
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be(actorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenGAgentToolHintAndRegistrationThrows_ShouldRollbackCreatedActor()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorExceptionAfterCommit = new OperationCanceledException("cancelled during admission verification"),
        };
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("existing-agent-2"),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorId.Should().NotBe("existing-agent-2",
            "Refactor (issue1321-first): tool_choice_hint is tool prefill, not actor addressing");
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == actorId);
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be(actorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == actorId);
    }

    [Fact]
    public async Task HandleListConversationsAsync_ShouldReturnRegisteredActors()
    {
        var actorStore = new StubGAgentActorStore
        {
            GroupsToReturn =
            [
                new GAgentActorGroup(NyxIdChatServiceDefaults.GAgentKind, ["actor-1"]),
                new GAgentActorGroup("other-agent", ["actor-2"]),
            ],
        };
        var result = await InvokeResultAsync(
            "HandleListConversationsAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(response.Body);
        var conversations = doc.RootElement.GetProperty("conversations");
        doc.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(1);
        conversations.GetArrayLength().Should().Be(1);
        conversations[0].GetProperty("actorId").GetString().Should().Be("actor-1");
        conversations[0].TryGetProperty("createdAt", out _).Should().BeFalse();
        actorStore.LastRequestedScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task HandleListConversationsAsync_ShouldRejectScopeMismatch_BeforeRegistryRead()
    {
        var actorStore = new StubGAgentActorStore();

        var result = await InvokeResultAsync(
            "HandleListConversationsAsync",
            CreateScopeGuardedContext("scope-other"),
            "scope-a",
            actorStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("SCOPE_ACCESS_DENIED");
        actorStore.LastRequestedScopeId.Should().BeNull();
    }

    [Fact]
    public async Task HandleListConversationsAsync_ShouldBubbleRegistryReadFailure()
    {
        var actorStore = new StubGAgentActorStore
        {
            ListActorsException = new InvalidOperationException("registry read failed"),
        };

        var act = async () => await InvokeResultAsync(
            "HandleListConversationsAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("registry read failed");
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldReturnOk_AndRemoveActor()
    {
        var actorStore = new StubGAgentActorStore();
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == "actor-1");
        historyCommandPort.DeletedConversations.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.ConversationId == "actor-1");
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-1" &&
            target.Operation == ScopeResourceOperation.Delete);
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldRejectScopeMismatch_BeforeAdmission()
    {
        var actorStore = new StubGAgentActorStore();
        var historyCommandPort = new StubChatHistoryCommandPort();

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            CreateScopeGuardedContext("scope-other"),
            "scope-a",
            "actor-1",
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("SCOPE_ACCESS_DENIED");
        actorStore.AdmissionTargets.Should().BeEmpty();
        actorStore.RemovedActors.Should().BeEmpty();
        historyCommandPort.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldReturnNotFound_WhenConversationIsUnregistered()
    {
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-missing"] = new StubActor("actor-missing");

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-missing",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Delete);
        actorStore.RemovedActors.Should().BeEmpty();
        historyCommandPort.DeletedConversations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeResourceAdmissionStatus.Denied, StatusCodes.Status403Forbidden)]
    [InlineData(ScopeResourceAdmissionStatus.ScopeMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(ScopeResourceAdmissionStatus.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task HandleDeleteConversationAsync_ShouldRejectAdmissionNegativeStatus_BeforeDispatchingActorOrSideEffects(
        ScopeResourceAdmissionStatus admissionStatus,
        int expectedStatusCode)
    {
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = new ScopeResourceAdmissionResult(admissionStatus),
        };
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-denied"] = new StubActor("actor-denied");

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-denied",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(expectedStatusCode);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-denied" &&
            target.Operation == ScopeResourceOperation.Delete);
        runtime.Actors.GetValueOrDefault("actor-denied")
            .Should().NotBeNull();
        runtime.DeleteDispatches.Should().BeEmpty();
        actorStore.RemovedActors.Should().BeEmpty();
        historyCommandPort.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldBubbleFailure_WhenActorRemovalFails()
    {
        var actorStore = new StubGAgentActorStore
        {
            RemoveActorException = new InvalidOperationException("registry unavailable"),
        };
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");

        var act = async () => await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("registry unavailable");
        historyCommandPort.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldRestoreActorRegistration_WhenHistoryDeleteFails()
    {
        var actorStore = new StubGAgentActorStore();
        var historyCommandPort = new StubChatHistoryCommandPort
        {
            DeleteConversationException = new InvalidOperationException("history unavailable"),
        };
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");

        var act = async () => await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("history unavailable");
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == "actor-1");
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == "actor-1");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectWithoutAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer";
        var runtime = new StubActorRuntime();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            runtime,
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        interactionService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectWithoutAuthenticatedSubject()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubActorRuntime(),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        interactionService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectConflictingAuthenticatedSubjectsBeforeDispatch()
    {
        var context = CreateAuthorizedStreamContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("uid", "user-audit-alpha"),
            new Claim("sub", "user-audit-beta"),
        ], authenticationType: "test"));
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubActorRuntime(),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        interactionService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectWhenNoPromptAndNoInputParts()
    {
        var context = CreateAuthorizedStreamContext();
        var runtime = new StubActorRuntime();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(null, Type: "text"),
            runtime,
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldRejectScopeMismatch_BeforeAdmission()
    {
        var context = CreateScopeGuardedContext("scope-other");
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();
        var actorStore = new StubGAgentActorStore();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubActorRuntime(),
            actorStore,
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        actorStore.AdmissionTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldReturnNotFound_WhenConversationIsUnregistered()
    {
        var context = CreateAuthorizedStreamContext();
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-missing",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubActorRuntime(),
            actorStore,
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Stream);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldRejectWithoutAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer";
        var runtime = new StubActorRuntime();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdApprovalCommand>();

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req"),
            runtime,
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        interactionService.Commands.Should().BeEmpty();
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
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdApprovalCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldRejectScopeMismatch_BeforeAdmission()
    {
        var context = CreateScopeGuardedContext("scope-other");
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();
        var actorStore = new StubGAgentActorStore();

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req"),
            new StubActorRuntime(),
            actorStore,
            new StubNyxIdChatInteractionService<NyxIdApprovalCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        actorStore.AdmissionTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldReturnNotFound_WhenConversationIsUnregistered()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-missing",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req"),
            new StubActorRuntime(),
            actorStore,
            new StubNyxIdChatInteractionService<NyxIdApprovalCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Approve);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldDispatchChatRequest_AndWriteRunFinished()
    {
        var context = CreateAuthorizedStreamContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<INyxIdUserLlmPreferencesStore>(new StubPreferencesStore(
                "relay-model",
                "/api/v1/proxy/s/relay-provider",
                7))
            .AddSingleton<IUserMemoryPromptContextProvider>(new StubUserMemoryPromptContextProvider("remember this"))
            .BuildServiceProvider();
        context.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        context.Request.Headers.Authorization = "Bearer forwarded-access-token";
        context.Request.Headers["X-Nyx-Refresh-Token"] = "refresh-token";

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Frames =
            {
                new AGUIEvent { TextMessageStart = new AguiTextMessageStartEvent() },
                new AGUIEvent { TextMessageContent = new AguiTextMessageContentEvent { Delta = "hello" } },
                new AGUIEvent { TextMessageEnd = new AguiTextMessageEndEvent() },
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello there", Type: "text"),
            runtime,
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var chatCommand = interactionService.Commands.Should().ContainSingle().Subject;
        chatCommand.Should().BeOfType<NyxIdChatCommand>();
        var command = (NyxIdChatCommand)chatCommand;
        command.ActorId.Should().Be("actor-1");
        command.Prompt.Should().Be("hello there");
        command.ScopeId.Should().Be("scope-a");
        command.AccessToken.Should().Be("forwarded-access-token");
        command.Metadata.Should().NotBeNull();
        command.Metadata!.Should().NotContainKey(NyxRefreshTokenMetadataKey);
        command.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        command.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        command.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride);
        command.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.UserMemoryPrompt);
        command.LlmControl.Should().Be(new LLMControlContext(
            NyxIdAccessToken: "forwarded-access-token",
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: "relay-model",
            NyxIdRoutePreference: "/api/v1/proxy/s/relay-provider",
            MaxToolRoundsOverride: 7,
            UserMemoryPrompt: "remember this")
        {
            RouteTarget = new LLMRouteTarget
            {
                UserServiceId = "us-relay",
                ServiceSlugSnapshot = "relay-provider",
            },
        });
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("TEXT_MESSAGE_START");
        body.Should().Contain("hello");
        body.Should().Contain("TEXT_MESSAGE_END");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldWriteKeepAliveDuringIdleInteraction()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var bodyStream = new SignalingWriteStream("aevatar.nyxid_chat.keepalive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            var context = CreateAuthorizedStreamContext();
            context.Response.Body = bodyStream;

            var runtime = new StubActorRuntime();
            runtime.Actors["actor-1"] = new StubActor("actor-1");
            var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
            {
                BeforeEmitAsync = bodyStream.WaitForSignalAsync,
                Frames =
                {
                    new AGUIEvent { RunFinished = new RunFinishedEvent() },
                },
            };

            await InvokeTaskAsync(
                "HandleStreamMessageAsync",
                context,
                "scope-a",
                "actor-1",
                new NyxIdChatEndpoints.NyxIdChatStreamRequest(
                    "long turn",
                    SessionId: "session-keepalive",
                    Type: "text"),
                runtime,
                new StubGAgentActorStore(),
                interactionService,
                NullLoggerFactory.Instance,
                timeout.Token);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            var body = bodyStream.GetText();
            body.Should().Contain("RUN_STARTED");
            body.Should().Contain("aevatar.nyxid_chat.keepalive");
            body.Should().Contain("\"turnId\":");
            body.Should().NotContain("\"sessionId\":");
            body.Should().Contain("RUN_FINISHED");
            body.IndexOf("RUN_STARTED", StringComparison.Ordinal)
                .Should().BeLessThan(body.IndexOf("aevatar.nyxid_chat.keepalive", StringComparison.Ordinal));
            body.IndexOf("aevatar.nyxid_chat.keepalive", StringComparison.Ordinal)
                .Should().BeLessThan(body.IndexOf("RUN_FINISHED", StringComparison.Ordinal));
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
        }
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldWriteNotFoundRunError_WhenResolverReportsMissingActor()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Failure = NyxIdChatStartError.ActorNotFound,
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("NyxID chat conversation was not found.");
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldDispatchDecision_AndWriteRunFinished()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var interactionService = new StubNyxIdChatInteractionService<NyxIdApprovalCommand>
        {
            Frames =
            {
                new AGUIEvent { TextMessageStart = new AguiTextMessageStartEvent() },
                new AGUIEvent { TextMessageEnd = new AguiTextMessageEndEvent() },
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req-1", Approved: false, Reason: "deny", SessionId: "session-1"),
            runtime,
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var approvalCommand = interactionService.Commands.Should().ContainSingle().Subject;
        approvalCommand.Should().BeOfType<NyxIdApprovalCommand>();
        var command = (NyxIdApprovalCommand)approvalCommand;
        command.ActorId.Should().Be("actor-1");
        command.RequestId.Should().Be("req-1");
        command.Approved.Should().BeFalse();
        command.Reason.Should().Be("deny");
        command.TurnId.Should().StartWith("turn-").And.NotBe("session-1");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldWriteNotFoundRunError_WhenResolverReportsMissingActor()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdApprovalCommand>
        {
            Failure = NyxIdChatStartError.ActorNotFound,
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("req-1"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("NyxID chat conversation was not found.");
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
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdApprovalCommand>
            {
                Exception = new InvalidOperationException("approval subscription failed"),
            },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("The approval continuation failed. Please try again.");
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldBindDispatchEmitFinalizeAndCleanup()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { TextMessageContent = new AguiTextMessageContentEvent { Delta = "hi" } },
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();
        var emitted = new List<AGUIEvent>();

        var result = await interaction.ExecuteAsync(
            new NyxIdChatCommand(
                actor.Id,
                "scope-a",
                "hello",
                "session-1",
                "access-token",
                null,
                new Dictionary<string, string> { [" custom "] = " value " }),
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be(actor.Id);
        result.Receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        result.Receipt.CommandId.Should().NotBe("session-1");
        result.Receipt.CorrelationId.Should().Be(result.Receipt.CommandId);
        result.Receipt.TurnId.Should().Be("session-1");
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.Completed.Should().BeTrue();
        result.FinalizeResult.Completion.Should().Be(NyxIdChatCompletionStatus.Completed);
        projectionPort.AttachExistingCalls.Should().ContainSingle(x => x.ActorId == actor.Id && x.SessionId == "session-1");
        projectionPort.AttachCount.Should().Be(1);
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
        var envelope = RequireDispatchedPayload<NyxIdChatStartTurnCommand>(dispatchPort);
        envelope.Route?.Direct?.TargetActorId.Should().Be(actor.Id);
        envelope.Propagation?.CorrelationId.Should().Be(result.Receipt.CorrelationId);
        var request = envelope.Payload.Unpack<NyxIdChatStartTurnCommand>();
        request.Prompt.Should().Be("hello");
        request.TurnId.Should().Be("session-1");
        request.CommandId.Should().Be(result.Receipt.CommandId);
        request.CorrelationId.Should().Be(result.Receipt.CorrelationId);
        request.ScopeId.Should().Be("scope-a");
        request.ToolContext.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        request.ToolContext.ExternalMetadata.Should().NotContainKey("scope_id");
        request.ToolContext.ExternalMetadata["custom"].Should().Be("value");
        LLMControlContextMapper.FromPayload(request.LlmControl)
            .NyxIdAccessToken.Should().Be("access-token");
        emitted.Select(x => x.EventCase).Should().ContainInOrder(
            AGUIEvent.EventOneofCase.TextMessageContent,
            AGUIEvent.EventOneofCase.RunFinished);
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldAttachSkillRecoveryForCanonicalTrigger()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var result = await interaction.ExecuteAsync(
            new NyxIdChatCommand(
                actor.Id,
                "scope-a",
                "::Goal ship today",
                "session-1",
                "access-token",
                null,
                null),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        var request = RequireDispatchedPayload<NyxIdChatStartTurnCommand>(dispatchPort)
            .Payload.Unpack<NyxIdChatStartTurnCommand>();
        request.Prompt.Should().Be("::Goal ship today");
        var recovery = AgentToolExecutionContextMapper.FromPayload(request.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("goal");
        recovery.PrimarySkillName.Should().Be("goal");
        recovery.CommandArguments.Should().Be("ship today");
        recovery.OriginalCommand.Should().Be("::Goal ship today");
        recovery.DiscoveryRequested.Should().BeFalse();
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldAttachDiscoveryRecoveryForBareCanonicalTrigger()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var result = await interaction.ExecuteAsync(
            new NyxIdChatCommand(
                actor.Id,
                "scope-a",
                "::",
                "session-1",
                "access-token",
                null,
                null),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        var request = RequireDispatchedPayload<NyxIdChatStartTurnCommand>(dispatchPort)
            .Payload.Unpack<NyxIdChatStartTurnCommand>();
        request.Prompt.Should().Be("::");
        var recovery = AgentToolExecutionContextMapper.FromPayload(request.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeFalse();
        recovery.CommandName.Should().BeNull();
        recovery.PrimarySkillName.Should().BeNull();
        recovery.CommandArguments.Should().BeNull();
        recovery.OriginalCommand.Should().Be("::");
        recovery.DiscoveryRequested.Should().BeTrue();
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldPreserveExplicitCommandAndCorrelationIdentity()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var result = await interaction.ExecuteAsync(
            new NyxIdChatCommand(
                actor.Id,
                "scope-a",
                "hello",
                "session-1",
                "access-token",
                null,
                null,
                CommandId: "command-explicit",
                CorrelationId: "correlation-explicit"),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(new NyxIdChatAcceptedReceipt(
            actor.Id,
            "command-explicit",
            "correlation-explicit",
            "session-1",
            "scope-a"));
        projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
            x.ActorId == actor.Id &&
            x.SessionId == "session-1");
        var envelope = RequireDispatchedPayload<NyxIdChatStartTurnCommand>(dispatchPort);
        envelope.Propagation?.CorrelationId.Should().Be("correlation-explicit");
        var request = envelope.Payload.Unpack<NyxIdChatStartTurnCommand>();
        request.TurnId.Should().Be("session-1");
        request.CommandId.Should().Be("command-explicit");
        request.CorrelationId.Should().Be("correlation-explicit");
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldReturnProjectionUnavailableAndDisposeSink_WhenBinderCannotAttach()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort { ReturnNullLease = true };
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(new StubActorDispatchPort(runtime))
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var result = await interaction.ExecuteAsync(
            new NyxIdChatCommand(actor.Id, "scope-a", "hello", "session-1", "token", null, null),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatStartError.ProjectionUnavailable);
        projectionPort.AttachExistingCalls.Should().ContainSingle(x => x.ActorId == actor.Id && x.SessionId == "session-1");
        projectionPort.AttachCount.Should().Be(0);
        projectionPort.DetachCount.Should().Be(0);
        projectionPort.ReleaseCount.Should().Be(0);
    }

    [Fact]
    public async Task NyxIdChatInteraction_ShouldCleanupBoundObservation_WhenDispatchFails()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort();
        var services = AddInMemoryStreamForwardingServices(new ServiceCollection())
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(new ThrowingActorDispatchPort(runtime, new InvalidOperationException("dispatch failed")))
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var act = async () => await interaction.ExecuteAsync(
            new NyxIdChatCommand(actor.Id, "scope-a", "hello", "session-1", "token", null, null),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle(x => x.ActorId == actor.Id && x.SessionId == "session-1");
        projectionPort.AttachCount.Should().Be(1);
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
    }

    [Fact]
    public void NyxIdApprovalEnvelopeFactory_ShouldBuildTypedDecisionEnvelope()
    {
        var factory = new NyxIdApprovalCommandEnvelopeFactory();
        var envelope = factory.CreateEnvelope(
            new NyxIdApprovalCommand("actor-1", "request-1", false, "deny", "session-1"),
            new CommandContext("actor-1", "command-1", "correlation-1", new Dictionary<string, string>()));

        envelope.Route?.Direct?.TargetActorId.Should().Be("actor-1");
        envelope.Propagation?.CorrelationId.Should().Be("correlation-1");
        var decision = envelope.Payload.Unpack<ToolApprovalDecisionEvent>();
        decision.RequestId.Should().Be("request-1");
        decision.ContinuationTurnId.Should().Be("session-1");
        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be("deny");
    }

    [Fact]
    public async Task NyxIdApprovalInteraction_ShouldPreserveExplicitCommandAndCorrelationIdentity()
    {
        var actor = new StubActor("actor-1");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
            ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();

        var result = await interaction.ExecuteAsync(
            new NyxIdApprovalCommand(
                actor.Id,
                "request-1",
                true,
                "approved",
                "session-1",
                CommandId: "approval-command-explicit",
                CorrelationId: "approval-correlation-explicit"),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(new NyxIdChatAcceptedReceipt(
            actor.Id,
            "approval-command-explicit",
            "approval-correlation-explicit",
            "session-1"));
        projectionPort.AttachExistingCalls.Should().ContainSingle(x =>
            x.ActorId == actor.Id &&
            x.SessionId == "session-1");
        var envelope = RequireDispatchedPayload<ToolApprovalDecisionEvent>(dispatchPort);
        envelope.Propagation?.CorrelationId.Should().Be("approval-correlation-explicit");
        var decision = envelope.Payload.Unpack<ToolApprovalDecisionEvent>();
        decision.RequestId.Should().Be("request-1");
        decision.ContinuationTurnId.Should().Be("session-1");
    }

    [Fact]
    public async Task NyxIdFinalizeEmitter_ShouldEmitTimeoutOnlyWhenCompletionMissing()
    {
        var emitter = new NyxIdChatFinalizeEmitter();
        var emitted = new List<AGUIEvent>();

        await emitter.EmitAsync(
            new NyxIdChatAcceptedReceipt("actor-1", "command-1", "correlation-1", "session-1"),
            NyxIdChatCompletionStatus.Unknown,
            completed: false,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            });

        emitted.Should().ContainSingle();
        emitted[0].RunError.Message.Should().Be("Request timed out.");

        await emitter.EmitAsync(
            new NyxIdChatAcceptedReceipt("actor-1", "command-1", "correlation-1", "session-1"),
            NyxIdChatCompletionStatus.Completed,
            completed: true,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            });

        emitted.Should().HaveCount(1);
    }

    [Fact]
    public void AddNyxIdChat_ShouldResolveRealInteractionServices()
    {
        var runtime = new StubActorRuntime();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(new StubActorDispatchPort(runtime))
            .AddSingleton<INyxIdChatSessionProjectionPort>(new StubNyxIdChatSessionProjectionPort())
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();

        services.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>()
            .Should().NotBeNull();
        services.GetRequiredService<
            ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>()
            .Should().NotBeNull();
        services.GetRequiredService<ICommandEnvelopeFactory<NyxIdChatCommand>>()
            .Should().BeOfType<NyxIdChatCommandEnvelopeFactory>();
        services.GetRequiredService<ICommandObservationLifecycle<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>()
            .Should().BeOfType<NyxIdChatObservationLifecycle<NyxIdChatCommand>>();
        services.GetRequiredService<INyxIdRelayIngressPort>()
            .Should().BeOfType<NyxIdRelayIngressPort>();
        services.GetRequiredService<INyxIdChatControlCommandPort>()
            .Should().BeOfType<NyxIdChatControlCommandPort>();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldReturnParseError_ForInvalidJson()
    {
        var relay = CreateRelayInvocationDependencies();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{ invalid"));
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Body.Should().Contain("invalid_relay_payload");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldIgnoreEmptyTextPayload()
    {
        var relay = CreateRelayInvocationDependencies();
        var payload = """
            {
              "message_id":"msg-empty-text",
              "correlation_id":"corr-empty-text",
              "platform":"slack",
              "agent":{"api_key_id":"scope-test"},
              "conversation":{"platform_id":"room-empty","type":"group"},
              "content":{"text":"   "}
            }
            """;
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-empty-text");

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("ignored");
        response.Body.Should().Contain("empty_text");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldIgnoreInvalidCardActionPayload()
    {
        var relay = CreateRelayInvocationDependencies();
        var payload = """
            {
              "message_id":"msg-invalid-card",
              "correlation_id":"corr-invalid-card",
              "platform":"lark",
              "agent":{"api_key_id":"scope-test"},
              "conversation":{"platform_id":"oc_chat_invalid","type":"private"},
              "content":{"content_type":"card_action","text":"{ invalid"}
            }
            """;
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-invalid-card");

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("ignored");
        response.Body.Should().Contain("invalid_card_action_payload");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldIgnoreUnsupportedConversationType()
    {
        var relay = CreateRelayInvocationDependencies();
        var payload = """
            {
              "message_id":"msg-device",
              "correlation_id":"corr-device",
              "platform":"slack",
              "agent":{"api_key_id":"scope-test"},
              "conversation":{"platform_id":"device-1","type":"device"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-device");
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("ignored");
        response.Body.Should().Contain("unsupported_conversation_type");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldDispatchCardAction_ToConversationActor_ForAgentBuilderSubmit()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-card");
        var payload = """
            {
              "message_id":"msg-card-builder-1",
              "correlation_id":"corr-card-builder-1",
              "platform":"lark",
              "agent":{"api_key_id":"scope-card"},
              "conversation":{"id":"conv-card-builder-1","platform_id":"oc_chat_b","type":"private"},
              "sender":{"platform_id":"ou_user_b","display_name":"Builder User"},
              "content":{
                "content_type":"card_action",
                "text":"{\"value\":{\"agent_builder_action\":\"create_daily\"},\"form_value\":{\"github_username\":\"eanzhao\",\"schedule_time\":\"09:00\"}}"
              }
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-card-builder-1");

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("accepted");
        response.Body.Should().Contain("msg-card-builder-1");
        response.Body.Should().NotContain("unsupported_card_action");

        runtime.CreateCalls.Should().ContainSingle(call => call.Type == typeof(ConversationGAgent));
        var actor = (StubActor)runtime.Actors.Values.Single();
        actor.HandledEnvelopes.Should().ContainSingle(envelope =>
            envelope.Payload != null &&
            envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        var activity = relayInbound.Activity;
        activity.Type.Should().Be(ActivityType.CardAction);
        activity.Content.Text.Should().BeEmpty();
        var cardAction = activity.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.Arguments.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("create_daily");
        cardAction.FormFields.Should().ContainKey("github_username")
            .WhoseValue.Should().Be("eanzhao");
        cardAction.FormFields.Should().ContainKey("schedule_time")
            .WhoseValue.Should().Be("09:00");
        cardAction.ActionId.Should().Be("create_daily");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldDispatchCardAction_ToConversationActor_ForWorkflowResumeSubmit()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-card");
        var payload = """
            {
              "message_id":"msg-card-workflow-1",
              "correlation_id":"corr-card-workflow-1",
              "platform":"lark",
              "agent":{"api_key_id":"scope-card"},
              "conversation":{"id":"conv-card-workflow-1","platform_id":"oc_chat_wf","type":"private"},
              "sender":{"platform_id":"ou_user_wf","display_name":"Workflow User"},
              "content":{
                "content_type":"card_action",
                "text":"{\"value\":{\"actor_id\":\"workflow-actor-1\",\"run_id\":\"run-1\",\"step_id\":\"approval-1\",\"approved\":false},\"form_value\":{\"user_input\":\"Need stronger hook\"}}"
              }
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-card-workflow-1");

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("accepted");

        runtime.CreateCalls.Should().ContainSingle(call => call.Type == typeof(ConversationGAgent));
        var actor = (StubActor)runtime.Actors.Values.Single();
        var relayInbound = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<NyxRelayInboundActivity>();
        var activity = relayInbound.Activity;
        activity.Type.Should().Be(ActivityType.CardAction);
        var cardAction = activity.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.WorkflowResume.ActorId.Should().Be("workflow-actor-1");
        cardAction.WorkflowResume.RunId.Should().Be("run-1");
        cardAction.WorkflowResume.StepId.Should().Be("approval-1");
        cardAction.WorkflowResume.Approved.Should().BeFalse();
        cardAction.WorkflowResume.UserInput.Should().Be("Need stronger hook");
        cardAction.Arguments.Should().NotContainKeys("actor_id", "run_id", "step_id", "approved");
        cardAction.FormFields.Should().ContainKey("user_input")
            .WhoseValue.Should().Be("Need stronger hook");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenCallbackTokenMissing()
    {
        var relay = CreateRelayInvocationDependencies();
        var payload = """
            {
              "message_id":"msg-auth",
              "correlation_id":"corr-auth",
              "platform":"slack",
              "agent":{"api_key_id":"scope-test"},
              "conversation":{"platform_id":"room-auth","type":"group"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Request.Headers["X-NyxID-Message-Id"] = "msg-auth";

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectInvalidSignature()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-a");
        var payload = """
            {
              "message_id":"msg-bad-sig",
              "correlation_id":"corr-bad-sig",
              "platform":"slack",
              "agent":{"api_key_id":"scope-a"},
              "conversation":{"platform_id":"room-1","type":"group"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-bad-sig");
        context.Request.Headers["X-NyxID-Callback-Token"] = "not-a-jwt";

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldAcceptAndDispatchChatActivity_WhenRelayIsValid()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-a");
        var payload = """
            {
              "message_id":"msg-1",
              "correlation_id":"corr-1",
              "platform":"slack",
              "reply_token":"reply-token-1",
              "agent":{"api_key_id":"scope-a"},
              "conversation":{"platform_id":"room-1","type":"group"},
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
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-1");

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("accepted");
        response.Body.Should().Contain("msg-1");
        var expectedActorId = BuildScopedRelayConversationActorId("scope-a", "slack:group:room-1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);
        var actor = (StubActor)runtime.Actors[expectedActorId];
        actor.HandledEnvelopes.Should().ContainSingle(envelope =>
            envelope.Payload != null &&
            envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.ReplyToken.Should().Be("reply-token-1");
        relayInbound.CorrelationId.Should().Be("corr-1");
        relayInbound.RelayApiKeyId.Should().Be(relay.RelayApiKeyId);
        relayInbound.CallbackJti.Should().Be("corr-1");
        relayInbound.CallbackObservedAtUnixMs.Should().BeGreaterThan(0);
        relayInbound.CallbackReplayExpiresAtUnixMs.Should().BeGreaterThan(relayInbound.CallbackObservedAtUnixMs);
        var activity = relayInbound.Activity;
        activity.Id.Should().Be("msg-1");
        activity.Content.Text.Should().Be("hello");
        activity.ChannelId.Value.Should().Be("slack");
        activity.Conversation.Scope.Should().Be(ConversationScope.Group);
        activity.OutboundDelivery.ReplyMessageId.Should().Be("msg-1");
        activity.OutboundDelivery.CorrelationId.Should().Be("corr-1");
        activity.TransportExtras.NyxPlatform.Should().Be("slack");
        activity.TransportExtras.NyxUserAccessToken.Should().Be(relay.UserToken);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldDispatchLarkPrivateSummarySlashCommand_WithReplyToken()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-summary");
        var payload = """
            {
              "message_id":"msg-summary-1",
              "correlation_id":"corr-summary-1",
              "platform":"lark",
              "reply_token":"reply-token-summary-1",
              "agent":{"api_key_id":"scope-summary"},
              "conversation":{"id":"oc_private_1","type":"private"},
              "sender":{"platform_id":"ou_user_1","display_name":"Alice"},
              "content":{"type":"text","text":"/summary alice"},
              "raw_platform_data":{
                "event":{
                  "sender":{"sender_id":{"union_id":"on_union_1"}},
                  "message":{"chat_id":"oc_lark_chat_1","message_id":"om_invoice_1"}
                }
              }
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-summary-1");

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().Contain("accepted");
        var expectedActorId = BuildScopedRelayConversationActorId("scope-summary", "lark:dm:ou_user_1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);

        var actor = (StubActor)runtime.Actors[expectedActorId];
        actor.HandledEnvelopes.Should().ContainSingle(envelope =>
            envelope.Payload != null &&
            envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.ReplyToken.Should().Be("reply-token-summary-1");
        relayInbound.CorrelationId.Should().Be("corr-summary-1");
        relayInbound.RelayApiKeyId.Should().Be(relay.RelayApiKeyId);
        relayInbound.CallbackJti.Should().Be("corr-summary-1");
        relayInbound.CallbackObservedAtUnixMs.Should().BeGreaterThan(0);
        relayInbound.CallbackReplayExpiresAtUnixMs.Should().BeGreaterThan(relayInbound.CallbackObservedAtUnixMs);
        var activity = relayInbound.Activity;
        activity.Id.Should().Be("msg-summary-1");
        activity.Content.Text.Should().Be("/summary alice");
        activity.ChannelId.Value.Should().Be("lark");
        activity.Conversation.Scope.Should().Be(ConversationScope.DirectMessage);
        activity.Conversation.CanonicalKey.Should().Be("lark:dm:ou_user_1");
        activity.OutboundDelivery.ReplyMessageId.Should().Be("msg-summary-1");
        activity.OutboundDelivery.CorrelationId.Should().Be("corr-summary-1");
        activity.TransportExtras.NyxPlatform.Should().Be("lark");
        activity.TransportExtras.NyxConversationId.Should().Be("oc_private_1");
        activity.TransportExtras.NyxPlatformMessageId.Should().Be("om_invoice_1");
        activity.TransportExtras.NyxLarkUnionId.Should().Be("on_union_1");
        activity.TransportExtras.NyxLarkChatId.Should().Be("oc_lark_chat_1");
        activity.TransportExtras.NyxUserAccessToken.Should().Be(relay.UserToken);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldStashSenderNyxUserIdOnTransportExtras_ForChatRoutePolicyLookup()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-summary");
        var payload = """
            {
              "message_id":"msg-sender-nyxid",
              "correlation_id":"corr-sender-nyxid",
              "platform":"lark",
              "reply_token":"reply-token-sender-nyxid",
              "agent":{"api_key_id":"scope-summary"},
              "conversation":{"id":"oc_private_2","type":"private"},
              "sender":{"platform_id":"ou_user_2","display_name":"Bob"},
              "content":{"type":"text","text":"ping"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-sender-nyxid");

        var runtime = new StubActorRuntime();
        var userResolver = new StubNyxIdCurrentUserResolver { ResolvedUserId = "nyx-user-bob" };
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            userResolver,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var expectedActorId = BuildScopedRelayConversationActorId("scope-summary", "lark:dm:ou_user_2");
        var actor = (StubActor)runtime.Actors[expectedActorId];
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.Activity.TransportExtras.NyxSenderUserId.Should().Be(
            "nyx-user-bob",
            "the relay ingress must resolve the sender NyxID via /me so ConversationGAgent can " +
            "build a per-user owner scope without re-resolving inside the actor turn");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldLeaveSenderNyxUserIdEmpty_WhenResolverFails()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-summary");
        var payload = """
            {
              "message_id":"msg-sender-nyxid-fail",
              "correlation_id":"corr-sender-nyxid-fail",
              "platform":"lark",
              "reply_token":"reply-token-sender-nyxid-fail",
              "agent":{"api_key_id":"scope-summary"},
              "conversation":{"id":"oc_private_3","type":"private"},
              "sender":{"platform_id":"ou_user_3","display_name":"Carol"},
              "content":{"type":"text","text":"ping"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-sender-nyxid-fail");

        var runtime = new StubActorRuntime();
        var userResolver = new StubNyxIdCurrentUserResolver
        {
            OnResolve = (_, _) => throw new HttpRequestException("simulated NyxID /me failure"),
        };
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            userResolver,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(
            StatusCodes.Status202Accepted,
            "an unreliable /me must not break ingress; routing falls back to scope-only / default policies");
        var expectedActorId = BuildScopedRelayConversationActorId("scope-summary", "lark:dm:ou_user_3");
        var actor = (StubActor)runtime.Actors[expectedActorId];
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.Activity.TransportExtras.NyxSenderUserId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldResolveScopeIdFromRegistration_WhenCallbackJwtHasNoScope()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-1");
        var scopeResolver = new StubNyxIdRelayScopeResolver
        {
            ScopeId = "scope-from-registration",
        };
        var payload = """
            {
              "message_id":"msg-registration-scope",
              "correlation_id":"corr-registration-scope",
              "platform":"lark",
              "reply_token":"reply-token-registration-scope",
              "agent":{"api_key_id":"nyx-key-1"},
              "conversation":{"platform_id":"ou_user_1","type":"private"},
              "sender":{"platform_id":"ou_user_1"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<INyxIdRelayScopeResolver>(scopeResolver)
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-registration-scope", includeSubject: false);

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        scopeResolver.LastNyxAgentApiKeyId.Should().Be("nyx-key-1");
        var expectedActorId = BuildScopedRelayConversationActorId("scope-from-registration", "lark:dm:ou_user_1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenScopeResolverIsUnavailable()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-no-resolver");
        var payload = """
            {
              "message_id":"msg-no-scope-resolver",
              "correlation_id":"corr-no-scope-resolver",
              "platform":"lark",
              "reply_token":"reply-token-no-scope-resolver",
              "agent":{"api_key_id":"nyx-key-no-resolver"},
              "conversation":{"platform_id":"ou_user_1","type":"private"},
              "sender":{"platform_id":"ou_user_1"},
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
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-no-scope-resolver", includeSubject: false);

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenScopeResolverReturnsEmpty()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-empty-scope");
        var scopeResolver = new StubNyxIdRelayScopeResolver
        {
            ScopeId = " ",
        };
        var payload = """
            {
              "message_id":"msg-empty-scope",
              "correlation_id":"corr-empty-scope",
              "platform":"lark",
              "reply_token":"reply-token-empty-scope",
              "agent":{"api_key_id":"nyx-key-empty-scope"},
              "conversation":{"platform_id":"ou_user_1","type":"private"},
              "sender":{"platform_id":"ou_user_1"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<INyxIdRelayScopeResolver>(scopeResolver)
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-empty-scope", includeSubject: false);

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        scopeResolver.LastNyxAgentApiKeyId.Should().Be("nyx-key-empty-scope");
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenScopeResolverThrows()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-throwing-scope");
        var scopeResolver = new StubNyxIdRelayScopeResolver
        {
            Exception = new InvalidOperationException("registration projection unavailable"),
        };
        var payload = """
            {
              "message_id":"msg-throwing-scope",
              "correlation_id":"corr-throwing-scope",
              "platform":"lark",
              "reply_token":"reply-token-throwing-scope",
              "agent":{"api_key_id":"nyx-key-throwing-scope"},
              "conversation":{"platform_id":"ou_user_1","type":"private"},
              "sender":{"platform_id":"ou_user_1"},
              "content":{"text":"hello"}
            }
            """;
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<INyxIdRelayScopeResolver>(scopeResolver)
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-throwing-scope", includeSubject: false);

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        scopeResolver.LastNyxAgentApiKeyId.Should().Be("nyx-key-throwing-scope");
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectMismatchedRelayApiKeyId()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-a");
        var payload = """
            {
              "message_id":"msg-mismatch",
              "correlation_id":"corr-mismatch",
              "platform":"slack",
              "agent":{"api_key_id":"scope-b"},
              "conversation":{"platform_id":"room-1","type":"group"},
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
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-mismatch");

        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            new StubActorRuntime(),
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldUseConversationId_WhenPresent()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-b");
        var payload = """
            {
              "message_id":"msg-2",
              "correlation_id":"corr-2",
              "platform":"discord",
              "agent":{"api_key_id":"scope-b"},
              "conversation":{"id":"conv-1","platform_id":"room-2","type":"channel"},
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
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        AttachRelayHeaders(context, relay, payload, "msg-2");

        var runtime = new StubActorRuntime();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var expectedActorId = BuildScopedRelayConversationActorId("scope-b", "discord:channel:conv-1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);
    }

    [Fact]
    public void ExtractNyxIdAccessToken_ShouldPreferDelegationHeaderAndFallbackToBearer()
    {
        var context = new DefaultHttpContext();
        var method = EndpointsType.GetMethod("ExtractNyxIdAccessToken", BindingFlags.NonPublic | BindingFlags.Static)!;

        context.Request.Headers.Authorization = "Basic abc";
        method.Invoke(null, [context]).Should().BeNull();

        context.Request.Headers.Authorization = "Bearer my-token";
        method.Invoke(null, [context]).Should().Be("my-token");
    }

    [Fact]
    public void ResolveReplyTokenExpiresAtUnixMs_ShouldUseJwtExpiryAndFallbackTtl()
    {
        var relay = CreateRelayInvocationDependencies();
        var method = EndpointsType.GetMethod("ResolveReplyTokenExpiresAtUnixMs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var options = new RelayOptions
        {
            RelayReplyTokenRuntimeTtlSeconds = 7,
        };
        var before = DateTimeOffset.UtcNow;
        var validReplyJwt = CreateRelayJwt(
            relay.SigningKey,
            relay.Issuer,
            relay.RelayApiKeyId,
            "reply-msg",
            "lark",
            "reply-jti",
            "unused-body-hash");

        var jwtExpiry = (long)method.Invoke(null, [validReplyJwt, options])!;
        var missingFallback = (long)method.Invoke(null, [null, options])!;
        var malformedFallback = (long)method.Invoke(null, ["not-a-jwt", options])!;
        var after = DateTimeOffset.UtcNow;

        DateTimeOffset.FromUnixTimeMilliseconds(jwtExpiry)
            .Should().BeOnOrAfter(before.AddMinutes(4))
            .And.BeOnOrBefore(after.AddMinutes(6));
        DateTimeOffset.FromUnixTimeMilliseconds(missingFallback)
            .Should().BeOnOrAfter(before.AddSeconds(6))
            .And.BeOnOrBefore(after.AddSeconds(9));
        DateTimeOffset.FromUnixTimeMilliseconds(malformedFallback)
            .Should().BeOnOrAfter(before.AddSeconds(6))
            .And.BeOnOrBefore(after.AddSeconds(9));
    }

    [Fact]
    public void ClassifyError_ShouldMapKnownCodePatterns()
    {
        NyxIdRelayErrorClassifier.Classify("request failed with 403").Should().Be(
            "Sorry, I can't reach the AI service right now (403 Forbidden).");
        NyxIdRelayErrorClassifier.Classify("status=401 unauthorized").Should().Be(
            "Sorry, authentication with the AI service failed (401).");
        NyxIdRelayErrorClassifier.Classify("service rate limit reached").Should().Be(
            "Sorry, the AI service is busy right now (429). Please wait a moment and try again.");
        NyxIdRelayErrorClassifier.Classify("LLM request timeout").Should().Be(
            "Sorry, the AI service took too long to respond. Please try again.");
        NyxIdRelayErrorClassifier.Classify("model `gpt-5` not found").Should().Be(
            "Sorry, the configured AI model is not available.");
        NyxIdRelayErrorClassifier.Classify("unknown issue").Should().Be(
            "Sorry, something went wrong while generating a response.");
    }

    [Fact]
    public void BuildRelayDiagnostic_ShouldUseServerDefaultsAndTokenFlag()
    {
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

        var diag = NyxIdRelayReplies.BuildDiagnostic(metadata, configuration, "LLM request failed: timeout");

        diag.Should().Contain("Model: deepseek-chat (from user config)");
        diag.Should().Contain("Route: direct");
        diag.Should().Contain("Scope: scope-a");
        diag.Should().Contain("Token: present");
        diag.Should().Contain("timeout");
    }

    private static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
    {
        var method = EndpointsType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var normalizedArgs = NormalizeEndpointArgs(method, args);
        EnsureEndpointContextServices(normalizedArgs);
        var result = method.Invoke(null, normalizedArgs);
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
        var normalizedArgs = NormalizeEndpointArgs(method, args);
        EnsureEndpointContextServices(normalizedArgs);
        var result = method.Invoke(null, normalizedArgs)!;
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

    private static object[] NormalizeEndpointArgs(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        var normalized = args.ToList();
        var suppliedRuntime = normalized.OfType<IActorRuntime>().FirstOrDefault();
        var isLifecycleEndpoint = parameters.Any(parameter => parameter.ParameterType == typeof(NyxIdChatLifecycleFacade));

        if (!isLifecycleEndpoint && parameters.All(parameter => parameter.ParameterType != typeof(IActorRuntime)))
        {
            normalized.RemoveAll(arg => arg is IActorRuntime);
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(INyxIdRelayIngressPort)) &&
            normalized.All(arg => arg is not INyxIdRelayIngressPort))
        {
            var relayIngressIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(INyxIdRelayIngressPort));
            if (relayIngressIndex >= 0)
            {
                var runtime = suppliedRuntime ?? new StubActorRuntime();
                normalized.Insert(
                    relayIngressIndex,
                    new NyxIdRelayIngressPort(
                        runtime,
                        new StubActorDispatchPort(runtime),
                        NullLogger<NyxIdRelayIngressPort>.Instance));
            }
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(IActorDispatchPort)) &&
            normalized.All(arg => arg is not IActorDispatchPort))
        {
            var dispatchPortIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(IActorDispatchPort));
            if (dispatchPortIndex >= 0)
            {
                var actorRuntime = normalized.OfType<IActorRuntime>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Endpoint test invocation needs IActorRuntime before IActorDispatchPort can be inferred.");
                normalized.Insert(dispatchPortIndex, new StubActorDispatchPort(actorRuntime));
            }
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(INyxIdChatSessionProjectionPort)) &&
            normalized.All(arg => arg is not INyxIdChatSessionProjectionPort))
        {
            var projectionPortIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(INyxIdChatSessionProjectionPort));
            if (projectionPortIndex >= 0)
            {
                normalized.Insert(projectionPortIndex, new StubNyxIdChatSessionProjectionPort());
            }
        }

        if (parameters.Any(parameter =>
                parameter.ParameterType == typeof(ICommandInteractionService<
                    NyxIdActionContinuationCommand,
                    NyxIdChatAcceptedReceipt,
                    NyxIdChatStartError,
                    AGUIEvent,
                    NyxIdChatCompletionStatus>)) &&
            normalized.All(arg => arg is not ICommandInteractionService<
                NyxIdActionContinuationCommand,
                NyxIdChatAcceptedReceipt,
                NyxIdChatStartError,
                AGUIEvent,
                NyxIdChatCompletionStatus>))
        {
            var index = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(ICommandInteractionService<
                    NyxIdActionContinuationCommand,
                    NyxIdChatAcceptedReceipt,
                    NyxIdChatStartError,
                    AGUIEvent,
                    NyxIdChatCompletionStatus>));
            normalized.Insert(
                index,
                new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>());
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(IChatRoutePolicyQueryPort)) &&
            normalized.All(arg => arg is not IChatRoutePolicyQueryPort))
        {
            var index = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(IChatRoutePolicyQueryPort));
            normalized.Insert(index, StaticChatRoutePolicyQueryPort.ForSnapshot(
                new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(ChatRouteResolver)) &&
            normalized.All(arg => arg is not ChatRouteResolver))
        {
            var index = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(ChatRouteResolver));
            normalized.Insert(index, NewChatRouteResolver());
        }

        if (parameters.Any(parameter =>
                parameter.ParameterType == typeof(Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver)) &&
            normalized.All(arg => arg is not Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver))
        {
            var index = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver));
            normalized.Insert(index, new StubNyxIdCurrentUserResolver());
        }

        if (parameters.Any(parameter => parameter.ParameterType == typeof(NyxIdChatLifecycleFacade)))
            return NormalizeNyxIdLifecycleEndpointArgs(parameters, normalized);

        return normalized.ToArray();
    }

    private static object[] NormalizeNyxIdLifecycleEndpointArgs(
        ParameterInfo[] parameters,
        List<object> args)
    {
        var registryCommandPort = args.OfType<IGAgentActorRegistryCommandPort>().FirstOrDefault() ?? new StubGAgentActorStore();
        var routeQueryPort = args.OfType<IChatRoutePolicyQueryPort>().FirstOrDefault()
            ?? StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), []));
        var resolver = args.OfType<ChatRouteResolver>().FirstOrDefault() ?? NewChatRouteResolver();
        var admissionPort = args.OfType<IScopeResourceAdmissionPort>().FirstOrDefault()
            ?? registryCommandPort as IScopeResourceAdmissionPort
            ?? new StubGAgentActorStore();
        var historyCommandPort = args.OfType<IChatHistoryCommandPort>().FirstOrDefault() ?? new StubChatHistoryCommandPort();
        var runtime = args.OfType<IActorRuntime>().FirstOrDefault()
            ?? new StubActorRuntime(registryCommandPort, historyCommandPort);
        if (runtime is StubActorRuntime stubRuntime)
            stubRuntime.ConfigureNyxIdChatServices(registryCommandPort, historyCommandPort);
        var dispatchPort = new StubActorDispatchPort(runtime);
        var facade = new NyxIdChatLifecycleFacade(
            new DefaultCommandDispatchService<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>(
                new DefaultCommandDispatchPipeline<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>(
                    new NyxIdChatConversationCreateCommandTargetResolver(runtime, routeQueryPort, resolver, new DisabledNyxIdChatAgentProfileResolver()),
                    new DefaultCommandContextPolicy(),
                    new NyxIdChatLifecycleCommandEnvelopeFactory(),
                    new ActorCommandTargetDispatcher<NyxIdChatConversationCreateCommandTarget>(dispatchPort),
                    new NyxIdChatCreateLifecycleCommandReceiptFactory())),
            new DefaultCommandDispatchService<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>(
                new DefaultCommandDispatchPipeline<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>(
                    new NyxIdChatConversationDeleteCommandTargetResolver(runtime, admissionPort),
                    new DefaultCommandContextPolicy(),
                    new NyxIdChatLifecycleCommandEnvelopeFactory(),
                    new ActorCommandTargetDispatcher<NyxIdChatConversationDeleteCommandTarget>(dispatchPort),
                    new NyxIdChatDeleteLifecycleCommandReceiptFactory())));
        return RebuildArgs(parameters, args, facade);
    }

    private static object[] RebuildArgs(
        ParameterInfo[] parameters,
        List<object> args,
        object facade)
    {
        var used = new bool[args.Count];
        var rebuilt = new List<object>(parameters.Length);
        foreach (var parameter in parameters)
        {
            if (parameter.ParameterType.IsInstanceOfType(facade))
            {
                rebuilt.Add(facade);
                continue;
            }

            var index = -1;
            for (var i = 0; i < args.Count; i++)
            {
                if (!used[i] && parameter.ParameterType.IsInstanceOfType(args[i]))
                {
                    index = i;
                    break;
                }
            }
            if (index >= 0)
            {
                used[index] = true;
                rebuilt.Add(args[index]);
                continue;
            }

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                rebuilt.Add(CancellationToken.None);
                continue;
            }

            throw new InvalidOperationException($"Unable to normalize endpoint argument {parameter.Name}:{parameter.ParameterType.FullName}.");
        }

        return rebuilt.ToArray();
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
    };

    private static async Task AssertSingleCreationUnavailableEventAsync(
        StubActorRuntime runtime,
        string actorId,
        bool destroyActor,
        string reason)
    {
        var eventStore = runtime.EventStore
            ?? throw new InvalidOperationException("NyxId chat test runtime is missing an event store.");
        var events = await eventStore.GetEventsAsync(actorId);
        events
            .Where(stateEvent =>
            {
                if (stateEvent.EventData is null ||
                    !stateEvent.EventData.Is(NyxIdChatConversationRegistrationUnavailableEvent.Descriptor))
                    return false;

                var evt = stateEvent.EventData.Unpack<NyxIdChatConversationRegistrationUnavailableEvent>();
                return evt.ScopeId == "scope-a" &&
                       evt.ActorId == actorId &&
                       evt.DestroyActor == destroyActor &&
                       evt.Reason == reason;
            })
            .Should()
            .ContainSingle();
    }

    private static async Task AssertSingleCreationAcceptedEventAsync(
        StubActorRuntime runtime,
        string actorId)
    {
        var eventStore = runtime.EventStore
            ?? throw new InvalidOperationException("NyxId chat test runtime is missing an event store.");
        var events = await eventStore.GetEventsAsync(actorId);
        events
            .Where(stateEvent =>
            {
                if (stateEvent.EventData is null ||
                    !stateEvent.EventData.Is(NyxIdChatConversationRegistrationAcceptedEvent.Descriptor))
                    return false;

                var evt = stateEvent.EventData.Unpack<NyxIdChatConversationRegistrationAcceptedEvent>();
                return evt.ScopeId == "scope-a" &&
                       evt.ActorId == actorId &&
                       !string.IsNullOrWhiteSpace(evt.CommandId) &&
                       !string.IsNullOrWhiteSpace(evt.CorrelationId);
            })
            .Should()
            .ContainSingle();
    }

    private sealed class StubNyxIdCurrentUserResolver : Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver
    {
        public string? ResolvedUserId { get; set; }

        public Func<string, CancellationToken, Task<string?>>? OnResolve { get; set; }

        public Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default)
        {
            if (OnResolve is not null)
                return OnResolve(nyxIdAccessToken, ct);
            return Task.FromResult(ResolvedUserId);
        }
    }

    private static void EnsureEndpointContextServices(IEnumerable<object> args)
    {
        foreach (var context in args.OfType<DefaultHttpContext>())
        {
            var currentServices = context.RequestServices;
            if (currentServices?.GetService<IHostEnvironment>() is not null)
                continue;

            context.RequestServices = new FallbackServiceProvider(
                currentServices ?? EmptyServiceProvider.Instance,
                CreateScopeGuardServices(authenticationEnabled: false));
        }
    }

    private static DefaultHttpContext CreateAuthorizedStreamContext()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
        };
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static IReadOnlyList<JsonElement> ParseSseFrames(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(static frame => frame.Trim())
            .Where(static frame => frame.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.Clone())
            .ToArray();

    private static DefaultHttpContext CreateScopeGuardedContext(string claimedScopeId)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = CreateScopeGuardServices(authenticationEnabled: true),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", claimedScopeId)],
                authenticationType: "test")),
        };
        return context;
    }

    private static ServiceProvider CreateScopeGuardServices(bool authenticationEnabled) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment
            {
                EnvironmentName = authenticationEnabled ? Environments.Production : Environments.Development,
            })
            .BuildServiceProvider();

    private static ChatRouteResolver NewChatRouteResolver() =>
        new(new StaticChatRouteFallbackProvider(string.Empty));

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction GAgentToolHintAction(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Google.Protobuf.WellKnownTypes.Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId),
                    },
                },
            },
        },
    };

    private sealed class StaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public static StaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) => new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = ForwardToModelAction(modelName),
            MatchedRuleId = string.Empty,
            UsedFallback = true,
            ResolvedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private sealed class FallbackServiceProvider(
        IServiceProvider primary,
        IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            primary.GetService(serviceType) ?? fallback.GetService(serviceType);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

    private static string BuildScopedRelayConversationActorId(string scopeId, string canonicalKey)
    {
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim())))
            .ToLowerInvariant();
        return $"channel-conversation:{canonicalKey}:scope:{scopeHash}";
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private static async Task<(int StatusCode, string Body, string? Location)> ExecuteResultAsync(IResult result)
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
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync(),
            context.Response.Headers.Location.ToString());
    }

    private static RouteEndpoint BuildRouteEndpoint(string routePattern)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapNyxIdChatEndpoints();

        return routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => string.Equals(endpoint.RoutePattern.RawText, routePattern, StringComparison.Ordinal));
    }

    private static async Task<(int StatusCode, string Body)> ExecuteEndpointAsync(RouteEndpoint endpoint, DefaultHttpContext context)
    {
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static string AssertAcceptedCreateAck(
        (int StatusCode, string Body, string? Location) response,
        string scopeId)
    {
        response.Location.Should().Be($"/api/scopes/{scopeId}/nyxid-chat/conversations");
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("acceptedCommandId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("statusUrl").GetString().Should().Be($"/api/scopes/{scopeId}/nyxid-chat/conversations");
        var actorId = doc.RootElement.GetProperty("actorId").GetString();
        actorId.Should().NotBeNullOrWhiteSpace();
        return actorId!;
    }

    private static IServiceCollection AddInMemoryStreamForwardingServices(IServiceCollection services)
    {
        services.AddSingleton<InMemoryStreamForwardingRegistry>();
        services.AddSingleton<IStreamForwardingRegistry>(sp =>
            sp.GetRequiredService<InMemoryStreamForwardingRegistry>());
        services.AddSingleton<IStreamForwardingBindingAuthority>(sp =>
            sp.GetRequiredService<InMemoryStreamForwardingRegistry>());
        return services;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static RelayInvocationDependencies CreateRelayInvocationDependencies(
        string relayApiKeyId = "scope-test")
    {
        const string baseUrl = "https://nyx.example.com";
        const string userToken = "user-token-1";
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "kid-1" };
        var discoveryJson = $$"""
            {
              "issuer": "{{baseUrl}}",
              "jwks_uri": "{{baseUrl}}/jwks"
            }
            """;
        var jwksJson = JsonSerializer.Serialize(new
            {
                keys = new[] { JsonWebKeyConverter.ConvertFromSecurityKey(key) },
            });

        var options = new RelayOptions
        {
            OidcCacheTtlSeconds = 60,
            JwtClockSkewSeconds = 60,
            RequireMessageIdHeader = true,
            JwksKidMissRefreshCooldownSeconds = 0,
        };
        var validator = new NyxIdRelayAuthValidator(
            new NyxRelayTestHttpClientFactory(new HttpClient(new NyxRelayOidcDocumentHandler(discoveryJson, jwksJson))),
            new NyxIdToolOptions { BaseUrl = baseUrl },
            options,
            NullLogger<NyxIdRelayAuthValidator>.Instance);

        return new RelayInvocationDependencies(
            new NyxIdRelayTransport(),
            validator,
            options,
            key,
            baseUrl,
            relayApiKeyId,
            userToken);
    }

    private static string CreateRelayJwt(
        RsaSecurityKey key,
        string issuer,
        string relayApiKeyId,
        string messageId,
        string platform,
        string jti,
        string bodySha256,
        bool includeSubject = true)
    {
        var claims = new List<Claim>
        {
            new("api_key_id", relayApiKeyId),
            new("message_id", messageId),
            new("platform", platform),
            new("body_sha256", bodySha256),
            new(JwtRegisteredClaimNames.Jti, jti),
            new("token_type", "relay_callback"),
        };
        if (includeSubject)
            claims.Insert(0, new Claim(JwtRegisteredClaimNames.Sub, relayApiKeyId));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "channel-relay/callback",
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static void AttachRelayHeaders(
        DefaultHttpContext context,
        RelayInvocationDependencies relay,
        string body,
        string messageId,
        bool includeSubject = true)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var platform = root.GetProperty("platform").GetString() ?? string.Empty;
        var correlationId = root.GetProperty("correlation_id").GetString() ?? string.Empty;
        var callbackToken = CreateRelayJwt(
            relay.SigningKey,
            relay.Issuer,
            relay.RelayApiKeyId,
            messageId,
            platform,
            correlationId,
            ComputeBodySha256Hex(Encoding.UTF8.GetBytes(body)),
            includeSubject);
        context.Request.Headers["X-NyxID-Callback-Token"] = callbackToken;
        context.Request.Headers["X-NyxID-User-Token"] = relay.UserToken;
        context.Request.Headers["X-NyxID-Message-Id"] = messageId;
    }

    private static string ComputeBodySha256Hex(byte[] bodyBytes) =>
        Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

    private static EventEnvelope RequireDispatchedPayload<TPayload>(StubActorDispatchPort dispatchPort)
        where TPayload : IMessage, new()
    {
        var descriptor = new TPayload().Descriptor;
        var matches = dispatchPort.Dispatches
            .Where(dispatch => dispatch.Envelope.Payload?.Is(descriptor) == true)
            .Select(dispatch => dispatch.Envelope)
            .ToList();
        matches.Should().ContainSingle();
        return matches[0];
    }

    private sealed record RelayInvocationDependencies(
        NyxIdRelayTransport Transport,
        NyxIdRelayAuthValidator Validator,
        RelayOptions Options,
        RsaSecurityKey SigningKey,
        string Issuer,
        string RelayApiKeyId,
        string UserToken);

    private sealed class StubJsonHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        private ServiceProvider? _nyxIdChatServices;
        private IGAgentActorRegistryCommandPort? _registryCommandPort;
        private IChatHistoryCommandPort? _historyCommandPort;

        public StubActorRuntime(
            IGAgentActorRegistryCommandPort? registryCommandPort = null,
            IChatHistoryCommandPort? historyCommandPort = null)
        {
            if (registryCommandPort is not null || historyCommandPort is not null)
            {
                ConfigureNyxIdChatServices(
                    registryCommandPort ?? new StubGAgentActorStore(),
                    historyCommandPort ?? new StubChatHistoryCommandPort());
            }
        }

        public Dictionary<string, IActor> Actors { get; } = [];
        public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];
        public List<string> DestroyCalls { get; } = [];
        public List<EventEnvelope> DeleteDispatches { get; } = [];
        public IEventStore? EventStore => _nyxIdChatServices?.GetService<IEventStore>();

        /// <summary>
        /// Stream forwarding topology owned by this runtime; projection scope actors publish their
        /// observation relay (activation evidence) here, exactly as the local runtime's stream provider does.
        /// </summary>
        public InMemoryStreamForwardingRegistry StreamForwardingRegistry { get; } = new();

        public void ConfigureNyxIdChatServices(
            IGAgentActorRegistryCommandPort registryCommandPort,
            IChatHistoryCommandPort? historyCommandPort = null)
        {
            historyCommandPort ??= new StubChatHistoryCommandPort();
            if (ReferenceEquals(_registryCommandPort, registryCommandPort) &&
                ReferenceEquals(_historyCommandPort, historyCommandPort) &&
                _nyxIdChatServices is not null)
                return;

            _registryCommandPort = registryCommandPort;
            _historyCommandPort = historyCommandPort;
            _nyxIdChatServices?.Dispose();
            _nyxIdChatServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddSingleton<IActorRuntime>(this)
                .AddSingleton(registryCommandPort)
                .AddSingleton(historyCommandPort)
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .BuildServiceProvider();

            foreach (var (actorId, actor) in Actors.ToArray())
            {
                if (actor is StubActor)
                    Actors[actorId] = new NyxIdChatConversationTestActor(actorId, _nyxIdChatServices);
            }
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult(Actors.GetValueOrDefault(id));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            IActor actor = agentType == typeof(NyxIdChatConversationGAgent) && _nyxIdChatServices is not null
                ? new NyxIdChatConversationTestActor(actorId, _nyxIdChatServices)
                : new StubActor(actorId);
            Actors[actorId] = actor;
            CreateCalls.Add((agentType, id));
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            IActor actor = agentKind.StartsWith("projection.session-scope.", StringComparison.Ordinal)
                ? new StubProjectionScopeActor(actorId, agentKind, StreamForwardingRegistry)
                : new StubActor(actorId);
            Actors[actorId] = actor;
            return Task.FromResult(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyCalls.Add(id);
            Actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NyxIdChatConversationTestActor : IActor
    {
        private readonly NyxIdChatConversationGAgent _agent;
        private readonly StubActorRuntime _runtime;

        public NyxIdChatConversationTestActor(string id, IServiceProvider services)
        {
            Id = id;
            _runtime = (StubActorRuntime)services.GetRequiredService<IActorRuntime>();
            _agent = new NyxIdChatConversationGAgent(
                _runtime,
                new StubActorDispatchPort(_runtime),
                TimeProvider.System)
            {
                Services = services,
                EventSourcingBehaviorFactory = services.GetRequiredService<
                    IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
            };

            var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
                .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setId.Invoke(_agent, [id]);
        }

        public string Id { get; }
        public IAgent Agent => _agent;
        public List<EventEnvelope> HandledEnvelopes { get; } = [];
        public Task ActivateAsync(CancellationToken ct = default) => _agent.ActivateAsync(ct);
        public Task DeactivateAsync(CancellationToken ct = default) => _agent.DeactivateAsync(ct);

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            HandledEnvelopes.Add(envelope);
            if (envelope.Payload?.Is(NyxIdChatConversationDeleteCommand.Descriptor) == true)
                _runtime.DeleteDispatches.Add(envelope);
            await _agent.HandleEventAsync(envelope, ct);
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
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

    private sealed class StubActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    /// <summary>
    /// Fails the chat turn / approval dispatch while still delivering every other envelope
    /// (e.g. projection scope ensure/release) to the runtime's actors like <see cref="StubActorDispatchPort"/>.
    /// </summary>
    private sealed class ThrowingActorDispatchPort(IActorRuntime runtime, Exception exception) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope));
            if (envelope.Payload?.Is(NyxIdChatStartTurnCommand.Descriptor) == true ||
                envelope.Payload?.Is(ToolApprovalDecisionEvent.Descriptor) == true)
            {
                throw exception;
            }

            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
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

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubNyxIdChatInteractionService<TCommand>
        : ICommandInteractionService<TCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>
    {
        public List<TCommand> Commands { get; } = [];
        public List<AGUIEvent> Frames { get; } = [];
        public Exception? Exception { get; init; }
        public Exception? AfterBeforeEmitException { get; init; }
        public Exception? AfterEmitException { get; init; }
        public NyxIdChatStartError? Failure { get; init; }
        public Func<CancellationToken, Task>? BeforeEmitAsync { get; init; }

        public async Task<CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>> ExecuteAsync(
            TCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);

            if (Exception != null)
                throw Exception;

            if (Failure.HasValue)
            {
                return CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>
                    .Failure(Failure.Value);
            }

            var (actorId, sessionId) = ResolveReceiptParts(command);
            var receipt = new NyxIdChatAcceptedReceipt(actorId, sessionId, sessionId, sessionId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            if (BeforeEmitAsync != null)
                await BeforeEmitAsync(ct);

            if (AfterBeforeEmitException != null)
                throw AfterBeforeEmitException;

            foreach (var frame in Frames)
                await emitAsync(frame, ct);

            if (AfterEmitException != null)
                throw AfterEmitException;

            return CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>
                .Success(
                    receipt,
                    new CommandInteractionFinalizeResult<NyxIdChatCompletionStatus>(
                        NyxIdChatCompletionStatus.Completed,
                        true));
        }

        async Task<RealtimeSessionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>>
            IRealtimeSession<TCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>.ExecuteAsync(
                TCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }

        private static (string ActorId, string TurnId) ResolveReceiptParts(TCommand command) =>
            command switch
            {
                NyxIdChatCommand chat => (chat.ActorId, chat.TurnId),
                NyxIdApprovalCommand approval => (approval.ActorId, approval.TurnId),
                _ => ("actor", "session"),
            };
    }

    private sealed class SignalingWriteStream(string signalText) : MemoryStream
    {
        private readonly TaskCompletionSource _signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            Observe(buffer.Span);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            Observe(buffer.AsSpan(offset, count));
        }

        public Task WaitForSignalAsync(CancellationToken ct) => _signal.Task.WaitAsync(ct);

        public string GetText()
        {
            Position = 0;
            using var reader = new StreamReader(this, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }

        private void Observe(ReadOnlySpan<byte> buffer)
        {
            if (Encoding.UTF8.GetString(buffer).Contains(signalText, StringComparison.Ordinal))
                _signal.TrySetResult();
        }
    }

    private sealed class StubNyxIdRelayScopeResolver : INyxIdRelayScopeResolver
    {
        public string? ScopeId { get; init; }
        public Exception? Exception { get; init; }
        public string? LastNyxAgentApiKeyId { get; private set; }

        public Task<string?> ResolveScopeIdByApiKeyAsync(
            string nyxAgentApiKeyId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastNyxAgentApiKeyId = nyxAgentApiKeyId;
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(ScopeId);
        }
    }

    private sealed class StubNyxIdChatSessionProjectionPort : INyxIdChatSessionProjectionPort
    {
        private IEventSink<AGUIEvent>? _sink;
        private INyxIdChatSessionProjectionLease? _lease;

        public List<AGUIEvent> Messages { get; } = [];
        public List<(string ActorId, string SessionId)> AttachExistingCalls { get; } = [];
        public bool ProjectionEnabled => true;
        public bool ReturnNullLease { get; init; }
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public async Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
            string actorId,
            string sessionId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachExistingCalls.Add((actorId, sessionId));
            if (ReturnNullLease)
                return null;

            _lease = new StubNyxIdChatSessionProjectionLease(actorId, sessionId);
            var liveSinkLease = await AttachLiveSinkAsync(_lease, sink, ct);
            return new EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>(_lease, liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
            INyxIdChatSessionProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            AttachCount++;
            _sink = sink;
            await PublishBufferedMessagesAsync(ct);
            return null;
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
            ct.ThrowIfCancellationRequested();
            DetachCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            INyxIdChatSessionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            ReleaseCount++;
            return Task.CompletedTask;
        }

        private async Task PublishBufferedMessagesAsync(CancellationToken ct)
        {
            if (_sink == null || _lease == null)
                return;

            foreach (var message in Messages)
                await _sink.PushAsync(message, ct);
        }
    }

    private sealed record StubNyxIdChatSessionProjectionLease(string ActorId, string SessionId)
        : INyxIdChatSessionProjectionLease;

    private sealed class ThrowingNyxIdChatSessionProjectionPort(Exception exception) : INyxIdChatSessionProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
            string actorId,
            string sessionId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = sessionId;
            _ = sink;
            _ = ct;
            throw exception;
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            INyxIdChatSessionProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(null);

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            INyxIdChatSessionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubGAgentActorStore :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public IReadOnlyList<GAgentActorGroup> GroupsToReturn { get; init; } = [];
        public Exception? ListActorsException { get; init; }
        public Exception? AddActorException { get; init; }
        public Exception? AddActorExceptionAfterCommit { get; init; }
        public Exception? RemoveActorException { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;
        public ScopeResourceAdmissionResult AdmissionResult { get; init; } =
            ScopeResourceAdmissionResult.Allowed();
        public List<(string ScopeId, string AgentKind, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string AgentKind, string ActorId)> RemovedActors { get; } = [];
        public List<ScopeResourceTarget> AdmissionTargets { get; } = [];
        public string? LastRequestedScopeId { get; private set; }

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            LastRequestedScopeId = scopeId;
            if (ListActorsException is not null)
                throw ListActorsException;

            return Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                GroupsToReturn,
                1,
                DateTimeOffset.Parse("2026-04-27T09:30:00Z"),
                DateTimeOffset.UtcNow));
        }

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            if (AddActorException is not null)
                throw AddActorException;
            AddedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            if (AddActorExceptionAfterCommit is not null)
                throw AddActorExceptionAfterCommit;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            RemovedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            if (RemoveActorException is not null)
                throw RemoveActorException;
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            AdmissionTargets.Add(target);
            return Task.FromResult(AdmissionResult);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubChatHistoryCommandPort : IChatHistoryCommandPort
    {
        public List<(string ScopeId, string ConversationId)> DeletedConversations { get; } = [];
        public Exception? DeleteConversationException { get; init; }

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = conversationId;
            _ = meta;
            _ = messages;
            return Task.CompletedTask;
        }

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
        {
            if (DeleteConversationException is not null)
                throw DeleteConversationException;
            DeletedConversations.Add((scopeId, conversationId));
            return Task.FromResult(ChatHistoryDeleteResult.Accepted());
        }
    }

    private sealed class StubPreferencesStore(string model, string route, int maxToolRounds) : INyxIdUserLlmPreferencesStore
    {
        private readonly NyxIdUserLlmPreferences _preferences = new(
            new LLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = route,
                NyxIdUserServiceId = "us-relay",
                ServiceSlugSnapshot = "relay-provider",
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = model,
                },
            },
            LLMSelectionPersistenceStatus.Ready,
            maxToolRounds);

        public Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_preferences);

        public Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_preferences);
    }

    private sealed class StubUserMemoryPromptContextProvider(string promptSection)
        : IUserMemoryPromptContextProvider
    {
        public Task<string> BuildAsync(int maxChars, CancellationToken ct = default) =>
            Task.FromResult(promptSection);
    }

}
