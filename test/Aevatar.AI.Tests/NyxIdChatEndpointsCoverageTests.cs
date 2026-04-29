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
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.AspNetCore.Authorization;

namespace Aevatar.AI.Tests;

using RelayOptions = Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions;

public class NyxIdChatEndpointsCoverageTests
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
        routes.Should().Contain("/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve");
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
                .AddSingleton(new NyxIdToolOptions { BaseUrl = prefix.TrimEnd('/') })
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
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.TryGetProperty("actorId", out var actorId).Should().BeTrue();
        doc.RootElement.TryGetProperty("createdAt", out _).Should().BeFalse();
        var createdActorId = actorId.GetString();
        createdActorId.Should().NotBeNullOrWhiteSpace();
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == createdActorId);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatGAgent) &&
            call.Id == createdActorId);
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
    public async Task HandleCreateConversationAsync_ShouldBubbleFailure_WhenActorRegistrationFails()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorException = new InvalidOperationException("registry unavailable"),
            RemoveActorException = new InvalidOperationException("registry unregister unavailable"),
        };
        var runtime = new StubActorRuntime();

        var act = async () => await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("registry unavailable");
        actorStore.AddedActors.Should().BeEmpty();
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldUnregister_WhenRegistrationThrowsAfterCommit()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorExceptionAfterCommit = new OperationCanceledException("cancelled during admission verification"),
        };
        var runtime = new StubActorRuntime();

        var act = async () => await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == actorId);
        runtime.DestroyCalls.Should().ContainSingle(actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldRollback_WhenRegistrationIsNotAdmissionVisible()
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
        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == actorId);
        runtime.DestroyCalls.Should().ContainSingle(actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldNotDestroy_WhenRollbackCannotUnregister()
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
        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        actorStore.AddedActors.Should().ContainSingle();
        actorStore.RemovedActors.Should().ContainSingle();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListConversationsAsync_ShouldReturnRegisteredActors()
    {
        var actorStore = new StubGAgentActorStore
        {
            GroupsToReturn =
            [
                new GAgentActorGroup(NyxIdChatServiceDefaults.GAgentTypeName, ["actor-1"]),
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
        var historyStore = new StubChatHistoryStore();
        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            actorStore,
            actorStore,
            historyStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == "actor-1");
        historyStore.DeletedConversations.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.ConversationId == "actor-1");
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            target.ActorId == "actor-1" &&
            target.Operation == ScopeResourceOperation.Delete);
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldRejectScopeMismatch_BeforeAdmission()
    {
        var actorStore = new StubGAgentActorStore();
        var historyStore = new StubChatHistoryStore();

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            CreateScopeGuardedContext("scope-other"),
            "scope-a",
            "actor-1",
            actorStore,
            actorStore,
            historyStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("SCOPE_ACCESS_DENIED");
        actorStore.AdmissionTargets.Should().BeEmpty();
        actorStore.RemovedActors.Should().BeEmpty();
        historyStore.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldReturnNotFound_WhenConversationIsUnregistered()
    {
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };
        var historyStore = new StubChatHistoryStore();

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-missing",
            actorStore,
            actorStore,
            historyStore,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Delete);
        actorStore.RemovedActors.Should().BeEmpty();
        historyStore.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldBubbleFailure_WhenActorRemovalFails()
    {
        var actorStore = new StubGAgentActorStore
        {
            RemoveActorException = new InvalidOperationException("registry unavailable"),
        };
        var historyStore = new StubChatHistoryStore();

        var act = async () => await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            actorStore,
            actorStore,
            historyStore,
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("registry unavailable");
        historyStore.DeletedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldRestoreActorRegistration_WhenHistoryDeleteFails()
    {
        var actorStore = new StubGAgentActorStore();
        var historyStore = new StubChatHistoryStore
        {
            DeleteConversationException = new InvalidOperationException("history unavailable"),
        };

        var act = async () => await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            actorStore,
            actorStore,
            historyStore,
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Be("history unavailable");
        actorStore.RemovedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == "actor-1");
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            entry.ActorId == "actor-1");
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
            new StubGAgentActorStore(),
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
            new StubGAgentActorStore(),
            new StubSubscriptionProvider(),
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
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
            new StubActorRuntime(),
            actorStore,
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        actorStore.AdmissionTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldReturnNotFound_WhenConversationIsUnregistered()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-token";
        var actorStore = new StubGAgentActorStore
        {
            AdmissionResult = ScopeResourceAdmissionResult.NotFound(),
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-missing",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
            new StubActorRuntime(),
            actorStore,
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Stream);
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
            new StubGAgentActorStore(),
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
            new StubGAgentActorStore(),
            new StubSubscriptionProvider(),
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
            new StubSubscriptionProvider(),
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
            new StubSubscriptionProvider(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.GAgentType == NyxIdChatServiceDefaults.GAgentTypeName &&
            target.ActorId == "actor-missing" &&
            target.Operation == ScopeResourceOperation.Approve);
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
        context.Request.Headers["X-Nyx-Refresh-Token"] = "refresh-token";
        context.Response.Body = new MemoryStream();

        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
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
            new StubGAgentActorStore(),
            subscriptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var actor = runtime.Actors["actor-1"].Should().BeOfType<StubActor>().Subject;
        var chatRequest = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Prompt.Should().Be("hello there");
        chatRequest.ScopeId.Should().Be("scope-a");
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("valid-token");
        chatRequest.Metadata.Should().NotContainKey(NyxRefreshTokenMetadataKey);
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
            new StubGAgentActorStore(),
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
            new StubGAgentActorStore(),
            new ThrowingSubscriptionProvider(new InvalidOperationException("subscription failed")),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("The chat request failed. Please try again.");
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
            new StubGAgentActorStore(),
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
            new StubGAgentActorStore(),
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
            new StubGAgentActorStore(),
            new ThrowingSubscriptionProvider(new InvalidOperationException("approval subscription failed")),
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
                "text":"{\"value\":{\"agent_builder_action\":\"create_daily_report\"},\"form_value\":{\"github_username\":\"eanzhao\",\"schedule_time\":\"09:00\"}}"
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
            .WhoseValue.Should().Be("create_daily_report");
        cardAction.FormFields.Should().ContainKey("github_username")
            .WhoseValue.Should().Be("eanzhao");
        cardAction.FormFields.Should().ContainKey("schedule_time")
            .WhoseValue.Should().Be("09:00");
        cardAction.ActionId.Should().Be("create_daily_report");
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
        cardAction!.Arguments.Should().ContainKey("actor_id").WhoseValue.Should().Be("workflow-actor-1");
        cardAction.Arguments.Should().ContainKey("run_id").WhoseValue.Should().Be("run-1");
        cardAction.Arguments.Should().ContainKey("step_id").WhoseValue.Should().Be("approval-1");
        cardAction.Arguments.Should().ContainKey("approved").WhoseValue.Should().Be("False");
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
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-a");
        var payload = """
            {
              "message_id":"msg-1",
              "correlation_id":"corr-1",
              "platform":"slack",
              "reply_token":"reply-token-1",
              "agent":{"api_key_id":"nyx-key-a"},
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
        AttachRelayHeaders(context, relay, payload, "msg-1", scopeId: "scope-a");

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
        var expectedActorId = BuildRelayConversationActorId("nyx-key-a", "scope-a", "slack:group:room-1");
        var wrongScopeActorId = BuildRelayConversationActorId("nyx-key-a", "scope-b", "slack:group:room-1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        expectedActorId.Should().NotContain(":scope:");
        expectedActorId.Should().NotContain("slack:");
        expectedActorId.Should().NotContain("room-1");
        runtime.CreateCalls.Should().NotContain(call => call.Id == wrongScopeActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);
        var actor = (StubActor)runtime.Actors[expectedActorId];
        actor.HandledEnvelopes.Should().ContainSingle(envelope =>
            envelope.Payload != null &&
            envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.ReplyToken.Should().Be("reply-token-1");
        relayInbound.CorrelationId.Should().Be("corr-1");
        var activity = relayInbound.Activity;
        activity.Id.Should().Be("msg-1");
        activity.Content.Text.Should().Be("hello");
        activity.ChannelId.Value.Should().Be("slack");
        activity.Conversation.Scope.Should().Be(ConversationScope.Group);
        activity.OutboundDelivery.ReplyMessageId.Should().Be("msg-1");
        activity.OutboundDelivery.CorrelationId.Should().Be("corr-1");
        activity.TransportExtras.NyxPlatform.Should().Be("slack");
        activity.TransportExtras.NyxUserAccessToken.Should().Be(relay.UserToken);
        activity.TransportExtras.ValidatedScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldDispatchRelayInboundThroughDispatchPort()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-dispatch");
        var payload = """
            {
              "message_id":"msg-dispatch",
              "correlation_id":"corr-dispatch",
              "platform":"slack",
              "agent":{"api_key_id":"nyx-key-dispatch"},
              "conversation":{"platform_id":"room-dispatch","type":"group"},
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
        AttachRelayHeaders(context, relay, payload, "msg-dispatch", scopeId: "scope-dispatch");

        var runtime = new StubActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var result = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            context,
            runtime,
            dispatchPort,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        var expectedActorId = BuildRelayConversationActorId(
            "nyx-key-dispatch",
            "scope-dispatch",
            "slack:group:room-dispatch");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        dispatchPort.Dispatches.Should().ContainSingle(entry =>
            entry.ActorId == expectedActorId &&
            entry.Envelope.Payload != null &&
            entry.Envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var actor = (StubActor)runtime.Actors[expectedActorId];
        actor.HandledEnvelopes.Should().BeEmpty();

        var relayInbound = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.Activity.TransportExtras.ValidatedScopeId.Should().Be("scope-dispatch");
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldPartitionRelayActorByVerifiedScope_WhenNyxKeyIsShared()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "nyx-key-shared");
        var runtime = new StubActorRuntime();

        var firstPayload = """
            {
              "message_id":"msg-shared-a",
              "correlation_id":"corr-shared-a",
              "platform":"slack",
              "agent":{"api_key_id":"nyx-key-shared"},
              "conversation":{"platform_id":"room-shared","type":"group"},
              "content":{"text":"hello a"}
            }
            """;
        var firstContext = BuildRelayHttpContext(firstPayload);
        AttachRelayHeaders(firstContext, relay, firstPayload, "msg-shared-a", scopeId: "scope-a");

        var secondPayload = """
            {
              "message_id":"msg-shared-b",
              "correlation_id":"corr-shared-b",
              "platform":"slack",
              "agent":{"api_key_id":"nyx-key-shared"},
              "conversation":{"platform_id":"room-shared","type":"group"},
              "content":{"text":"hello b"}
            }
            """;
        var secondContext = BuildRelayHttpContext(secondPayload);
        AttachRelayHeaders(secondContext, relay, secondPayload, "msg-shared-b", scopeId: "scope-b");

        var firstResult = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            firstContext,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        var secondResult = await InvokeResultAsync(
            "HandleRelayWebhookAsync",
            secondContext,
            runtime,
            relay.Transport,
            relay.Validator,
            relay.Options,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        (await ExecuteResultAsync(firstResult)).StatusCode.Should().Be(StatusCodes.Status202Accepted);
        (await ExecuteResultAsync(secondResult)).StatusCode.Should().Be(StatusCodes.Status202Accepted);

        var firstActorId = BuildRelayConversationActorId("nyx-key-shared", "scope-a", "slack:group:room-shared");
        var secondActorId = BuildRelayConversationActorId("nyx-key-shared", "scope-b", "slack:group:room-shared");
        firstActorId.Should().NotBe(secondActorId);
        runtime.CreateCalls.Should().ContainSingle(call => call.Id == firstActorId);
        runtime.CreateCalls.Should().ContainSingle(call => call.Id == secondActorId);
        runtime.Actors.Keys.Should().Contain([firstActorId, secondActorId]);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldDispatchLarkPrivateDailySlashCommand_WithReplyToken()
    {
        var relay = CreateRelayInvocationDependencies(relayApiKeyId: "scope-daily");
        var payload = """
            {
              "message_id":"msg-daily-1",
              "correlation_id":"corr-daily-1",
              "platform":"lark",
              "reply_token":"reply-token-daily-1",
              "agent":{"api_key_id":"scope-daily"},
              "conversation":{"id":"oc_private_1","type":"private"},
              "sender":{"platform_id":"ou_user_1","display_name":"Alice"},
              "content":{"type":"text","text":"/daily alice"},
              "raw_platform_data":{
                "event":{
                  "sender":{"sender_id":{"union_id":"on_union_1"}},
                  "message":{"chat_id":"oc_lark_chat_1","message_id":"om_daily_1"}
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
        AttachRelayHeaders(context, relay, payload, "msg-daily-1");

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
        var expectedActorId = BuildRelayConversationActorId("scope-daily", "scope-daily", "lark:dm:ou_user_1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);

        var actor = (StubActor)runtime.Actors[expectedActorId];
        actor.HandledEnvelopes.Should().ContainSingle(envelope =>
            envelope.Payload != null &&
            envelope.Payload.Is(NyxRelayInboundActivity.Descriptor));
        var relayInbound = actor.HandledEnvelopes.Single().Payload.Unpack<NyxRelayInboundActivity>();
        relayInbound.ReplyToken.Should().Be("reply-token-daily-1");
        relayInbound.CorrelationId.Should().Be("corr-daily-1");
        var activity = relayInbound.Activity;
        activity.Id.Should().Be("msg-daily-1");
        activity.Content.Text.Should().Be("/daily alice");
        activity.ChannelId.Value.Should().Be("lark");
        activity.Conversation.Scope.Should().Be(ConversationScope.DirectMessage);
        activity.Conversation.CanonicalKey.Should().Be("lark:dm:ou_user_1");
        activity.OutboundDelivery.ReplyMessageId.Should().Be("msg-daily-1");
        activity.OutboundDelivery.CorrelationId.Should().Be("corr-daily-1");
        activity.TransportExtras.NyxPlatform.Should().Be("lark");
        activity.TransportExtras.NyxConversationId.Should().Be("oc_private_1");
        activity.TransportExtras.NyxPlatformMessageId.Should().Be("om_daily_1");
        activity.TransportExtras.NyxLarkUnionId.Should().Be("on_union_1");
        activity.TransportExtras.NyxLarkChatId.Should().Be("oc_lark_chat_1");
        activity.TransportExtras.NyxUserAccessToken.Should().Be(relay.UserToken);
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectAndNotUseResolver_WhenCallbackJwtHasNoScope()
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
        AttachRelayHeaders(context, relay, payload, "msg-registration-scope", includeSubject: true, includeScopeClaim: false);

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
        scopeResolver.LastNyxAgentApiKeyId.Should().BeNull();
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldRejectWhenScopeClaimIsMissing()
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
        AttachRelayHeaders(context, relay, payload, "msg-no-scope-resolver", includeSubject: false, includeScopeClaim: false);

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
    public async Task HandleRelayWebhookAsync_ShouldNotCallResolver_WhenScopeClaimIsMissing()
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
        AttachRelayHeaders(context, relay, payload, "msg-empty-scope", includeSubject: false, includeScopeClaim: false);

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
        scopeResolver.LastNyxAgentApiKeyId.Should().BeNull();
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRelayWebhookAsync_ShouldIgnoreThrowingResolver_WhenScopeClaimIsMissing()
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
        AttachRelayHeaders(context, relay, payload, "msg-throwing-scope", includeSubject: false, includeScopeClaim: false);

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
        scopeResolver.LastNyxAgentApiKeyId.Should().BeNull();
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
        var expectedActorId = BuildRelayConversationActorId("scope-b", "scope-b", "discord:channel:conv-1");
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(ConversationGAgent) &&
            call.Id == expectedActorId);
        runtime.Actors.Should().ContainKey(expectedActorId);
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
    public async Task BuildConnectedServicesContext_ShouldRenderServiceHintsAndFallbackMessage()
    {
        var arrayPayload = """
            [
              {"slug":"calendar","label":"Calendar","base_url":"https://api.example.com"}
            ]
            """;
        var arrayContext = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            arrayPayload, null, "", CancellationToken.None);
        arrayContext.Should().Contain("calendar");
        arrayContext.Should().Contain("Use nyxid_proxy");

        var emptyContext = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            """{"services":[]}""", null, "", CancellationToken.None);
        emptyContext.Should().Contain("No services connected yet");
    }

    [Fact]
    public async Task BuildConnectedServicesContext_ShouldHandleDataShape_AndInvalidJson()
    {
        var dataPayload = """
            {
              "data":[
                {"slug":"github","name":"GitHub","endpoint_url":"https://api.github.com"}
              ]
            }
            """;
        var dataContext = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            dataPayload, null, "", CancellationToken.None);
        dataContext.Should().Contain("GitHub");
        dataContext.Should().Contain("https://api.github.com");

        var invalidContext = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            "{ invalid", null, "", CancellationToken.None);
        invalidContext.Should().Contain("No services connected yet");
        invalidContext.Should().Contain("Use nyxid_proxy");
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
            body.Should().NotContain("boom");
            body.Should().Contain("something went wrong");
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
        EnsureEndpointContextServices(args);
        var method = EndpointsType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        args = NormalizeEndpointArguments(method, args);
        var result = method.Invoke(null, args);
        return result switch
        {
            Task<IResult> task => await task,
            ValueTask<IResult> valueTask => await valueTask,
            _ => throw new InvalidOperationException($"Unexpected return type: {result?.GetType().FullName}"),
        };
    }

    private static object[] NormalizeEndpointArguments(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        if (string.Equals(method.Name, "HandleRelayWebhookAsync", StringComparison.Ordinal) &&
            parameters.Length == args.Length + 1 &&
            parameters.Length > 2 &&
            parameters[2].ParameterType == typeof(IActorDispatchPort) &&
            args.Length > 1 &&
            args[1] is StubActorRuntime runtime)
        {
            return args[..2]
                .Append(new ForwardingActorDispatchPort(runtime))
                .Concat(args[2..])
                .ToArray();
        }

        return args;
    }

    private static async Task InvokeTaskAsync(string methodName, params object[] args)
    {
        EnsureEndpointContextServices(args);
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

    private static DefaultHttpContext BuildRelayHttpContext(string payload)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        return context;
    }

    private static string BuildRelayConversationActorId(
        string relayIdentity,
        string validatedScopeId,
        string canonicalKey)
    {
        var actorKey = $"{relayIdentity.Trim()}\n{validatedScopeId.Trim()}\n{canonicalKey.Trim()}";
        var relayHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actorKey)))
            .ToLowerInvariant();
        return $"channel-conversation:relay:{relayHash}";
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
            NullLogger<NyxIdRelayAuthValidator>.Instance,
            new NyxIdRelayReplayGuard());

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
        bool includeSubject = true,
        bool includeScopeClaim = true,
        string? scopeId = null)
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
        if (includeScopeClaim)
            claims.Add(new Claim("scope_id", scopeId ?? relayApiKeyId));

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
        bool includeSubject = true,
        bool includeScopeClaim = true,
        string? scopeId = null)
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
            includeSubject,
            includeScopeClaim,
            scopeId);
        context.Request.Headers["X-NyxID-Callback-Token"] = callbackToken;
        context.Request.Headers["X-NyxID-User-Token"] = relay.UserToken;
        context.Request.Headers["X-NyxID-Message-Id"] = messageId;
    }

    private static string ComputeBodySha256Hex(byte[] bodyBytes) =>
        Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

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
            public Dictionary<string, IActor> Actors { get; } = [];
            public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];
            public List<string> DestroyCalls { get; } = [];

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

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class ForwardingActorDispatchPort(StubActorRuntime runtime) : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (runtime.Actors.TryGetValue(actorId, out var actor))
                return actor.HandleEventAsync(envelope, ct);

            return Task.CompletedTask;
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
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];
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
            AddedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
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
            RemovedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
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

    private sealed class StubChatHistoryStore : IChatHistoryStore
    {
        public ChatHistoryIndex IndexToReturn { get; init; } = new([]);
        public List<(string ScopeId, string ConversationId, ConversationMeta Meta)> SavedConversations { get; } = [];
        public List<(string ScopeId, string ConversationId)> DeletedConversations { get; } = [];
        public Exception? SaveMessagesException { get; init; }
        public Exception? DeleteConversationException { get; init; }

        public Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
        {
            _ = scopeId;
            return Task.FromResult(IndexToReturn);
        }

        public Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = conversationId;
            return Task.FromResult<IReadOnlyList<StoredChatMessage>>([]);
        }

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            _ = messages;
            if (SaveMessagesException is not null)
                throw SaveMessagesException;
            SavedConversations.Add((scopeId, conversationId, meta));
            return Task.CompletedTask;
        }

        public Task DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
        {
            if (DeleteConversationException is not null)
                throw DeleteConversationException;
            DeletedConversations.Add((scopeId, conversationId));
            return Task.CompletedTask;
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
