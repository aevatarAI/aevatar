using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndExecuteRoutedNonStreamingRequest()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                CatalogRouteTarget("catalog-openai", "openai")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("openai/gpt-5")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            0.4,
            64,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        result.Completed!.CompletionStage.Should().Be(ResponsesCompletionStage.ReadModelObserved);
        result.Completed.Completion.OutputText.Should().Be("ok");
        sessions.Registered.Should().ContainSingle().Which.ResponseId.Should().StartWith("resp_");
        sessions.UpdatedStatuses.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5");
        command.RoutePreference.Should().BeEmpty();
        AssertRouteTarget(
            command.RouteTarget,
            CatalogRouteTarget("catalog-openai", "openai"));
        command.ScopeId.Should().Be("scope-1");
        command.BearerToken.Should().Be("token");
        var toolContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        toolContext.Request.RequestId.Should().Be(command.ResponseId);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Caller.ResponseId.Should().Be(command.ResponseId);
        toolContext.Caller.OwnerScopeId.Should().Be("owner-1");
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().BeNull();
        AssertRouteTarget(
            toolContext.Routing.RouteTarget,
            CatalogRouteTarget("catalog-openai", "openai"));
    }

    [Fact]
    public async Task CreateAsync_WhenRouteInventoryIsUnavailable_ShouldReturnServiceUnavailableWithoutDispatch()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            routeResolver: new UnavailableResponsesRouteResolver());

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "anthropic/claude-sonnet",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().Be(new ResponsesCommandError(
            503,
            "model_route_unavailable",
            "The model routing inventory is temporarily unavailable."));
        dispatch.Calls.Should().BeEmpty();
        sessions.Registered.Should().ContainSingle();
        sessions.UpdatedStatuses.Should().ContainSingle()
            .Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenQualifiedModelRouteCannotResolve_ShouldFailClosedWithoutDispatch()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(null));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "chrono-llm/gpt-5.5",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().Be(new ResponsesCommandError(
            404,
            "model_not_found",
            "Model 'gpt-5.5' is not configured for service 'chrono-llm'."));
        dispatch.Calls.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().ContainSingle()
            .Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenBareModelHasNoRoutePreference_ShouldUseDefaultGateway()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(null));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "gpt-5.5",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
        command.RoutePreference.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldApplyConfiguredDefaultModel_WhenCallerOmitsModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                UserRouteTarget("us-chrono-public", "chrono-llm-public")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            defaultIngressModel: "chrono-llm-public/gpt-5.5");

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "  ",
            "hi without a model",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
        command.RoutePreference.Should().BeEmpty();
        AssertRouteTarget(
            command.RouteTarget,
            UserRouteTarget("us-chrono-public", "chrono-llm-public"));
    }

    [Fact]
    public async Task CreateAsync_ShouldUseAccountPreferredModel_WhenCallerOmits_OverRoutePolicy()
    {
        // Account UserConfig beats a stale per-owner route-policy ForwardToModel on the ingress.
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                UserRouteTarget("us-chrono", "chrono-llm")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(
                ForwardToModelAction("deepseek/deepseek-chat")),
            defaultIngressModel: "fallback-vendor/fallback-model",
            ownerLlmConfigSource: new StubOwnerLlmConfigSource(
                OwnerConfig("chrono-llm/gpt-5.5")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "  ",
            "hi without a model",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task CreateAsync_WhenFallbackRouteHasModel_ShouldPreserveExplicitPrefixedRequestModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                UserRouteTarget("us-chrono", "chrono-llm")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(
                ForwardToModelAction("gpt-5.4-mini"),
                usedFallback: true));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "chrono-llm/gpt-5.4-mini",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.4-mini");
        command.RoutePreference.Should().BeEmpty();
        AssertRouteTarget(
            command.RouteTarget,
            UserRouteTarget("us-chrono", "chrono-llm"));
    }

    [Fact]
    public async Task CreateAsync_WhenDefaultRouteHasModel_ShouldPreserveExplicitPrefixedRequestModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                UserRouteTarget("us-chrono", "chrono-llm")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(
                ForwardToModelAction("gpt-5.4-mini")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "chrono-llm/gpt-5.4-mini",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.4-mini");
        command.RoutePreference.Should().BeEmpty();
        AssertRouteTarget(
            command.RouteTarget,
            UserRouteTarget("us-chrono", "chrono-llm"));
    }

    [Fact]
    public async Task CreateAsync_WhenRoutePinsGAgentTool_ShouldRegisterSessionAndExecuteThroughLlm()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var invokeTool = new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent");
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(GAgentToolHintAction("member-1")),
            toolSetRegistry: new StaticToolSetRegistry([invokeTool]),
            ownedToolCatalogPlanner: new StaticOwnedToolCatalogPlanner(
                "workspace.default",
                [invokeTool]));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        sessions.Registered.Should().ContainSingle();
        result.Completed.Should().NotBeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_gagent");
        command.ToolSelection.OwnedToolNames.Should().Contain("aevatar_invoke_gagent");
        // Off-grain run re-resolves this name to re-materialize the route tool set.
        command.ToolSelection.ToolSetName.Should().Be("workspace.default");
    }

    [Fact]
    public async Task CreateAsync_WhenForwardToModelCarriesToolSetAndChoiceHint_ShouldAddToolsAndApplyHint()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var action = new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ModelName = "routed-model",
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                    PrefilledArguments = new Google.Protobuf.WellKnownTypes.Struct
                    {
                        Fields =
                        {
                            ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("member-1"),
                        },
                    },
                },
            },
        };
        var invokeTool = new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent");
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(action),
            toolSetRegistry: new StaticToolSetRegistry([invokeTool]),
            ownedToolCatalogPlanner: new StaticOwnedToolCatalogPlanner(
                "workspace.default",
                [invokeTool]));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_gagent");
        command.ToolSelection.OwnedToolNames.Should().Contain("aevatar_invoke_gagent");
        command.ToolSelection.ToolChoiceHintArguments.Fields["actor_id"].StringValue.Should().Be("member-1");
        sessions.RecordedToolCalls.Should().BeEmpty("tool set tools execute locally and are not client-forwarded tools");
    }

    [Fact]
    public async Task CreateAsync_WithForwardedClientTool_ShouldWriteTypedToolSchema()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(dispatchPort: dispatch);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            [
                new ResponsesApplicationToolDeclaration(
                    "get_weather",
                    "Get weather",
                    """{"type":"object","properties":{"city":{"type":"string"}}}""",
                    "schema-1"),
            ]), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var declaration = command.ToolSelection.ForwardedTools.Should().ContainSingle().Subject;
        declaration.Parameters.Fields["type"].StringValue.Should().Be("object");
        declaration.Parameters.Fields["properties"].StructValue.Fields["city"].StructValue.Fields["type"]
            .StringValue.Should().Be("string");
    }

    [Fact]
    public void ToRuntimeToolCall_ShouldWriteTypedRuntimeToolArguments()
    {
        var method = typeof(ResponsesCommandFacade).GetMethod(
            "ToRuntimeToolCall",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var converted = (LlmSessionRuntimeToolCall)method!.Invoke(null,
        [
            new ToolCall
            {
                Id = "call_1",
                Name = "get_weather",
                ArgumentsJson = """{"city":"Singapore"}""",
            },
        ])!;

        converted.Arguments.Fields["city"].StringValue.Should().Be("Singapore");
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnAuthenticationError_WhenCallerScopeCannotBeResolved()
    {
        var facade = CreateFacade(callerScopeResolver: new ThrowingCallerScopeResolver());

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "access token is invalid"));
    }

    [Fact]
    public async Task CreateAsync_WithPreviousResponseAfterBearerScopeRotation_ShouldRejectBeforeRegistrationOrDispatch()
    {
        const string previousResponseId = "resp_previous";
        var previousSnapshot = BuildSnapshot(previousResponseId, scopeId: "old-scope");
        var queryPort = new RecordingSessionQueryPort { Snapshot = previousSnapshot };
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var callerScopeResolver = new StaticCallerScopeResolver("new-scope", "owner-1", LlmSessionOriginKind.ApiKey);
        var facade = CreateFacade(
            sessionPort: sessions,
            queryPort: queryPort,
            callerScopeResolver: callerScopeResolver,
            dispatchPort: dispatch);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            null,
            [
                new ResponsesToolResultInput(
                    "call_1",
                    """{"ok":true}""",
                    null),
            ],
            false,
            previousResponseId,
            null,
            null,
            []), CallerScopeContext("rotated-token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            403,
            "response_scope_mismatch",
            "response id is not visible to the current caller scope."));
        result.Completed.Should().BeNull();
        result.StreamPlan.Should().BeNull();
        sessions.Registered.Should().BeEmpty();
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
        sessions.RecordedToolCalls.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithAlreadyResolvedToolResults_ShouldReturnCommittedCompletionWithoutReadModelPolling()
    {
        const string previousResponseId = "resp_previous";
        var previousSnapshot = BuildSnapshot(
            previousResponseId,
            scopeId: "scope-1",
            forwardedToolCalls:
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    "schema-1",
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Resolved,
                    DateTimeOffset.UtcNow.AddHours(1),
                    """{"temperature":"31C"}""",
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow.AddSeconds(-30)),
            ]);
        var queryPort = new RecordingSessionQueryPort
        {
            Snapshot = previousSnapshot,
        };
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            queryPort: queryPort,
            dispatchPort: dispatch);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            null,
            [
                new ResponsesToolResultInput(
                    "call_1",
                    """{"from":"client"}""",
                    "schema-1"),
            ],
            false,
            previousResponseId,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        result.Completed!.CompletionStage.Should().Be(ResponsesCompletionStage.Committed);
        result.Completed.Completion.OutputText.Should().Be("""{"temperature":"31C"}""");
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("""{"temperature":"31C"}""");
        queryPort.ReadCount.Should().Be(1, "the facade should validate previous_response_id but must not poll the completion readmodel");
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_ShouldRejectInvisibleResponse_AndCancelVisibleResponseThroughActor()
    {
        var queryPort = new RecordingSessionQueryPort
        {
            Snapshot = BuildSnapshot("resp_1", scopeId: "other-scope"),
        };
        var sessionPort = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessionPort, queryPort: queryPort);

        var invisible = await facade.CancelAsync("resp_1", CallerScopeContext("token"));

        invisible.Error!.Code.Should().Be("response_scope_mismatch");
        sessionPort.UpdatedStatuses.Should().BeEmpty();

        queryPort.Snapshot = BuildSnapshot("resp_1", scopeId: "scope-1");
        var cancelled = await facade.CancelAsync("resp_1", CallerScopeContext("token"));

        cancelled.Error.Should().BeNull();
        cancelled.ResponseId.Should().Be("resp_1");
        sessionPort.CancelledRuns.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "resp_1", "resp_1:llm-run"));
    }

    [Fact]
    public async Task CancelAsync_ShouldRejectExpiredResponse_WithoutUpdatingSession()
    {
        var queryPort = new RecordingSessionQueryPort
        {
            Snapshot = BuildSnapshot("resp_expired", scopeId: "scope-1", status: LlmSessionStatus.Expired),
        };
        var sessionPort = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessionPort, queryPort: queryPort);

        var result = await facade.CancelAsync("resp_expired", CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            400,
            "response_expired",
            "response id refers to an expired response session."));
        sessionPort.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_ShouldReturnRejected_WhenSessionActorRejectsCancel()
    {
        var queryPort = new RecordingSessionQueryPort
        {
            Snapshot = BuildSnapshot("resp_active", scopeId: "scope-1"),
        };
        var sessionPort = new RecordingSessionPort
        {
            CancelRunException = new InvalidOperationException("cannot cancel completed response"),
        };
        var facade = CreateFacade(sessionPort: sessionPort, queryPort: queryPort);

        var result = await facade.CancelAsync("resp_active", CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            400,
            "response_cancel_rejected",
            "cannot cancel completed response"));
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnObservedCompletion_AndDispatchWithResponseCorrelation()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var observer = StaticLlmSessionRunObservationService.Completed("Hello");
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationService: observer);
        var deltas = new List<string>();

        var result = await facade.StreamAsync(
            BuildStreamPlan(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Error.Should().BeNull();
        result.Completion.Should().NotBeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        deltas.Should().Equal("Hello");
        dispatch.Calls.Should().ContainSingle();
        var call = dispatch.Calls.Single();
        call.Envelope.Propagation!.CorrelationId.Should().Be("resp_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be("resp_stream");
        command.RunId.Should().Be("resp_stream:llm-run");
        observer.LastRequest.Should().NotBeNull();
        observer.LastRequest!.ResponseId.Should().Be("resp_stream");
        observer.LastRequest.RunId.Should().Be("resp_stream:llm-run");
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_WhenOffActorFlagIsOn_ShouldUseExecutorStartAdmissionWithoutLegacyRunDispatch()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var observer = StaticLlmSessionRunObservationService.Completed("Hello");
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationService: observer,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "model",
                OffActorLlmRunExecutorEnabled = true,
            });
        var deltas = new List<string>();

        var result = await facade.StreamAsync(
            BuildStreamPlan(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Error.Should().BeNull();
        result.Completion.Should().NotBeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        deltas.Should().Equal("Hello");
        dispatch.Calls.Should().BeEmpty();
        observer.LastAdmission.Should().Be(executor.StartAdmissions.Should().ContainSingle().Subject);
        observer.LastAdmission!.CommandId.Should().Be("start-resp_stream");
        var request = executor.StartedRequests.Should().ContainSingle().Subject;
        request.SessionActorId.Should().Be("actor-resp_stream");
        request.ResponseId.Should().Be("resp_stream");
        request.RunId.Should().Be("resp_stream:llm-run");
        request.Command.ResponseId.Should().Be("resp_stream");
        request.Command.Model.Should().Be("model");
        observer.LastRequest.Should().NotBeNull();
        observer.LastRequest!.ResponseId.Should().Be("resp_stream");
        observer.LastRequest.RunId.Should().Be("resp_stream:llm-run");
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAuthenticationError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdAuthenticationRequiredException("test-provider"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_ShouldCarryTypedToolContext_WhenRequestIsStreaming()
    {
        var facade = CreateFacade(
            routeResolver: new StaticResponsesRouteResolver(
                CatalogRouteTarget("catalog-openai", "openai")),
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("openai/gpt-5")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            true,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.StreamPlan.Should().NotBeNull();
        result.StreamPlan!.LlmRequest.ToolContext.Should().NotBeNull();
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey("scope_id");
        result.StreamPlan.LlmRequest.ToolContext!.Request.RequestId.Should().Be(result.StreamPlan.Normalized.ResponseId);
        result.StreamPlan.LlmRequest.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        result.StreamPlan.LlmRequest.ToolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        result.StreamPlan.LlmRequest.ToolContext.Routing.NyxIdRoutePreference.Should().BeNull();
        AssertRouteTarget(
            result.StreamPlan.LlmRequest.RouteTarget,
            CatalogRouteTarget("catalog-openai", "openai"));
        AssertRouteTarget(
            result.StreamPlan.LlmRequest.ToolContext.Routing.RouteTarget,
            CatalogRouteTarget("catalog-openai", "openai"));
    }

    [Fact]
    public async Task CreateAsync_WhenNamedSkillTriggerProvided_ShouldRouteCommandAndCarryRecoveryContext()
    {
        var dispatch = new RecordingActorDispatchPort();
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("openai/gpt-5"));
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                CatalogRouteTarget("catalog-openai", "openai")),
            chatRouteDecisionPort: routeDecisionPort);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "::Goal ship today",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        routeDecisionPort.LastRequest.Should().NotBeNull();
        routeDecisionPort.LastRequest!.CommandName.Should().Be("goal");
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var recovery = AgentToolExecutionContextMapper.FromPayload(command.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("goal");
        recovery.PrimarySkillName.Should().Be("goal");
        recovery.CommandArguments.Should().Be("ship today");
        recovery.OriginalCommand.Should().Be("::Goal ship today");
        recovery.DiscoveryRequested.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenDiscoveryTriggerProvided_ShouldKeepRouteCommandEmptyAndRequestDiscovery()
    {
        var dispatch = new RecordingActorDispatchPort();
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("openai/gpt-5"));
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver(
                CatalogRouteTarget("catalog-openai", "openai")),
            chatRouteDecisionPort: routeDecisionPort);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "::",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        routeDecisionPort.LastRequest.Should().NotBeNull();
        routeDecisionPort.LastRequest!.CommandName.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var recovery = AgentToolExecutionContextMapper.FromPayload(command.ToolContext).SkillRecovery;
        recovery.DiscoveryRequested.Should().BeTrue();
        recovery.CommandName.Should().BeNull();
        recovery.PrimarySkillName.Should().BeNull();
        recovery.OriginalCommand.Should().Be("::");
    }

    [Fact]
    public async Task CreateAsync_WhenObservedRunFails_ShouldReturnFailure_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.Failed,
                500,
                "provider_failed",
                "provider crashed"));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "provider_failed",
            "provider crashed"));
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenObservedRunIsCancelled_ShouldReturnCancel_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.Cancelled,
                409,
                "run_cancelled",
                "LLM run was cancelled."));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            409,
            "run_cancelled",
            "LLM run was cancelled."));
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenObservationTimesOut_ShouldReturnTimeout_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.TimedOut,
                504,
                "response_timeout",
                "Timed out waiting 30 seconds for the LLM run to emit a terminal event."));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            504,
            "response_timeout",
            "Timed out waiting 30 seconds for the LLM run to emit a terminal event."));
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenRequestIsCancelled_ShouldReturnTimeout_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(ct => new OperationCanceledException(ct));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            408,
            "request_timeout",
            "Request timed out."));
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_WhenObservedRunFails_ShouldReturnFailure_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.Failed,
                500,
                "llm_run_failed",
                "provider crashed"));

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "llm_run_failed",
            "provider crashed"));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_WhenObservedRunIsCancelled_ShouldReturnCancel_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.Cancelled,
                409,
                "run_cancelled",
                "LLM run was cancelled."));

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            409,
            "run_cancelled",
            "LLM run was cancelled."));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_WhenObservationTimesOut_ShouldReturnTimeout_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.TimedOut,
                504,
                "response_timeout",
                "Timed out waiting 30 seconds for the LLM run to emit a terminal event."));

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            504,
            "response_timeout",
            "Timed out waiting 30 seconds for the LLM run to emit a terminal event."));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnUpstreamError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdUpstreamException(
                NyxIdUpstreamFailureKind.RateLimited,
                429,
                "route-a",
                "model-a",
                "rate limited"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            429,
            "ratelimited",
            "rate limited"));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnTimeout_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(ct => new OperationCanceledException(ct));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask, cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            408,
            "request_timeout",
            "Request timed out."));
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnApiError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new InvalidOperationException("provider crashed"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Internal server error."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenOffActorFlagIsOff_ShouldKeepLegacyRunRequestedDispatch()
    {
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "model",
                OffActorLlmRunExecutorEnabled = false,
            });

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        dispatch.Calls.Should().ContainSingle()
            .Which.Envelope.Payload!.Is(LlmRunRequested.Descriptor).Should().BeTrue();
        executor.StartedRequests.Should().BeEmpty();
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenOffActorFlagIsOn_ShouldAdmitExecutorStartWithoutLegacyRunDispatch()
    {
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var observation = StaticLlmSessionRunObservationService.Completed("done");
        var facade = CreateFacade(
            dispatchPort: dispatch,
            observationService: observation,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "model",
                OffActorLlmRunExecutorEnabled = true,
            });

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed!.Completion.OutputText.Should().Be("done");
        dispatch.Calls.Should().BeEmpty();
        var admission = executor.StartAdmissions.Should().ContainSingle().Subject;
        admission.Accepted.Should().BeTrue();
        admission.ActorId.Should().NotBeNullOrWhiteSpace();
        admission.CorrelationId.Should().NotBeNullOrWhiteSpace();
        admission.CommandId.Should().StartWith("start-");
        executor.StartedRequests.Should().ContainSingle();
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    private static ResponsesCommandFacade CreateFacade(
        ILlmSessionRegistrationPort? sessionPort = null,
        ILlmSessionQueryPort? queryPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        IToolSetRegistry? toolSetRegistry = null,
        IActorDispatchPort? dispatchPort = null,
        ILlmSessionRunObservationService? observationService = null,
        string? defaultIngressModel = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ResponsesIngressOptions? ingressOptions = null,
        ILlmRunExecutor? llmRunExecutor = null,
        IResponsesOwnedToolCatalogPlanner? ownedToolCatalogPlanner = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        var options = ingressOptions ?? (defaultIngressModel is null
            ? null
            : new ResponsesIngressOptions { DefaultModel = defaultIngressModel });
        return new ResponsesCommandFacade(
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            routeResolver ?? new StaticResponsesRouteResolver(null),
            effectiveSessionPort,
            queryPort ?? (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            dispatchPort ?? new RecordingActorDispatchPort(),
            new ResponsesToolClassificationService([], NullLogger<ResponsesToolClassificationService>.Instance),
            new ResponsesDirectToolPlanService(toolSetRegistry ?? new EmptyToolSetRegistry()),
            observationService ?? StaticLlmSessionRunObservationService.Completed("ok"),
            NullLogger<ResponsesCommandFacade>.Instance,
            options is null ? null : Options.Create(options),
            ownerLlmConfigSource,
            llmRunExecutor,
            ownedToolCatalogPlanner);
    }

    private sealed class StaticOwnedToolCatalogPlanner(
        string toolSetName,
        IReadOnlyList<IAgentTool> tools) : IResponsesOwnedToolCatalogPlanner
    {
        private readonly AgentProfileSnapshot _profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-test",
            ProfileVersion = "1.0.0",
            AgentKind = "workspace.chat",
            PolicyRevision = "policy-test",
            RouteToolSetRef = toolSetName,
            PublishedRevision = 1,
            MaxOwnedToolCount = AgentTurnToolCatalogBudget.Ordinary.MaximumToolCount,
            MaxSchemaBytes = AgentTurnToolCatalogBudget.Ordinary.MaximumSchemaBytes,
        });

        public Task<ResponsesOwnedToolCatalogPlan> PlanAsync(
            ChatRouteAction? routeAction,
            string scopeId,
            string turnIdentity,
            string userMessage,
            AgentToolExecutionContext toolContext,
            CancellationToken ct = default)
        {
            var catalog = new AgentTurnToolCatalog(
                tools.Select(static tool => tool.Name),
                profilePromptLayer: null,
                selectedSkillPromptLayer: null,
                selectedIntentId: "invoke",
                candidateIntentId: "invoke",
                diagnostics: null,
                exactTools: tools);
            return Task.FromResult(new ResponsesOwnedToolCatalogPlan(
                catalog,
                _profile,
                toolSetName,
                null));
        }
    }

    private static OwnerLlmConfig OwnerConfig(string modelId) => new(
        new LLMSelection
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = LLMSelectionPolicy.GatewayRoute,
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = modelId,
            },
        },
        LLMSelectionPersistenceStatus.Ready,
        0);

    private sealed class StubOwnerLlmConfigSource(OwnerLlmConfig? config = null)
        : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(config ?? OwnerLlmConfig.Empty);
    }

    private static ResponsesCreateCommandPlan BuildStreamPlan() =>
        new(
            new NormalizedResponsesRequest(
                "resp_stream",
                "msg_stream",
                "model",
                "hello",
                true,
                null,
                null,
                null,
                [],
                []),
            new LlmSessionRegistrationResult("actor-resp_stream", "resp_stream"),
            null,
            new LLMRequest
            {
                RequestId = "resp_stream",
                Model = "model",
                Messages = [ChatMessage.User("hello")],
                ToolContext = BuildToolContext("resp_stream"),
            },
            BuildToolContext("resp_stream"),
            new ResponsesToolClassification([], [], [], [], []),
            ResponsesToolChoiceHintPlan.Empty,
            DateTimeOffset.UtcNow);

    private static AgentToolExecutionContext BuildToolContext(string responseId) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(responseId, null),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", responseId),
        };

    private static LlmSessionSnapshot BuildSnapshot(
        string responseId,
        string scopeId,
        LlmSessionStatus status = LlmSessionStatus.Accepted,
        IReadOnlyList<LlmSessionForwardedToolCallSnapshot>? forwardedToolCalls = null,
        LlmSessionCompletionSnapshot? completion = null) =>
        new(
            responseId,
            scopeId,
            "owner-1",
            LlmSessionOriginKind.ApiKey,
            null,
            status,
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            null,
            "actor-1",
            1,
            "event-1",
            forwardedToolCalls,
            completion);

    private static LLMRouteTarget CatalogRouteTarget(string catalogServiceId, string serviceSlug) => new()
    {
        CatalogServiceId = catalogServiceId,
        ServiceSlugSnapshot = serviceSlug,
    };

    private static LLMRouteTarget UserRouteTarget(string userServiceId, string serviceSlug) => new()
    {
        UserServiceId = userServiceId,
        ServiceSlugSnapshot = serviceSlug,
    };

    private static void AssertRouteTarget(LLMRouteTarget? actual, LLMRouteTarget expected)
    {
        actual.Should().NotBeNull();
        actual!.SourceIdentityCase.Should().Be(expected.SourceIdentityCase);
        actual.CatalogServiceId.Should().Be(expected.CatalogServiceId);
        actual.UserServiceId.Should().Be(expected.UserServiceId);
        actual.ServiceSlugSnapshot.Should().Be(expected.ServiceSlugSnapshot);
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ResponsesCallerScopeResolutionContext CallerScopeContext(string bearerToken) =>
        new(bearerToken, null, null);

    private static async Task WaitForCompletionAsync(TaskCompletionSource completion, CancellationToken ct)
    {
        using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
        await completion.Task;
    }

    private static ChatRouteAction GAgentToolHintAction(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId),
                    },
                },
            },
        },
    };

    private sealed class StaticCallerScopeResolver(
        string scopeId = "scope-1",
        string ownerSubject = "owner-1",
        LlmSessionOriginKind originKind = LlmSessionOriginKind.ApiKey) : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope(scopeId, ownerSubject, originKind));
    }

    private sealed class ThrowingCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
            throw new ResponsesCallerScopeUnavailableException("access token is invalid");
    }

    private sealed class StaticResponsesRouteResolver(LLMRouteTarget? routeTarget) : IResponsesRouteResolver
    {
        public Task<LLMRouteTarget?> ResolveRouteTargetAsync(
            string serviceSlug,
            string upstreamModelId,
            ResponsesCallerScope callerScope,
            CancellationToken ct) =>
            Task.FromResult(routeTarget?.Clone());
    }

    private sealed class UnavailableResponsesRouteResolver : IResponsesRouteResolver
    {
        public Task<LLMRouteTarget?> ResolveRouteTargetAsync(
            string serviceSlug,
            string upstreamModelId,
            ResponsesCallerScope callerScope,
            CancellationToken ct) =>
            throw new ResponsesRouteUnavailableException("inventory unavailable");
    }

    private sealed class StaticResponsesChatRouteDecisionPort(
        ChatRouteAction action,
        bool usedFallback = false,
        string matchedRuleId = "")
        : IResponsesChatRouteDecisionPort
    {
        public ResponsesChatRouteDecisionRequest? LastRequest { get; private set; }

        public Task<ChatRouteDecision> ResolveAsync(
            ResponsesChatRouteDecisionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatRouteDecision
            {
                Action = action.Clone(),
                UsedFallback = usedFallback,
                MatchedRuleId = matchedRuleId,
            });
        }
    }

    private sealed class EmptyToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(string? name) =>
            ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                name ?? string.Empty,
                $"Tool set '{name}' is not registered.",
                []));
    }

    private sealed class StaticToolSetRegistry(IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["workspace.default"];

        public ToolSetResolveResult Resolve(string? name) =>
            ToolSetResolveResult.Success(
                name ?? "workspace.default",
                [new StaticAgentToolSource(tools)]);
    }

    private sealed class StaticAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class StubAgentTool : IAgentTool
    {
        public StubAgentTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        public string ParametersSchema { get; } = """{"type":"object","properties":{}}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"ok":true}""");
    }

    private sealed class StaticLlmSessionRunObservationService(
        LlmSessionRunObservedResult result,
        IReadOnlyList<LlmSessionRunObservedDelta>? deltas = null) : ILlmSessionRunObservationService
    {
        public LlmSessionRunObservationRequest? LastRequest { get; private set; }

        public DispatchAdmission? LastAdmission { get; private set; }

        public static StaticLlmSessionRunObservationService Completed(string outputText) =>
            new(
                new LlmSessionRunObservedResult(
                    null,
                    new LlmSessionCompletionSnapshot(outputText, [], DateTimeOffset.UtcNow, null, null),
                    null),
                [new LlmSessionRunObservedDelta(outputText, null)]);

        public static StaticLlmSessionRunObservationService Error(
            LlmSessionRunObservedTerminalKind kind,
            int statusCode,
            string code,
            string message) =>
            new(new LlmSessionRunObservedResult(
                null,
                null,
                new LlmSessionRunObservedError(kind, statusCode, code, message)));

        public async Task<LlmSessionRunObservedResult> ObserveAsync(
            LlmSessionRunObservationRequest request,
            Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var admission = await request.DispatchAsync(ct);
            LastAdmission = admission;
            foreach (var delta in deltas ?? [])
            {
                if (onDelta != null)
                    await onDelta(delta, ct);
            }

            return result with { Admission = admission };
        }
    }

    private sealed class BlockingLlmRunExecutor : ILlmRunExecutor
    {
        private readonly TaskCompletionSource _releaseExecute =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LlmRunExecutorRequest> StartedRequests { get; } = [];

        public List<DispatchAdmission> StartAdmissions { get; } = [];

        public TaskCompletionSource ExecuteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExecuteReleased { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> StartAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            StartedRequests.Add(request);
            var envelope = new EventEnvelope
            {
                Id = "start-" + request.ResponseId,
                Propagation = new EnvelopePropagation { CorrelationId = request.ResponseId },
                Payload = Any.Pack(new RecordLlmRunStarted
                {
                    Command = request.Command.Clone(),
                    StartedAt = request.Command.RequestedAt?.Clone(),
                }),
            };
            var admission = DispatchAdmissionFactory.Create(request.SessionActorId, envelope);
            StartAdmissions.Add(admission);
            return Task.FromResult(admission);
        }

        public async Task ExecuteAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            ExecuteStarted.SetResult();
            await WaitForCompletionAsync(_releaseExecute, ct);
            ExecuteReleased.SetResult();
        }

        public void ReleaseExecute() => _releaseExecute.SetResult();
    }

    private sealed class RecordingActorDispatchPort(
        Func<CancellationToken, Exception>? dispatchExceptionFactory = null) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (dispatchExceptionFactory?.Invoke(ct) is { } ex)
                throw ex;
            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

        public List<(string ActorId, string ResponseId, string RunId)> CancelledRuns { get; } = [];

        public List<LlmSessionForwardedToolCall> RecordedToolCalls { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId, string SchemaHash, string ResultJson)> ToolResults { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId)> ResolvedToolResults { get; } = [];

        public RecordingSessionQueryPort QueryPort { get; } = new();

        public Exception? UpdateStatusException { get; init; }

        public Exception? CancelRunException { get; init; }

        public Task<LlmSessionRegistrationResult> RegisterAsync(LlmSessionRecord record, CancellationToken ct = default)
        {
            Registered.Add(record);
            return Task.FromResult(new LlmSessionRegistrationResult("actor-" + record.ResponseId, record.ResponseId));
        }

        public Task UpdateStatusAsync(string sessionActorId, string responseId, LlmSessionStatus status, CancellationToken ct = default)
        {
            if (UpdateStatusException is not null)
                throw UpdateStatusException;
            UpdatedStatuses.Add((sessionActorId, responseId, status));
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(
            string sessionActorId,
            string responseId,
            string runId,
            CancellationToken ct = default)
        {
            if (CancelRunException is not null)
                throw CancelRunException;
            CancelledRuns.Add((sessionActorId, responseId, runId));
            return Task.CompletedTask;
        }

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default)
        {
            RecordedToolCalls.Add(call);
            return Task.CompletedTask;
        }

        public Task RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            RecordedCompletions.Add(completion.Clone());
            return Task.CompletedTask;
        }

        public Task ReceiveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            string schemaHash,
            string resultJson,
            CancellationToken ct = default)
        {
            ToolResults.Add((sessionActorId, responseId, callId, schemaHash, resultJson));
            return Task.CompletedTask;
        }

        public Task ResolveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            CancellationToken ct = default)
        {
            ResolvedToolResults.Add((sessionActorId, responseId, callId));
            return Task.CompletedTask;
        }

        private static LlmSessionCompletionSnapshot ToSnapshot(LlmSessionCompletion completion) =>
            new(
                completion.OutputText,
                completion.ToolCalls
                    .Select(static tool => new LlmSessionCompletedToolCallSnapshot(
                        tool.CallId,
                        tool.ToolName,
                        ResponsesJsonValues.ToBoundaryJson(tool.Result)))
                    .ToArray(),
                completion.CompletedAt?.ToDateTimeOffset(),
                string.IsNullOrWhiteSpace(completion.FailureCode) ? null : completion.FailureCode,
                string.IsNullOrWhiteSpace(completion.FailureMessage) ? null : completion.FailureMessage,
                completion.Usage is null
                    ? null
                    : new TokenUsage(
                        completion.Usage.PromptTokens,
                        completion.Usage.CompletionTokens,
                        completion.Usage.TotalTokens));
    }

    private sealed class RecordingSessionQueryPort : ILlmSessionQueryPort
    {
        public LlmSessionSnapshot? Snapshot { get; set; }

        public int ReadCount { get; private set; }

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(string responseId, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(Snapshot);
        }
    }

}
