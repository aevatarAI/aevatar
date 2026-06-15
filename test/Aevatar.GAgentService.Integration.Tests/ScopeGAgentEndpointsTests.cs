using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
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
        routes.Should().Contain(route => route.Contains("gagent/draft-run"));
        routes.Should().Contain(route => route.Contains("gagent-actors"));
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectUnknownActorTypeWithJsonError()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.UnknownActorType))
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.IamNotReal, Aevatar.IamNotReal",
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("UNKNOWN_GAGENT_TYPE");
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: "existing-actor",
                TimeoutMs: 1),
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: "existing-actor",
                TimeoutMs: 200),
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
                actorTypeName = "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: "auth-actor",
                TimeoutMs: 50),
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
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
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
    public async Task HandleDraftRunAsync_ShouldReturnConflict_WhenInteractionReportsActorTypeMismatch()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ActorTypeMismatch))
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                typeof(FakeAgent).AssemblyQualifiedName!,
                "hello",
                PreferredActorId: "existing-actor"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_ACTOR_TYPE_MISMATCH");
        body.Should().Contain("existing-actor");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldReturnConflict_WhenInteractionPortReportsActorTypeMismatch()
    {
        var interactionPort = new FakeGAgentDraftRunInteractionPort
        {
            ResultFactory = (_, _, _, _) =>
            {
                return Task.FromResult(
                    CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                        GAgentDraftRunStartError.ActorTypeMismatch));
            }
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                typeof(FakeAgent).AssemblyQualifiedName!,
                "hello",
                PreferredActorId: "existing-actor"),
            interactionPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_ACTOR_TYPE_MISMATCH");
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
                typeof(FakeAgent).AssemblyQualifiedName!,
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
                    request.ActorTypeName,
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
        var actorTypeName = typeof(FakeAgent).AssemblyQualifiedName!;

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                actorTypeName,
                "hello"),
            interactionPort,
            logger,
            CancellationToken.None);

        interactionPort.Requests.Should().ContainSingle();
        interactionPort.Requests[0].ActorTypeName.Should().Be(actorTypeName);
        interactionPort.Requests[0].ScopeId.Should().Be("scope-a");
        interactionPort.Requests[0].Prompt.Should().Be("hello");
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
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
        var actorTypeName = typeof(FakeAgent).AssemblyQualifiedName!;

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                actorTypeName,
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
        var actorTypeName = typeof(FakeAgent).AssemblyQualifiedName!;
        var store = new RecordingGAgentActorStore
        {
            Actors =
            [
                new GAgentActorGroup(actorTypeName, ["actor-1", "actor-2"])
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
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(actorTypeName, "actor-3"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        store.AddedActors.Should().BeEmpty();

        var removeResult = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            actorTypeName,
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)removeResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.RemovedActors.Should().ContainSingle(x =>
            x.ScopeId == "scope-a" &&
            x.GAgentType == actorTypeName &&
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
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest("not.a.real.agent.type", "actor-4"),
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
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(actorTypeName, "actor-1"),
            new RecordingGAgentActorStore { ThrowOnAdd = new InvalidOperationException("add failed") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwAdd).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var throwRemove = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            actorTypeName,
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
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(actorTypeName, "actor-1"),
            new RecordingGAgentActorStore { ThrowOnAdd = new Exception("boom") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwAddUnexpected).StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);

        var throwRemoveUnexpected = await InvokeHandleRemoveActorAsync(
            context,
            "scope-a",
            "actor-1",
            actorTypeName,
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
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(typeof(FakeAgent).AssemblyQualifiedName!, "actor-1"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

        var removeResult = await InvokeHandleRemoveActorAsync(
            deniedContext,
            "scope-a",
            "actor-1",
            typeof(FakeAgent).AssemblyQualifiedName!,
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)removeResult).StatusCode.Should().Be((int)HttpStatusCode.Forbidden);

        store.LastRequestedScopeId.Should().BeNull();
        store.AddedActors.Should().BeEmpty();
        store.RemovedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleListGAgentTypesAsync_ShouldReadRegisteredStaticServiceRevisionFacts()
    {
        var staticActorTypeName = "Tests.RegisteredStaticGAgent, Tests.Assembly";
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
                                Static: new ServiceRevisionStaticSnapshot(staticActorTypeName, "preferred-actor"))),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var gAgentType = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        gAgentType.GetProperty("typeName").GetString().Should().Be("RegisteredStaticGAgent");
        gAgentType.GetProperty("fullName").GetString().Should().Be(staticActorTypeName);
        gAgentType.GetProperty("assemblyName").GetString().Should().Be("Tests.Assembly");
        var endpoint = gAgentType.GetProperty("endpoints").EnumerateArray().Should().ContainSingle().Subject;
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
    public async Task HandleListGAgentTypesAsync_ShouldSkipServicesWithoutRevisionCatalog()
    {
        var catalogReader = new FakeServiceCatalogQueryReader
        {
            Services =
            [
                CreateServiceCatalogSnapshot("svc-missing-revisions"),
            ],
        };
        var revisionReader = new FakeServiceRevisionCatalogQueryReader();

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        body.Should().Be("[]");
        revisionReader.RequestedIdentities.Should().ContainSingle(identity =>
            identity.ServiceId == "svc-missing-revisions" &&
            identity.TenantId == "scope-a");
    }

    [Fact]
    public async Task HandleListGAgentTypesAsync_ShouldIgnoreNonStaticAndBlankStaticRevisionFacts()
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
                        CreateStaticRevisionSnapshot("rev-blank-static", " ", "blank-static-endpoint"),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

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
    public async Task HandleListGAgentTypesAsync_ShouldMapBlankDisplayNameAndCustomEndpointKind()
    {
        var staticActorTypeName = "Tests.CustomEndpointGAgent, Tests.Assembly";
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

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var gAgentType = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        gAgentType.GetProperty("typeName").GetString().Should().Be("CustomEndpointGAgent");
        gAgentType.GetProperty("fullName").GetString().Should().Be(staticActorTypeName);
        gAgentType.GetProperty("assemblyName").GetString().Should().Be("Tests.Assembly");
        var endpoint = gAgentType.GetProperty("endpoints").EnumerateArray().Should().ContainSingle().Subject;
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
    public async Task HandleListGAgentTypesAsync_ShouldMergeDuplicateStaticActorTypeEndpoints()
    {
        var staticActorTypeName = "Tests.SharedStaticGAgent, Tests.Assembly";
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
                        CreateStaticRevisionSnapshot("rev-a", staticActorTypeName, "chat", "run"),
                        CreateStaticRevisionSnapshot("rev-b", staticActorTypeName, "chat", "status"),
                    ],
                    DateTimeOffset.UtcNow),
            },
        };

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

        var (statusCode, body) = await ExecuteResultAsync(result);
        statusCode.Should().Be((int)HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var gAgentType = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        gAgentType.GetProperty("typeName").GetString().Should().Be("SharedStaticGAgent");
        gAgentType.GetProperty("fullName").GetString().Should().Be(staticActorTypeName);
        gAgentType.GetProperty("assemblyName").GetString().Should().Be("Tests.Assembly");
        gAgentType.GetProperty("endpoints")
            .EnumerateArray()
            .Select(endpoint => endpoint.GetProperty("endpointId").GetString())
            .Should()
            .BeEquivalentTo(["chat", "run", "status"]);
        gAgentType.GetProperty("endpoints")
            .EnumerateArray()
            .Count(endpoint => endpoint.GetProperty("endpointId").GetString() == "chat")
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task HandleListGAgentTypesAsync_ShouldNotDiscoverLoadedClrAgentClasses()
    {
        var catalogReader = new FakeServiceCatalogQueryReader();
        var revisionReader = new FakeServiceRevisionCatalogQueryReader();

        var result = await InvokeHandleListGAgentTypesAsync(catalogReader, revisionReader);

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
    public void ScopeGAgentEndpointsSource_ShouldNotUseReflectionAsGAgentTypeCatalog()
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

    private static async Task<IResult> InvokeHandleListGAgentTypesAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListGAgentTypesAsync",
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
        string? gagentType,
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
            gagentType,
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

    private static HttpContext CreateDraftRunContext(string? authorization = null, string claimedScopeId = "scope-a")
    {
        var context = CreateScopedHttpContext(claimedScopeId);
        context.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static HttpContext CreateScopedHttpContext(string claimedScopeId)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
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
        params string[] endpointIds) =>
        CreateStaticRevisionSnapshot(
            revisionId,
            actorTypeName,
            endpointIds.Select(CreateEndpointSnapshot).ToArray());

    private static ServiceRevisionSnapshot CreateStaticRevisionSnapshot(
        string revisionId,
        string actorTypeName,
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
                Static: new ServiceRevisionStaticSnapshot(actorTypeName, "preferred-actor")));

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

    private sealed class ScopeGAgentEndpointHostedTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeGAgentEndpointHostedTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<ScopeGAgentEndpointHostedTestHost> StartAsync(
            FakeGAgentDraftRunInteractionPort interactionPort)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<IGAgentDraftRunInteractionPort>(interactionPort);

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

            return new ScopeGAgentEndpointHostedTestHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
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
                        new GAgentDraftRunAcceptedReceipt("actor-default", request.ActorTypeName, "cmd-default", "corr-default"),
                        new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.Unknown, false)));
            }

            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class RecordingGAgentActorStore :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        public List<GAgentActorGroup> Actors { get; set; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];
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

            AddedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
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

            RemovedActors.Add((registration.ScopeId, registration.GAgentType, registration.ActorId));
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
