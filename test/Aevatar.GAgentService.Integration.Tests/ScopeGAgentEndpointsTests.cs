using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Capabilities.ExecutionActivity;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeGAgentEndpointsTests
{
    [Fact]
    public void MapScopeGAgentCapabilityEndpoints_ShouldRegisterExpectedRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        using var app = builder.Build();

        app.MapScopeGAgentCapabilityEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(r => r != null)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain(route => route.Contains("gagent-types"));
        routes.Should().NotContain(route => route.Contains("gagent-kinds"));
        routes.Should().Contain(route => route.Contains("gagent/draft-run"));
        routes.Should().Contain(route => route.Contains("gagent-actors"));
        routes.Should().Contain("/api/scopes/{scopeId}/execution-events");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectUnknownAgentKindWithJsonError()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.UnknownAgentKind))
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "tests.missing-gagent",
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("UNKNOWN_GAGENT_KIND");
    }

    [Fact]
    public async Task DraftRunHttp_ShouldRejectLegacyActorTypeNameEvenWithAgentKind()
    {
        await using var host = await ScopeGAgentEndpointHostedTestHost.StartAsync(new FakeGAgentDraftRunInteractionPort());
        using var response = await host.Client.PostAsJsonAsync(
            "/api/scopes/scope-a/gagent/draft-run",
            new
            {
                agentKind = "aevatar.role",
                actorTypeName = "Tests.RoleGAgent, Tests",
                prompt = "hello",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("LEGACY_ACTOR_TYPE_NAME_REJECTED");
    }

    [Fact]
    public async Task DeleteActorHttp_ShouldRejectLegacyGAgentTypeQuery()
    {
        await using var host = await ScopeGAgentEndpointHostedTestHost.StartAsync(new FakeGAgentDraftRunInteractionPort());
        using var response = await host.Client.DeleteAsync(
            "/api/scopes/scope-a/gagent-actors/actor-1?agentKind=aevatar.role&gagentType=Tests.RoleGAgent");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("LEGACY_GAGENT_TYPE_REJECTED");
    }

    [Fact]
    public async Task GAgentTypesRoute_ShouldReturnAgentKindCatalogOverHttp()
    {
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services = [CreateServiceCatalogSnapshot("orders")],
        };
        var revisionReader = new FakeServiceRevisionCatalogQueryReader();
        revisionReader.Revisions[ServiceKeys.Build(CreateServiceIdentity("orders"))] = new ServiceRevisionCatalogSnapshot(
            ServiceKeys.Build(CreateServiceIdentity("orders")),
            [CreateStaticRevisionSnapshot(
                "rev-1",
                "Tests.OrdersGAgent, Tests",
                "tests.orders",
                "run")],
            DateTimeOffset.UtcNow);
        await using var host = await ScopeGAgentEndpointHostedTestHost.StartAsync(
            new FakeGAgentDraftRunInteractionPort(),
            catalogReader,
            revisionReader);
        using var response = await host.Client.GetAsync("/api/scopes/gagent-types");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"agentKind\":\"tests.orders\"");
        body.Should().Contain("\"diagnosticClrTypeName\":\"Tests.OrdersGAgent, Tests\"");
        body.Should().NotContain("actorTypeName");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectMismatchedAuthenticatedScope()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext(claimedScopeId: "scope-other");

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        interactionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldTimeoutWhenNoCompletionEventReceived()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = async (_, _, _, ct) =>
            {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => pending.TrySetCanceled(ct));
                await pending.Task;
                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                    new GAgentDraftRunAcceptedReceipt("actor-1", "RoleGAgent", "cmd-1", "cmd-1"),
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.Unknown, false));
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                "hello",
                preferredActorId: "existing-actor",
                timeoutMs: 1),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.ContentType.Should().StartWith("text/event-stream");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAgent draft-run timed out");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldFinishWhenInteractionEmitsCompletionFrames()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt("existing-actor", "RoleGAgent", "cmd-123", "corr-123");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                await emitAsync(new AGUIEvent
                {
                    TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent
                    {
                        MessageId = "session-1",
                    },
                }, ct);
                await emitAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = "existing-actor",
                        RunId = "cmd-123",
                    },
                }, ct);

                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                    receipt,
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.RunFinished, true));
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext("Bearer token-abc");

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                "hello",
                preferredActorId: "existing-actor",
                timeoutMs: 200),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("corr-123");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("runStarted");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task DraftRunEndpoint_ShouldStreamRunStartedBeforeTerminalFrames()
    {
        var releaseTerminalFrames = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedObserved = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt("existing-actor", "RoleGAgent", "cmd-early", "corr-early");
                if (onAcceptedAsync != null)
                {
                    await onAcceptedAsync(receipt, ct);
                    acceptedObserved.TrySetResult(null);
                }

                await releaseTerminalFrames.Task.WaitAsync(ct);

                await emitAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = "existing-actor",
                        RunId = "cmd-early",
                    },
                }, ct);

                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                    receipt,
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(
                        GAgentDraftRunCompletionStatus.RunFinished,
                        true));
            }
        };

        await using var host = await ScopeGAgentEndpointHostedTestHost.StartAsync(interactionPort);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/gagent/draft-run")
        {
            Content = JsonContent.Create(new
            {
                agentKind = "aevatar.role",
                prompt = "hello",
                preferredActorId = "existing-actor",
                timeoutMs = 2000,
            }),
        };

        using var response = await host.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be("corr-early");

        await acceptedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var firstFrame = await ReadUntilContainsAsync(reader, "\"runStarted\"", TimeSpan.FromSeconds(5));
        firstFrame.Should().Contain("\"runStarted\"");
        firstFrame.Should().NotContain("\"runFinished\"");

        releaseTerminalFrames.TrySetResult(null);

        var remainder = await reader.ReadToEndAsync();
        remainder.Should().Contain("\"runFinished\"");
    }

    [Fact]
    public async Task ExecutionEventsEndpoint_ShouldStreamStartedAndCompletedFrames_ForRequestedScope()
    {
        var streamProvider = new InMemoryStreamProvider();
        var context = CreateExecutionEventsContext("scope-a");
        var responseStream = new ObservableResponseStream();
        context.Response.Body = responseStream;
        using var cts = new CancellationTokenSource();
        var endpointTask = InvokeHandleExecutionEventsAsync(context, "scope-a", streamProvider, cts.Token);

        await streamProvider.GetStream(ExecutionActivityStreamTopics.ForScope("scope-a")).ProduceAsync(
            new ExecutionActivityEvent
            {
                ScopeId = "scope-a",
                ActorId = "actor-1",
                AgentType = "RoleGAgent",
                HandlerName = "HandleAsync",
                EventType = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                EventId = "evt-1",
                Stage = ExecutionActivityLifecycleStage.Started,
                Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var startedFrame = await responseStream.WaitUntilContainsAsync("handler.started", TimeSpan.FromSeconds(5));
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.ContentType.Should().Be("text/event-stream; charset=utf-8");
        startedFrame.Should().Contain("\"scopeId\":\"scope-a\"");
        startedFrame.Should().Contain("\"actorId\":\"actor-1\"");
        startedFrame.Should().Contain("\"handlerName\":\"HandleAsync\"");
        startedFrame.Should().Contain("\"eventId\":\"evt-1\"");

        await streamProvider.GetStream(ExecutionActivityStreamTopics.ForScope("scope-a")).ProduceAsync(
            new ExecutionActivityEvent
            {
                ScopeId = "scope-a",
                ActorId = "actor-1",
                AgentType = "RoleGAgent",
                HandlerName = "HandleAsync",
                EventType = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                EventId = "evt-1",
                Stage = ExecutionActivityLifecycleStage.Completed,
                Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(12)),
                Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var completedFrame = await responseStream.WaitUntilContainsAsync("handler.completed", TimeSpan.FromSeconds(5));
        completedFrame.Should().Contain("\"durationMs\":12");

        await cts.CancelAsync();
        await endpointTask;
    }

    [Fact]
    public async Task ExecutionEventsEndpoint_ShouldNotLeakEventsAcrossScopes()
    {
        var streamProvider = new InMemoryStreamProvider();
        var context = CreateExecutionEventsContext("scope-a");
        var responseStream = new ObservableResponseStream();
        context.Response.Body = responseStream;
        using var cts = new CancellationTokenSource();
        var endpointTask = InvokeHandleExecutionEventsAsync(context, "scope-a", streamProvider, cts.Token);

        await streamProvider.GetStream(ExecutionActivityStreamTopics.ForScope("scope-b")).ProduceAsync(
            new ExecutionActivityEvent
            {
                ScopeId = "scope-b",
                ActorId = "actor-b",
                AgentType = "RoleGAgent",
                HandlerName = "OtherHandler",
                EventType = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                EventId = "evt-b",
                Stage = ExecutionActivityLifecycleStage.Started,
                Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        await streamProvider.GetStream(ExecutionActivityStreamTopics.ForScope("scope-a")).ProduceAsync(
            new ExecutionActivityEvent
            {
                ScopeId = "scope-a",
                ActorId = "actor-a",
                AgentType = "RoleGAgent",
                HandlerName = "ScopeAHandler",
                EventType = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                EventId = "evt-a",
                Stage = ExecutionActivityLifecycleStage.Started,
                Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var frame = await responseStream.WaitUntilContainsAsync("handler.started", TimeSpan.FromSeconds(5));
        frame.Should().Contain("\"scopeId\":\"scope-a\"");
        frame.Should().Contain("\"actorId\":\"actor-a\"");
        frame.Should().NotContain("scope-b");
        frame.Should().NotContain("actor-b");

        await cts.CancelAsync();
        await endpointTask;
    }

    [Fact]
    public async Task ExecutionEventsEndpoint_ShouldStreamFailedFrames()
    {
        var streamProvider = new InMemoryStreamProvider();
        var context = CreateExecutionEventsContext("scope-a");
        var responseStream = new ObservableResponseStream();
        context.Response.Body = responseStream;
        using var cts = new CancellationTokenSource();
        var endpointTask = InvokeHandleExecutionEventsAsync(context, "scope-a", streamProvider, cts.Token);

        await streamProvider.GetStream(ExecutionActivityStreamTopics.ForScope("scope-a")).ProduceAsync(
            new ExecutionActivityEvent
            {
                ScopeId = "scope-a",
                ActorId = "actor-1",
                AgentType = "RoleGAgent",
                HandlerName = "FailingHandler",
                EventType = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                EventId = "evt-fail",
                Stage = ExecutionActivityLifecycleStage.Failed,
                Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(7)),
                Error = "boom",
                Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var failedFrame = await responseStream.WaitUntilContainsAsync("handler.failed", TimeSpan.FromSeconds(5));
        failedFrame.Should().Contain("\"error\":\"boom\"");
        failedFrame.Should().Contain("\"durationMs\":7");

        await cts.CancelAsync();
        await endpointTask;
    }

    [Fact]
    public async Task ExecutionActivityPublisherHook_ShouldNotBlockHandler_WhenStreamPublishBackpressures()
    {
        var blockingStreamProvider = new BlockingStreamProvider();
        var hook = new ExecutionActivityPublisherHook(
            blockingStreamProvider,
            new ExecutionActivityScopeResolver(),
            LoggerFactory.Create(_ => { }).CreateLogger<ExecutionActivityPublisherHook>());
        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new Aevatar.AI.Abstractions.ChatRequestEvent { ScopeId = "scope-a" }),
        };
        var ctx = new GAgentExecutionHookContext
        {
            AgentId = "actor-1",
            AgentType = "RoleGAgent",
            EventId = "evt-1",
            EventType = envelope.Payload.TypeUrl,
            HandlerName = "HandleAsync",
        };
        ctx.Items[GAgentExecutionHookItemKeys.InboundEnvelope] = envelope;

        var publishTask = hook.OnEventHandlerStartAsync(ctx, CancellationToken.None);

        await publishTask.WaitAsync(TimeSpan.FromSeconds(2));
        await blockingStreamProvider.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));

        publishTask.IsCompletedSuccessfully.Should().BeTrue();
        blockingStreamProvider.Attempts.Should().Be(1);
        blockingStreamProvider.ReleasePublish.TrySetResult(null);
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectBlankActorTypeAndPrompt()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort();
        var logger = LoggerFactory.Create(_ => { });

        var missingTypeContext = CreateDraftRunContext();
        await InvokeHandleDraftRunAsync(
            missingTypeContext,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(" ", "hello"),
            interactionPort,
            logger,
            CancellationToken.None);
        missingTypeContext.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var missingPromptContext = CreateDraftRunContext();
        await InvokeHandleDraftRunAsync(
            missingPromptContext,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                " "),
            interactionPort,
            logger,
            CancellationToken.None);
        missingPromptContext.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldWriteAuthRequiredErrorWhenInteractionThrowsAfterAccepted()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = async (_, _, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt("auth-actor", "RoleGAgent", "cmd-auth", "corr-auth");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                throw new NyxIdAuthenticationRequiredException("sign in");
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                "hello",
                preferredActorId: "auth-actor",
                timeoutMs: 50),
            interactionPort,
            logger,
            CancellationToken.None);

        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("authentication_required");
        body.Should().Contain("NyxID authentication required");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldFail_WhenInteractionPortThrowsBeforeResponseStarts()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            Exception = new InvalidOperationException("persist failed")
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "aevatar.role",
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_DRAFT_RUN_FAILED");
        body.Should().Contain("persist failed");
        body.Should().NotContain("runStarted");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldReturnConflict_WhenInteractionReportsActorKindMismatch()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ActorKindMismatch))
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "tests.fake-agent",
                "hello",
                preferredActorId: "existing-actor"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_ACTOR_KIND_MISMATCH");
        body.Should().Contain("existing-actor");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldReturnConflict_WhenInteractionPortReportsActorKindMismatch()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) =>
            {
                return Task.FromResult(
                    CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                        GAgentDraftRunStartError.ActorKindMismatch));
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "tests.fake-agent",
                "hello",
                preferredActorId: "existing-actor"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_ACTOR_KIND_MISMATCH");
        body.Should().Contain("existing-actor");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldReturnServiceUnavailable_WhenInteractionReportsProjectionUnavailable()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ProjectionUnavailable))
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "tests.fake-agent",
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        context.Response.ContentType.Should().Be("application/json");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_PROJECTION_UNAVAILABLE");
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldDelegateNormalizedRequestToInteractionPort()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt(
                    "generated-actor",
                    request.AgentKind,
                    "cmd-new",
                    "corr-new");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                await emitAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = receipt.ActorId,
                        RunId = receipt.CommandId,
                    },
                }, ct);

                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                    receipt,
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.RunFinished, true));
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();
        var agentKind = "tests.fake-agent";

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                agentKind,
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        interactionPort.Requests.Should().ContainSingle();
        interactionPort.Requests[0].AgentKind.Should().Be(agentKind);
        interactionPort.Requests[0].ScopeId.Should().Be("scope-a");
        interactionPort.Requests[0].Prompt.Should().Be("hello");
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleDraftRunAsync_WithoutTypedSelection_ShouldIgnoreCompatibilityRoute(
        bool useUnspecifiedSelection)
    {
        const string prefixedModel = "chrono-llm/gpt-5.5";
        var selection = useUnspecifiedSelection
            ? new LLMSelection
            {
                RouteKind = LLMRouteKind.Unspecified,
                RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                NyxIdUserServiceId = "us-ignored",
                ServiceSlugSnapshot = "ignored",
                ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.Unspecified },
            }
            : null;
        var interactionPort = new FakeGAgentDraftRunInteractionPort();
        var context = CreateDraftRunContext(
            userConfigQueryPort: new StubUserConfigStore(new UserConfig(
                DefaultModel: prefixedModel,
                PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                LlmSelection: selection)));

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest("tests.fake-agent", "hello"),
            interactionPort,
            LoggerFactory.Create(_ => { }),
            CancellationToken.None);

        var request = interactionPort.Requests.Should().ContainSingle().Which;
        request.ModelOverride.Should().Be(prefixedModel);
        request.PreferredLlmRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleDraftRunAsync_WithTypedGateway_ShouldUseCanonicalGateway()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort();
        var context = CreateDraftRunContext(
            userConfigQueryPort: new StubUserConfigStore(new UserConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                LlmSelection: new LLMSelection
                {
                    RouteKind = LLMRouteKind.Gateway,
                    RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                    NyxIdUserServiceId = "us-ignored",
                    ServiceSlugSnapshot = "ignored",
                    ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                })));

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest("tests.fake-agent", "hello"),
            interactionPort,
            LoggerFactory.Create(_ => { }),
            CancellationToken.None);

        interactionPort.Requests.Should().ContainSingle().Which.PreferredLlmRoute.Should()
            .Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task HandleDraftRunAsync_WithTypedService_ShouldUseExactTypedRoute()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort();
        var context = CreateDraftRunContext(
            userConfigQueryPort: new StubUserConfigStore(new UserConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                LlmSelection: new LLMSelection
                {
                    RouteKind = LLMRouteKind.NyxIdUserService,
                    RouteValue = " route-alpha ",
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "service-alpha",
                    ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                })));

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest("tests.fake-agent", "hello"),
            interactionPort,
            LoggerFactory.Create(_ => { }),
            CancellationToken.None);

        interactionPort.Requests.Should().ContainSingle().Which.PreferredLlmRoute.Should().Be("route-alpha");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldMapInteractionExceptionBeforeResponseStarts()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) =>
            {
                throw new InvalidOperationException("dispatch failed");
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();
        var agentKind = "tests.fake-agent";

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                agentKind,
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void ResolveAgentType_ShouldFindAndNotFindTypes()
    {
        ScopeGAgentActorTypeResolver.Resolve("Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core").Should().NotBeNull();
        ScopeGAgentActorTypeResolver.Resolve("Aevatar.IamNotReal, Aevatar.IamNotReal").Should().BeNull();
    }

    [Fact]
    public void ExtractBearerToken_ShouldParseBearerHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token-123";
        var actual = InvokeExtractBearerToken(context);
        actual.Should().Be("token-123");
    }

    [Fact]
    public void ExtractBearerToken_ShouldReturnNullWithoutBearerPrefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic abc";
        var actual = InvokeExtractBearerToken(context);
        actual.Should().BeNull();
    }

    [Fact]
    public void IsNyxIdAuthenticationRequired_ShouldDetectDirectInnerAndAggregate()
    {
        IsNyxIdAuthenticationRequired(new NyxIdAuthenticationRequiredException("test")).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new InvalidOperationException("bad", new NyxIdAuthenticationRequiredException("test"))).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new AggregateException([new InvalidOperationException("x"), new NyxIdAuthenticationRequiredException("test")])).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new InvalidOperationException("nope")).Should().BeFalse();
    }

    [Fact]
    public async Task HandleActorStoreEndpoints_ShouldCoverSuccessAndFailureBranches()
    {
        var agentKind = "tests.fake-agent";
        var store = new RecordingGAgentActorStore
        {
            Actors =
            [
                new GAgentActorGroup(agentKind, ["actor-1", "actor-2"])
            ]
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateScopedHttpContext("scope-a");

        var listResult = await InvokeHandleListActorsAsync(context, "scope-a", store, logger, CancellationToken.None);
        ((IStatusCodeHttpResult)listResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        var listResponse = await ExecuteResultAsync(listResult);
        using (var document = JsonDocument.Parse(listResponse.Body))
        {
            document.RootElement.GetProperty("scopeId").GetString().Should().Be("scope-a");
            document.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(23);
            DateTimeOffset.Parse(document.RootElement.GetProperty("updatedAt").GetString()!)
                .Should()
                .Be(new DateTimeOffset(2026, 4, 27, 9, 30, 0, TimeSpan.Zero));
            document.RootElement.GetProperty("groups").GetArrayLength().Should().Be(1);
        }
        store.LastRequestedScopeId.Should().Be("scope-a");

        var addResult = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(agentKind, "actor-3"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        store.AddedActors.Should().BeEmpty();

        var removeResult = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            agentKind,
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)removeResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.RemovedActors.Should().ContainSingle(x =>
            x.ScopeId == "scope-a" &&
            x.AgentKind == agentKind &&
            x.ActorId == "actor-1");

        var invalidAdd = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(" ", " "),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)invalidAdd).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var invalidRemove = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            " ",
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)invalidRemove).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var unknownTypeAdd = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest("tests.missing-agent", "actor-4"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)unknownTypeAdd).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var throwingStore = new RecordingGAgentActorStore { ThrowOnGet = new InvalidOperationException("get failed") };
        var throwList = await InvokeHandleListActorsAsync(context, "scope-a", throwingStore, logger, CancellationToken.None);
        ((IStatusCodeHttpResult)throwList).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var throwAdd = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(agentKind, "actor-1"),
            new RecordingGAgentActorStore { ThrowOnAdd = new InvalidOperationException("add failed") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwAdd).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var throwRemove = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            agentKind,
            new RecordingGAgentActorStore { ThrowOnRemove = new InvalidOperationException("remove failed") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwRemove).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var throwListUnexpected = await InvokeHandleListActorsAsync(
            context,
            "scope-a",
            new RecordingGAgentActorStore { ThrowOnGet = new Exception("boom") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwListUnexpected).StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);

        var throwAddUnexpected = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(agentKind, "actor-1"),
            new RecordingGAgentActorStore { ThrowOnAdd = new Exception("boom") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwAddUnexpected).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var throwRemoveUnexpected = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            agentKind,
            new RecordingGAgentActorStore { ThrowOnRemove = new Exception("boom") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwRemoveUnexpected).StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task HandleActorStoreEndpoints_ShouldRejectMismatchedAuthenticatedScope()
    {
        var store = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });
        var deniedContext = CreateScopedHttpContext("scope-other");

        var listResult = await InvokeHandleListActorsAsync(deniedContext, "scope-a", store, logger, CancellationToken.None);
        ((IStatusCodeHttpResult)listResult).StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

        var addResult = await InvokeHandleAddActorAsync(
            deniedContext,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest("tests.fake-agent", "actor-1"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

        var removeResult = await InvokeHandleRemoveActorAsync(
            deniedContext,
            "scope-a",
            "actor-1",
            "tests.fake-agent",
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)removeResult).StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

        store.LastRequestedScopeId.Should().BeNull();
        store.AddedActors.Should().BeEmpty();
        store.RemovedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldReadRegisteredStaticServiceRevisionFacts()
    {
        var staticActorTypeName = "Tests.RegisteredStaticGAgent, Tests.Assembly";
        var staticAgentKind = "tests.registered-static-gagent";
        var requestTypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent";
        var responseTypeUrl = "type.googleapis.com/aevatar.ai.ChatResponseEvent";
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-a"),
            ],
        };
        var revisionReader = new FakeServiceRevisionCatalogQueryReader
        {
            Revisions =
            {
                [ServiceKeys.Build(CreateServiceIdentity("svc-a"))] = new ServiceRevisionCatalogSnapshot(
                    ServiceKeys.Build(CreateServiceIdentity("svc-a")),
                    [
                        new ServiceRevisionSnapshot(
                            "rev-static",
                            ServiceImplementationKind.Static.ToString(),
                            ServiceRevisionStatus.Published.ToString(),
                            "hash-a",
                            string.Empty,
                            [
                                new ServiceEndpointSnapshot(
                                    "chat",
                                    "Chat",
                                    ServiceEndpointKind.Chat.ToString(),
                                    requestTypeUrl,
                                    responseTypeUrl,
                                    "Registered chat endpoint."),
                            ],
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            null,
                            new ServiceRevisionImplementationSnapshot(
                                Static: new ServiceRevisionStaticSnapshot(staticActorTypeName, "preferred-actor", staticAgentKind))),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var agentKind = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        agentKind.GetProperty("agentKind").GetString().Should().Be(staticAgentKind);
        agentKind.GetProperty("displayName").GetString().Should().Be("svc-a");
        agentKind.GetProperty("diagnosticClrTypeName").GetString().Should().Be(staticActorTypeName);
        agentKind.TryGetProperty("gagentType", out _).Should().BeFalse();
        agentKind.TryGetProperty("typeName", out _).Should().BeFalse();
        agentKind.TryGetProperty("fullName", out _).Should().BeFalse();
        agentKind.TryGetProperty("assemblyName", out _).Should().BeFalse();
        var endpoint = agentKind.GetProperty("endpoints").EnumerateArray().Should().ContainSingle().Subject;
        AssertEndpointContract(
            endpoint,
            endpointId: "chat",
            displayName: "Chat",
            kind: "chat",
            requestTypeUrl: requestTypeUrl,
            responseTypeUrl: responseTypeUrl,
            description: "Registered chat endpoint.");
        revisionReader.RequestedIdentities.Should().ContainSingle(identity =>
            identity.ServiceId == "svc-a" &&
            identity.TenantId == "scope-a");
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldSkipServicesWithoutRevisionCatalog()
    {
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-missing-revisions"),
            ],
        };
        var revisionReader = new FakeServiceRevisionCatalogQueryReader();

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        body.Should().Be("[]");
        revisionReader.RequestedIdentities.Should().ContainSingle(identity =>
            identity.ServiceId == "svc-missing-revisions" &&
            identity.TenantId == "scope-a");
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldIgnoreNonStaticAndBlankStaticRevisionFacts()
    {
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-filtered-revisions"),
            ],
        };
        var identity = CreateServiceIdentity("svc-filtered-revisions");
        var revisionReader = new FakeServiceRevisionCatalogQueryReader
        {
            Revisions =
            {
                [ServiceKeys.Build(identity)] = new ServiceRevisionCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        CreateWorkflowRevisionSnapshot("rev-workflow", "workflow-endpoint"),
                        CreateStaticRevisionSnapshot("rev-blank-static", " ", " ", "blank-static-endpoint"),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        body.Should().Be("[]");
        body.Should().NotContain("workflow-endpoint");
        body.Should().NotContain("blank-static-endpoint");
        revisionReader.RequestedIdentities.Should().ContainSingle(identity =>
            identity.ServiceId == "svc-filtered-revisions" &&
            identity.TenantId == "scope-a");
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldMapBlankDisplayNameAndCustomEndpointKind()
    {
        var staticActorTypeName = "Tests.CustomEndpointGAgent, Tests.Assembly";
        var staticAgentKind = "tests.custom-endpoint-gagent";
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-custom-endpoint"),
            ],
        };
        var identity = CreateServiceIdentity("svc-custom-endpoint");
        var requestTypeUrl = "type.googleapis.com/tests.CustomRequest";
        var responseTypeUrl = "type.googleapis.com/tests.CustomResponse";
        var revisionReader = new FakeServiceRevisionCatalogQueryReader
        {
            Revisions =
            {
                [ServiceKeys.Build(identity)] = new ServiceRevisionCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        CreateStaticRevisionSnapshot(
                            "rev-custom",
                            staticActorTypeName,
                            staticAgentKind,
                            new ServiceEndpointSnapshot(
                                "custom-action",
                                " ",
                                "  BatchCommand  ",
                                requestTypeUrl,
                                responseTypeUrl,
                                "Runs a custom action.")),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var agentKind = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        agentKind.GetProperty("agentKind").GetString().Should().Be(staticAgentKind);
        agentKind.GetProperty("displayName").GetString().Should().Be("svc-custom-endpoint");
        agentKind.GetProperty("diagnosticClrTypeName").GetString().Should().Be(staticActorTypeName);
        var endpoint = agentKind.GetProperty("endpoints").EnumerateArray().Should().ContainSingle().Subject;
        AssertEndpointContract(
            endpoint,
            endpointId: "custom-action",
            displayName: "custom-action",
            kind: "batchcommand",
            requestTypeUrl: requestTypeUrl,
            responseTypeUrl: responseTypeUrl,
            description: "Runs a custom action.");
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldMergeDuplicateStaticAgentKindEndpoints()
    {
        var staticActorTypeName = "Tests.SharedStaticGAgent, Tests.Assembly";
        var staticAgentKind = "tests.shared-static-gagent";
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-duplicate-actor-type"),
            ],
        };
        var identity = CreateServiceIdentity("svc-duplicate-actor-type");
        var revisionReader = new FakeServiceRevisionCatalogQueryReader
        {
            Revisions =
            {
                [ServiceKeys.Build(identity)] = new ServiceRevisionCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        CreateStaticRevisionSnapshot("rev-a", staticActorTypeName, staticAgentKind, "chat", "run"),
                        CreateStaticRevisionSnapshot("rev-b", "Tests.OtherDiagnosticGAgent, Tests.Assembly", staticAgentKind, "chat", "status"),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var agentKind = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        agentKind.GetProperty("agentKind").GetString().Should().Be(staticAgentKind);
        agentKind.GetProperty("diagnosticClrTypeName").GetString().Should().Be(staticActorTypeName);
        agentKind.GetProperty("endpoints")
            .EnumerateArray()
            .Select(endpoint => endpoint.GetProperty("endpointId").GetString())
            .Should()
            .BeEquivalentTo(["chat", "run", "status"]);
        agentKind.GetProperty("endpoints")
            .EnumerateArray()
            .Count(endpoint => endpoint.GetProperty("endpointId").GetString() == "chat")
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task HandleListAgentKindsAsync_ShouldNotDiscoverLoadedClrAgentClasses()
    {
        var catalogReader = new FakeServiceCatalogQueryReader();
        var revisionReader = new FakeServiceRevisionCatalogQueryReader();

        var result = await InvokeHandleListAgentKindsAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        body.Should().NotContain(nameof(FakeAgent));
        body.Should().Be("[]");
        revisionReader.RequestedIdentities.Should().BeEmpty();
    }

    [Fact]
    public void ScopeGAgentEndpointsSource_ShouldNotRetainAguiMapperWrappers()
    {
        // Refactor (iter5/cluster-010):
        //   Old: Endpoint tests locked Host-local EventEnvelope -> AGUI mapper wrappers via reflection.
        //   New: Host tests assert protocol boundaries and mapper behavior is tested at ScopeGAgentAguiEventMapper.
        var source = File.ReadAllText(GetScopeGAgentEndpointsSourcePath());

        source.Should().NotContain("TryMapEnvelopeToAguiEvent");
        source.Should().NotContain("BuildToolApprovalStruct");
        source.Should().NotContain("ScopeGAgentAguiEventMapper.TryMap");
    }

    [Fact]
    public void ScopeGAgentEndpointsSource_ShouldNotUseReflectionAsAgentKindCatalog()
    {
        var source = File.ReadAllText(GetScopeGAgentEndpointsSourcePath());

        source.Should().NotContain("AppDomain.CurrentDomain.GetAssemblies()");
        source.Should().NotContain("FindOpenGenericBaseType");
        source.Should().NotContain("DerivesFromOpenGeneric");
        source.Should().NotContain("EventHandlerAttribute");
        source.Should().NotContain("TryGetProtoTypeUrl");
        source.Should().Contain("IServiceRevisionCatalogQueryReader");
    }

    [Fact]
    public void ScopeGAgentDraftRunEndpoint_ShouldNotDependOnRuntimeOrPreparationPort()
    {
        var source = File.ReadAllText(GetScopeGAgentEndpointsSourcePath());

        source.Should().NotContain("[FromServices] IActorRuntime");
        source.Should().NotContain("IGAgentDraftRunActorPreparationPort");
        source.Should().NotContain("GAgentDraftRunPreparation");
        source.Should().Contain("IGAgentDraftRunInteractionPort");
    }

    private static string? InvokeExtractBearerToken(HttpContext context)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "ExtractBearerToken",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object[] { context });
    }

    private static bool IsNyxIdAuthenticationRequired(Exception ex)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "IsNyxIdAuthenticationRequired",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, new object[] { ex })!;
    }

    private static async Task<IResult> InvokeHandleListActorsAsync(
        HttpContext context,
        string scopeId,
        IGAgentActorRegistryQueryPort actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListActorsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            context,
            scopeId,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static async Task<IResult> InvokeHandleListAgentKindsAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListGAgentKindsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            catalogReader,
            revisionCatalogReader,
            CancellationToken.None,
        })!;
    }

    private static async Task<IResult> InvokeHandleAddActorAsync(
        HttpContext context,
        string scopeId,
        ScopeGAgentEndpoints.AddGAgentActorHttpRequest request,
        IGAgentActorRegistryCommandPort actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleAddActorAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            context,
            scopeId,
            request,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static async Task<IResult> InvokeHandleRemoveActorAsync(
        HttpContext context,
        string scopeId,
        string actorId,
        string? agentKind,
        RecordingGAgentActorStore actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleRemoveActorAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object?[]
        {
            context,
            scopeId,
            actorId,
            agentKind,
            null,
            actorStore,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static async Task InvokeHandleDraftRunAsync(
        HttpContext context,
        string scopeId,
        ScopeGAgentEndpoints.GAgentDraftRunHttpRequest request,
        IGAgentDraftRunInteractionPort interactionPort,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleDraftRunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        await (Task)method!.Invoke(
            null,
            new object[]
            {
                context,
                scopeId,
                request,
                interactionPort,
                loggerFactory,
                ct,
            })!;
    }

    private static Task InvokeHandleExecutionEventsAsync(
        HttpContext context,
        string scopeId,
        IStreamProvider streamProvider,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleExecutionEventsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (Task)method!.Invoke(
            null,
            new object[]
            {
                context,
                scopeId,
                streamProvider,
                ct,
            })!;
    }

    private static HttpContext CreateDraftRunContext(
        string? authorization = null,
        string claimedScopeId = "scope-a",
        IUserConfigQueryPort? userConfigQueryPort = null)
    {
        var context = CreateScopedHttpContext(claimedScopeId, userConfigQueryPort);
        context.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static HttpContext CreateExecutionEventsContext(string claimedScopeId)
    {
        var context = CreateScopedHttpContext(claimedScopeId);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static HttpContext CreateScopedHttpContext(
        string claimedScopeId,
        IUserConfigQueryPort? userConfigQueryPort = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (userConfigQueryPort != null)
            services.AddSingleton(userConfigQueryPort);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", claimedScopeId),
            ], "test")),
        };
    }

    private static ServiceCatalogSnapshot CreateServiceCatalogSnapshot(string serviceId)
    {
        var identity = CreateServiceIdentity(serviceId);
        return new ServiceCatalogSnapshot(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            DisplayName: serviceId,
            DefaultServingRevisionId: string.Empty,
            ActiveServingRevisionId: string.Empty,
            DeploymentId: string.Empty,
            PrimaryActorId: string.Empty,
            DeploymentStatus: string.Empty,
            Endpoints: [],
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static ServiceIdentity CreateServiceIdentity(string serviceId) =>
        new()
        {
            TenantId = "scope-a",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = serviceId,
        };

    private static ServiceRevisionSnapshot CreateStaticRevisionSnapshot(
        string revisionId,
        string actorTypeName,
        string agentKind,
        params string[] endpointIds) =>
        CreateStaticRevisionSnapshot(
            revisionId,
            actorTypeName,
            agentKind,
            endpointIds.Select(CreateEndpointSnapshot).ToArray());

    private static ServiceRevisionSnapshot CreateStaticRevisionSnapshot(
        string revisionId,
        string actorTypeName,
        string agentKind,
        params ServiceEndpointSnapshot[] endpoints) =>
        new(
            revisionId,
            ServiceImplementationKind.Static.ToString(),
            ServiceRevisionStatus.Published.ToString(),
            $"{revisionId}-hash",
            string.Empty,
            endpoints.ToList(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            new ServiceRevisionImplementationSnapshot(
                Static: new ServiceRevisionStaticSnapshot(actorTypeName, "preferred-actor", agentKind)));

    private static ServiceRevisionSnapshot CreateWorkflowRevisionSnapshot(
        string revisionId,
        params string[] endpointIds) =>
        new(
            revisionId,
            ServiceImplementationKind.Workflow.ToString(),
            ServiceRevisionStatus.Published.ToString(),
            $"{revisionId}-hash",
            string.Empty,
            endpointIds.Select(CreateEndpointSnapshot).ToList(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            new ServiceRevisionImplementationSnapshot(
                Workflow: new ServiceRevisionWorkflowSnapshot("workflow-a", "definition-actor", 1)));

    private static ServiceEndpointSnapshot CreateEndpointSnapshot(string endpointId) =>
        new(
            endpointId,
            endpointId,
            endpointId == "chat"
                ? ServiceEndpointKind.Chat.ToString()
                : ServiceEndpointKind.Command.ToString(),
            $"type.googleapis.com/tests.{endpointId}",
            string.Empty,
            $"{endpointId} endpoint.");

    private static void AssertEndpointContract(
        JsonElement endpoint,
        string endpointId,
        string displayName,
        string kind,
        string requestTypeUrl,
        string responseTypeUrl,
        string description)
    {
        endpoint.GetProperty("endpointId").GetString().Should().Be(endpointId);
        endpoint.GetProperty("displayName").GetString().Should().Be(displayName);
        endpoint.GetProperty("kind").GetString().Should().Be(kind);
        endpoint.GetProperty("requestTypeUrl").GetString().Should().Be(requestTypeUrl);
        endpoint.GetProperty("responseTypeUrl").GetString().Should().Be(responseTypeUrl);
        endpoint.GetProperty("description").GetString().Should().Be(description);
        endpoint.GetProperty("auto").GetBoolean().Should().BeFalse();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> ReadUntilContainsAsync(
        StreamReader reader,
        string expected,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new char[256];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
            read.Should().BeGreaterThan(0, $"expected stream to contain {expected}");
            builder.Append(buffer, 0, read);
            if (builder.ToString().Contains(expected, StringComparison.Ordinal))
                return builder.ToString();
        }
    }

    private sealed class ObservableResponseStream : MemoryStream
    {
        private readonly Lock _lock = new();
        private readonly List<ContentWaiter> _waiters = [];

        public Task<string> WaitUntilContainsAsync(string expected, TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expected);

            lock (_lock)
            {
                var snapshot = ReadSnapshotUnsafe();
                if (snapshot.Contains(expected, StringComparison.Ordinal))
                    return Task.FromResult(snapshot);

                var waiter = new ContentWaiter(
                    expected,
                    new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                _waiters.Add(waiter);

                var cancellation = new CancellationTokenSource(timeout);
                cancellation.Token.Register(() =>
                {
                    if (waiter.Completion.TrySetCanceled(cancellation.Token))
                    {
                        lock (_lock)
                        {
                            _waiters.Remove(waiter);
                        }
                    }
                });
                return waiter.Completion.Task;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            NotifyWaiters();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.WriteAsync(buffer, cancellationToken);
            NotifyWaiters();
            return result;
        }

        private void NotifyWaiters()
        {
            List<ContentWaiter>? completed = null;
            string snapshot;
            lock (_lock)
            {
                snapshot = ReadSnapshotUnsafe();
                foreach (var waiter in _waiters)
                {
                    if (!snapshot.Contains(waiter.Expected, StringComparison.Ordinal))
                        continue;

                    completed ??= [];
                    completed.Add(waiter);
                }

                if (completed != null)
                {
                    foreach (var waiter in completed)
                        _waiters.Remove(waiter);
                }
            }

            if (completed == null)
                return;

            foreach (var waiter in completed)
                waiter.Completion.TrySetResult(snapshot);
        }

        private string ReadSnapshotUnsafe()
        {
            return Encoding.UTF8.GetString(GetBuffer(), 0, checked((int)Length));
        }

        private sealed record ContentWaiter(
            string Expected,
            TaskCompletionSource<string> Completion);
    }

    private sealed class ScopeGAgentEndpointHostedTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeGAgentEndpointHostedTestHost(WebApplication app, HttpClient client, InMemoryStreamProvider streamProvider)
        {
            _app = app;
            Client = client;
            StreamProvider = streamProvider;
        }

        public HttpClient Client { get; }
        public InMemoryStreamProvider StreamProvider { get; }

        public static async Task<ScopeGAgentEndpointHostedTestHost> StartAsync(
            FakeGAgentDraftRunInteractionPort interactionPort,
            IServiceCatalogQueryReader? catalogReader = null,
            IServiceRevisionCatalogQueryReader? revisionCatalogReader = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<IGAgentDraftRunInteractionPort>(interactionPort);
            var streamProvider = new InMemoryStreamProvider();
            builder.Services.AddSingleton<IStreamProvider>(streamProvider);
            var actorStore = new RecordingGAgentActorStore();
            builder.Services.AddSingleton<IGAgentActorRegistryCommandPort>(actorStore);
            builder.Services.AddSingleton<IGAgentActorRegistryQueryPort>(actorStore);
            builder.Services.AddSingleton<IScopeResourceAdmissionPort>(actorStore);
            builder.Services.AddSingleton(catalogReader ?? new FakeServiceCatalogQueryReader());
            builder.Services.AddSingleton(revisionCatalogReader ?? new FakeServiceRevisionCatalogQueryReader());

            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope_id", "scope-a"),
                ], authenticationType: "Test"));
                await next();
            });
            app.UseAuthorization();
            app.MapScopeGAgentCapabilityEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ScopeGAgentEndpointHostedTestHost(app, client, streamProvider);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class BlockingStreamProvider : IStreamProvider
    {
        public int Attempts { get; private set; }
        public TaskCompletionSource<object?> FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IStream GetStream(string actorId) => new BlockingStream(this, actorId);

        private sealed class BlockingStream(BlockingStreamProvider owner, string streamId) : IStream
        {
            public string StreamId => streamId;

            public Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : Google.Protobuf.IMessage
            {
                owner.Attempts++;
                owner.FirstAttempt.TrySetResult(null);
                return owner.ReleasePublish.Task;
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : Google.Protobuf.IMessage, new() =>
                Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

            public Task UpsertRelayAsync(Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding binding, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding>>([]);
        }

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static string GetScopeGAgentEndpointsSourcePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "platform",
                "Aevatar.GAgentService.Hosting",
                "Endpoints",
                "ScopeGAgentEndpoints.cs");

            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate ScopeGAgentEndpoints.cs from test output directory.");
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class FakeGAgentDraftRunInteractionPort : IGAgentDraftRunInteractionPort
    {
        public List<GAgentDraftRunInteractionRequest> Requests { get; } = [];
        public Exception? Exception { get; init; }

        public Func<
            GAgentDraftRunInteractionRequest,
            Func<AGUIEvent, CancellationToken, ValueTask>,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>?,
            CancellationToken,
            Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>>>? ResultFactory { get; init; }

        public Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunInteractionRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Exception is not null)
                throw Exception;

            if (ResultFactory == null)
            {
                return Task.FromResult(
                    CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                        new GAgentDraftRunAcceptedReceipt("actor-default", request.AgentKind, "cmd-default", "corr-default"),
                        new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.Unknown, false)));
            }

            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class StubUserConfigStore(UserConfig config) : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(config);

        public Task<UserConfig> GetAsync(
            UserConfigResourceKey resource,
            CancellationToken ct = default) => GetAsync(ct);
    }

    private sealed class RecordingGAgentActorStore :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public List<GAgentActorGroup> Actors { get; set; } = [];
        public List<(string ScopeId, string AgentKind, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string AgentKind, string ActorId)> RemovedActors { get; } = [];
        public long SnapshotStateVersion { get; init; } = 23;
        public DateTimeOffset SnapshotUpdatedAt { get; init; } =
            new(2026, 4, 27, 9, 30, 0, TimeSpan.Zero);
        public Exception? ThrowOnGet { get; set; }
        public Exception? ThrowOnAdd { get; set; }
        public Exception? ThrowOnRemove { get; set; }
        public string? LastRequestedScopeId { get; private set; }

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet != null) throw ThrowOnGet;
            LastRequestedScopeId = scopeId;
            return Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                Actors,
                SnapshotStateVersion,
                SnapshotUpdatedAt,
                DateTimeOffset.UtcNow));
        }

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd != null)
                throw ThrowOnAdd;

            AddedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove != null)
                throw ThrowOnRemove;

            RemovedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class FakeServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        public IReadOnlyList<ServiceCatalogSnapshot> Services { get; set; } = [];

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Services.FirstOrDefault(x =>
                string.Equals(x.ServiceKey, ServiceKeys.Build(identity), StringComparison.Ordinal)));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(
            int take = 1000,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Services.Take(take).ToList());

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Services
                .Where(x =>
                    string.Equals(x.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(x.AppId, appId, StringComparison.Ordinal) &&
                    string.Equals(x.Namespace, @namespace, StringComparison.Ordinal))
                .Take(take)
                .ToList());
    }

    private sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        public Dictionary<string, ServiceRevisionCatalogSnapshot> Revisions { get; } = new(StringComparer.Ordinal);

        public List<ServiceIdentity> RequestedIdentities { get; } = [];

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            RequestedIdentities.Add(identity.Clone());
            return Task.FromResult(Revisions.GetValueOrDefault(ServiceKeys.Build(identity)));
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public string Id { get; } = "agent";
    }
}
