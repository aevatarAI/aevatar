using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using AiToolResult = Aevatar.AI.Abstractions.ToolResultEvent;

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
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.UnknownActorType))
        };
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Failure(GAgentDraftRunStartError.UnknownActorType)
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.IamNotReal, Aevatar.IamNotReal",
                "hello"),
            interactionService,
            actorPreparationPort,
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
        var interactionService = new FakeGAgentDraftRunInteractionService();
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext(claimedScopeId: "scope-other");

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello"),
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        actorPreparationPort.PrepareCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldTimeoutWhenNoCompletionEventReceived()
    {
        var interactionService = new FakeGAgentDraftRunInteractionService
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
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core", "existing-actor", false))
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
            interactionService,
            actorPreparationPort,
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
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt("existing-actor", "RoleGAgent", "cmd-123", "corr-123");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                await emitAsync(new AGUIEvent
                {
                    TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent
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
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core", "existing-actor", false))
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("corr-123");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("runStarted");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectBlankActorTypeAndPrompt()
    {
        var interactionService = new FakeGAgentDraftRunInteractionService();
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort();
        var logger = LoggerFactory.Create(_ => { });

        var missingTypeContext = CreateDraftRunContext();
        await InvokeHandleDraftRunAsync(
            missingTypeContext,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(" ", "hello"),
            interactionService,
            actorPreparationPort,
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);
        missingPromptContext.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldWriteAuthRequiredErrorWhenInteractionThrowsAfterAccepted()
    {
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = async (_, _, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt("auth-actor", "RoleGAgent", "cmd-auth", "corr-auth");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                throw new NyxIdAuthenticationRequiredException("sign in");
            }
        };
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core", "auth-actor", false))
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("authentication_required");
        body.Should().Contain("NyxID authentication required");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldFail_WhenActorRegistrationFails()
    {
        var executed = false;
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = async (_, _, onAcceptedAsync, ct) =>
            {
                executed = true;
                var receipt = new GAgentDraftRunAcceptedReceipt("actor-1", "RoleGAgent", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                    receipt,
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.RunFinished, true));
            }
        };
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            ThrowOnPrepare = new InvalidOperationException("persist failed")
        };
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello"),
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_DRAFT_RUN_FAILED");
        body.Should().Contain("persist failed");
        body.Should().NotContain("runStarted");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldReturnConflict_WhenInteractionReportsActorTypeMismatch()
    {
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ActorTypeMismatch))
        };
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", typeof(FakeAgent).AssemblyQualifiedName!, "existing-actor", false))
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAGENT_ACTOR_TYPE_MISMATCH");
        body.Should().Contain("existing-actor");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldPreRegisterGeneratedActorId_ForNewActors()
    {
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = async (command, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new GAgentDraftRunAcceptedReceipt(
                    command.PreferredActorId ?? string.Empty,
                    command.ActorTypeName,
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
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", typeof(FakeAgent).AssemblyQualifiedName!, "generated-actor", true))
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        actorPreparationPort.PrepareCalls.Should().ContainSingle();
        actorPreparationPort.PrepareCalls[0].ActorTypeName.Should().Be(actorTypeName);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRollbackPreRegisteredActor_WhenInteractionFailsBeforeResponseStarts()
    {
        var preparedActor = new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "generated-actor",
            true);
        var interactionService = new FakeGAgentDraftRunInteractionService
        {
            ResultFactory = (command, _, _, _) =>
            {
                throw new InvalidOperationException("dispatch failed");
            }
        };
        var actorPreparationPort = new FakeGAgentDraftRunActorPreparationPort
        {
            Result = GAgentDraftRunPreparationResult.Success(preparedActor)
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
            interactionService,
            actorPreparationPort,
            logger,
            CancellationToken.None);

        actorPreparationPort.RollbackCalls.Should().ContainSingle();
        actorPreparationPort.RollbackCalls[0].Should().Be(preparedActor);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void ResolveAgentType_ShouldFindAndNotFindTypes()
    {
        ScopeGAgentActorTypeResolver.Resolve("Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core").Should().NotBeNull();
        ScopeGAgentActorTypeResolver.Resolve("Aevatar.IamNotReal, Aevatar.IamNotReal").Should().BeNull();
    }

    [Fact]
    public void TryMapEnvelopeToAguiEvent_ShouldMapAIAndToolingEvents()
    {
        var textStart = TryMap(BuildEventEnvelope(new AiTextStart { SessionId = "s1", AgentId = "agent-1" }));
        textStart!.TextMessageStart.Should().NotBeNull();
        textStart.TextMessageStart!.MessageId.Should().Be("s1");

        var textContent = TryMap(BuildEventEnvelope(new AiTextContent { Delta = "d", SessionId = "s1" }));
        textContent!.TextMessageContent.Should().NotBeNull();
        textContent.TextMessageContent!.Delta.Should().Be("d");

        var reasoning = TryMap(BuildEventEnvelope(new AiTextReasoning { Delta = "r", SessionId = "s1" }));
        reasoning!.Custom.Should().NotBeNull();
        reasoning.Custom!.Name.Should().Be("TEXT_MESSAGE_REASONING");

        var textEnd = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "done", SessionId = "s1" }));
        textEnd!.TextMessageEnd.Should().NotBeNull();

        var textEndError = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "[[AEVATAR_LLM_ERROR]] boom", SessionId = "s2" }));
        textEndError!.RunError.Should().NotBeNull();
        textEndError.RunError!.Message.Should().Be("boom");

        var textEndFailed = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "LLM request failed: upstream", SessionId = "s2" }));
        textEndFailed!.RunError.Should().NotBeNull();
        textEndFailed.RunError!.Message.Should().Be("LLM request failed: upstream");

        var toolCall = TryMap(BuildEventEnvelope(new AiToolCall
        {
            ToolName = "search",
            CallId = "call-1",
        }));
        toolCall!.ToolCallStart.Should().NotBeNull();

        var toolResult = TryMap(BuildEventEnvelope(new AiToolResult
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
        }));
        toolResult!.ToolCallEnd.Should().NotBeNull();

        var approval = TryMap(BuildToolApprovalEventEnvelope(new ToolApprovalRequestEvent
        {
            RequestId = "req-1",
            SessionId = "s1",
            ToolName = "connector.run",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            IsDestructive = true,
            TimeoutSeconds = 30,
        }));
        approval.Should().NotBeNull();
        approval!.Custom.Should().NotBeNull();
        approval.Custom!.Name.Should().Be("TOOL_APPROVAL_REQUEST");
        approval.Custom.Payload.Should().NotBeNull();
        var approvalStruct = approval.Custom.Payload!.Unpack<Struct>();
        approvalStruct.Fields["toolName"].StringValue.Should().Be("connector.run");
        approvalStruct.Fields["isDestructive"].BoolValue.Should().BeTrue();
        approvalStruct.Fields["timeoutSeconds"].NumberValue.Should().Be(30);

        var agui = TryMap(BuildEventEnvelope(new AGUIEvent
        {
            TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent { MessageId = "m2" }
        }));
        agui.Should().NotBeNull();
        agui!.TextMessageEnd.Should().NotBeNull();

        var none = TryMap(new EventEnvelope());
        none.Should().BeNull();
    }

    [Fact]
    public void TryMapEnvelopeToAguiEvent_ShouldHandleUnknownPayloadAndWrappedAguiEvent()
    {
        TryMap(new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "unknown" }),
        }).Should().BeNull();

        var wrapped = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "thread-1",
                RunId = "run-1",
            },
        };

        TryMap(new EventEnvelope
        {
            Payload = Any.Pack(wrapped),
        }).Should().BeEquivalentTo(wrapped);
    }

    [Fact]
    public void BuildToolApprovalStruct_ShouldHandleDecodeFailure()
    {
        var invalidAny = new Any
        {
            TypeUrl = "type.googleapis.com/aevatar.ai.ToolApprovalRequestEvent",
            Value = ByteString.CopyFromUtf8("broken"),
        };

        var structure = InvokeBuildToolApprovalStruct(invalidAny);
        structure.Fields.Should().ContainKey("error");
        structure.Fields["error"].StringValue.Should().Contain("Failed to decode approval request");
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
        store.LastRequestedScopeId.Should().Be("scope-a");

        var addResult = await InvokeHandleAddActorAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(actorTypeName, "actor-3"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.AddedActors.Should().ContainSingle(x =>
            x.ScopeId == "scope-a" &&
            x.GAgentType == actorTypeName &&
            x.ActorId == "actor-3");

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
        ((IStatusCodeHttpResult)invalidAdd).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

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
        ((IStatusCodeHttpResult)unknownTypeAdd).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

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
        ((IStatusCodeHttpResult)throwAdd).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

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
        ((IStatusCodeHttpResult)throwAddUnexpected).StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);

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
    public void ToCamelCaseAndStripEventSuffix_ShouldTransformWords()
    {
        InvokeToCamelCase("").Should().BeEmpty();
        InvokeToCamelCase("TextEvent").Should().Be("textEvent");

        InvokeStripEventSuffix("ToolResultEvent").Should().Be("ToolResult");
        InvokeStripEventSuffix("NoSuffix").Should().Be("NoSuffix");
    }

    [Fact]
    public void HandleListGAgentTypesAsync_ShouldReturnOkResult()
    {
        var result = InvokeHandleListGAgentTypesAsync();
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    private static AGUIEvent? TryMap(EventEnvelope envelope)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            nameof(ScopeGAgentEndpoints.TryMapEnvelopeToAguiEvent),
            BindingFlags.NonPublic | BindingFlags.Static);
        return (AGUIEvent?)method!.Invoke(null, new object[] { envelope });
    }

    private static Struct InvokeBuildToolApprovalStruct(Any payload)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "BuildToolApprovalStruct",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (Struct)method!.Invoke(null, new object[] { payload })!;
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

    private static string InvokeToCamelCase(string value)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "ToCamelCase",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { value })!;
    }

    private static string InvokeStripEventSuffix(string value)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "StripEventSuffix",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { value })!;
    }

    private static async Task<IResult> InvokeHandleListActorsAsync(
        HttpContext context,
        string scopeId,
        IGAgentActorStore actorStore,
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

    private static IResult InvokeHandleListGAgentTypesAsync()
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListGAgentTypesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (IResult)method!.Invoke(null, [])!;
    }

    private static async Task<IResult> InvokeHandleAddActorAsync(
        HttpContext context,
        string scopeId,
        ScopeGAgentEndpoints.AddGAgentActorHttpRequest request,
        IGAgentActorStore actorStore,
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
        IGAgentActorStore actorStore,
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
            loggerFactory,
            ct,
        })!;
    }

    private static async Task InvokeHandleDraftRunAsync(
        HttpContext context,
        string scopeId,
        ScopeGAgentEndpoints.GAgentDraftRunHttpRequest request,
        ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService,
        IGAgentDraftRunActorPreparationPort actorPreparationPort,
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
                interactionService,
                actorPreparationPort,
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static EventEnvelope BuildEventEnvelope(IMessage message)
    {
        return new EventEnvelope { Payload = Any.Pack(message) };
    }

    private static EventEnvelope BuildToolApprovalEventEnvelope(ToolApprovalRequestEvent approvalRequest)
    {
        return BuildEventEnvelope(approvalRequest);
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeGAgentDraftRunInteractionService
        : ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>
    {
        public Func<
            GAgentDraftRunCommand,
            Func<AGUIEvent, CancellationToken, ValueTask>,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>?,
            CancellationToken,
            Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>>>? ResultFactory { get; init; }

        public Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            if (ResultFactory == null)
            {
                return Task.FromResult(
                    CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
                        new GAgentDraftRunAcceptedReceipt("actor-default", command.ActorTypeName, "cmd-default", "corr-default"),
                        new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(GAgentDraftRunCompletionStatus.Unknown, false)));
            }

            return ResultFactory(command, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class FakeGAgentDraftRunActorPreparationPort : IGAgentDraftRunActorPreparationPort
    {
        public List<GAgentDraftRunPreparationRequest> PrepareCalls { get; } = [];
        public List<GAgentDraftRunPreparedActor> RollbackCalls { get; } = [];
        public GAgentDraftRunPreparationResult Result { get; init; } =
            GAgentDraftRunPreparationResult.Success(
                new GAgentDraftRunPreparedActor("scope-a", "RoleGAgent", "actor-default", false));
        public Exception? ThrowOnPrepare { get; init; }

        public Task<GAgentDraftRunPreparationResult> PrepareAsync(
            GAgentDraftRunPreparationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PrepareCalls.Add(request);
            if (ThrowOnPrepare is not null)
                throw ThrowOnPrepare;

            return Task.FromResult(Result);
        }

        public Task RollbackAsync(
            GAgentDraftRunPreparedActor preparedActor,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add(preparedActor);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGAgentActorStore : IGAgentActorStore
    {
        public List<GAgentActorGroup> Actors { get; set; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];
        public Exception? ThrowOnGet { get; set; }
        public Exception? ThrowOnAdd { get; set; }
        public Exception? ThrowOnRemove { get; set; }
        public string? LastRequestedScopeId { get; private set; }

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet != null) throw ThrowOnGet;
            return Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);
        }

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet != null) throw ThrowOnGet;
            LastRequestedScopeId = scopeId;
            return Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);
        }

        public Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd != null)
                throw ThrowOnAdd;

            AddedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task AddActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd != null)
                throw ThrowOnAdd;

            AddedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove != null)
                throw ThrowOnRemove;

            RemovedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove != null)
                throw ThrowOnRemove;

            RemovedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Func<string, IActor?> _getAsync;
        private readonly IActor _createdActor;
        public List<string> DestroyedActorIds { get; } = [];

        public FakeActorRuntime(Func<string, IActor?> getAsync, IActor? createdActor = null)
        {
            _getAsync = getAsync;
            _createdActor = createdActor ?? new FakeActor("created");
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_createdActor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_createdActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            return Task.FromResult(_getAsync(id));
        }

        public Task<bool> ExistsAsync(string id)
        {
            return Task.FromResult(_getAsync(id) is not null);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent();
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ThrowingActor : IActor
    {
        private readonly Exception _exception;

        public ThrowingActor(string id, Exception exception)
        {
            Id = id;
            _exception = exception;
            Agent = new FakeAgent();
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.FromException(_exception);

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
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

    private sealed class FakeActorEventSubscriptionProvider : IActorEventSubscriptionProvider
    {
        private readonly EventEnvelope[] _envelopes;

        public FakeActorEventSubscriptionProvider(params EventEnvelope[] envelopes)
        {
            _envelopes = envelopes;
        }

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

            if (typeof(TMessage) == typeof(EventEnvelope) && _envelopes.Length > 0)
            {
                var eventHandler = (Func<EventEnvelope, Task>)(object)handler;
                _ = Task.Run(async () =>
                {
                    foreach (var envelope in _envelopes)
                    {
                        ct.ThrowIfCancellationRequested();
                        await eventHandler(envelope);
                    }
                }, ct);
            }

            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
