using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ChatCompletionsCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndDispatchLlmRun()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_1")
            .WithCompletedText("hello")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch, observationRuntime: observation);

        var result = await facade.CreateAsync(BuildRequest("chrono/gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        result.Accepted.Should().BeNull();
        result.Completed!.Completion.OutputText.Should().Be("hello");
        sessions.Registered.Should().ContainSingle().Which.ScopeId.Should().Be("scope-1");
        sessions.RecordedCompletions.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(result.Completed.Normalized.CompletionId.Insert(0, "actor-"));
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be(result.Completed.Normalized.CompletionId);
        command.RunId.Should().Be($"{result.Completed.Normalized.CompletionId}:llm-run");
        command.Model.Should().Be("gpt-4o-mini");
        command.ScopeId.Should().Be("scope-1");
        command.BearerToken.Should().Be("token");
        command.Messages.Should().ContainSingle().Which.Content.Should().Be("hello");
        var toolContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        toolContext.Request.RequestId.Should().Be(command.ResponseId);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Caller.ResponseId.Should().Be(command.ResponseId);
        toolContext.Caller.OwnerScopeId.Should().Be("owner-1");
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_ShouldApplyConfiguredDefaultModel_WhenCallerOmitsModel()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_default_model")
            .WithCompletedText("hello")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: observation,
            defaultIngressModel: "chrono-llm-public/gpt-5.5");

        var result = await facade.CreateAsync(BuildRequest("  "), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
        command.RoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_ShouldUseExplicitCallerModel_OverRoutePolicyForwardToModel()
    {
        // Regression for the deepseek incident: a stale per-owner route-policy ForwardToModel
        // must NOT override the caller's explicit model.
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(
                ForwardToModelAction("deepseek/deepseek-chat")));

        var result = await facade.CreateAsync(BuildRequest("chrono-llm/gpt-5.5"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task CreateAsync_ShouldUseAccountPreferredModel_WhenCallerOmits_OverRoutePolicy()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(
                ForwardToModelAction("deepseek/deepseek-chat")),
            defaultIngressModel: "fallback-vendor/fallback-model",
            ownerLlmConfigSource: new StubOwnerLlmConfigSource(
                OwnerConfig("chrono-llm/gpt-5.5")));

        var result = await facade.CreateAsync(BuildRequest("  "), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task CreateAsync_ShouldPreferExplicitCallerModel_OverAccountPreferredModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            ownerLlmConfigSource: new StubOwnerLlmConfigSource(
                OwnerConfig("chrono-llm/gpt-5.5")));

        var result = await facade.CreateAsync(BuildRequest("openai/gpt-4o"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task CreateAsync_ShouldSwallowOwnerConfigFailure_AndFallBackToDeploymentDefault()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            defaultIngressModel: "chrono-llm-public/gpt-5.5",
            ownerLlmConfigSource: new StubOwnerLlmConfigSource(throwOnGet: true));

        var result = await facade.CreateAsync(BuildRequest("  "), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task CreateAsync_WhenNamedSkillTriggerProvided_ShouldRouteCommandAndCarryRecoveryContext()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_skill")
            .WithCompletedText("ok")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("chrono/gpt-5-chat"));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: routeDecisionPort,
            observationRuntime: observation);

        var result = await facade.CreateAsync(
            BuildRequest("gpt-4o-mini", chatMessages: [ChatMessage.User("::Goal ship today")]),
            CallerScopeContext("token"));

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
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_discovery")
            .WithCompletedText("ok")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("chrono/gpt-5-chat"));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: routeDecisionPort,
            observationRuntime: observation);

        var result = await facade.CreateAsync(
            BuildRequest("gpt-4o-mini", chatMessages: [ChatMessage.User("::")]),
            CallerScopeContext("token"));

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

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task CreateAsync_WhenRequestValidationFails_ShouldReturnBadRequest(
        ChatCompletionsCommandRequest request,
        string expectedCode)
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(request, CallerScopeContext("token"));

        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(400);
        result.Error.Code.Should().Be(expectedCode);
        result.Accepted.Should().BeNull();
        result.StreamPlan.Should().BeNull();
        sessions.Registered.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenCallerScopeUnavailable_ShouldReturnAuthenticationError()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            callerScopeResolver: new FailingCallerScopeResolver());

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "caller unavailable"));
        sessions.Registered.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenChatRouteRejects_ShouldReturnRouteError()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(new ChatRouteAction
            {
                Reject = new Reject { Reason = "blocked by policy" },
            }));

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            403,
            "chat_route_rejected",
            "blocked by policy"));
        sessions.Registered.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenSessionRegistrationIsCancelled_ShouldReturnTimeout()
    {
        var sessions = new RecordingSessionPort
        {
            RegisterException = new OperationCanceledException(),
        };
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            408,
            "request_timeout",
            "Request timed out."));
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenSessionRegistrationFails_ShouldReturnApiError()
    {
        var sessions = new RecordingSessionPort
        {
            RegisterException = new InvalidOperationException("store unavailable"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Failed to register session."));
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenDirectToolPlanFails_ShouldReturnPlanError_AndNotDispatch()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var tools = new RecordingResponsesToolClassificationService();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            toolClassificationService: tools,
            directToolPlanService: new StaticResponsesDirectToolPlanService(
                ResponsesDirectToolPlan.FromError(new ResponsesCommandError(
                    500,
                    "tool_set_unavailable",
                    "tool set is unavailable"))));

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "tool_set_unavailable",
            "tool set is unavailable"));
        result.Accepted.Should().BeNull();
        result.StreamPlan.Should().BeNull();
        sessions.Registered.Should().ContainSingle();
        sessions.UpdatedStatuses.Should().BeEmpty();
        tools.Calls.Should().Be(0);
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchRequiresAuthentication_ShouldMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdAuthenticationRequiredException("test-provider"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchReturnsUpstreamError_ShouldMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdUpstreamException(
            NyxIdUpstreamFailureKind.RateLimited,
            429,
            "route-a",
            "model-a",
            "rate limited"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            429,
            "ratelimited",
            "rate limited"));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchIsCancelled_ShouldDetachWithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(ct => new OperationCanceledException(ct));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"), cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            499,
            "client_closed_request",
            "Client closed request."));
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchFails_ShouldMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new InvalidOperationException("dispatch failed"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Internal server error."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_WhenOffActorFlagIsOff_ShouldKeepLegacyRunRequestedDispatch()
    {
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_off_actor_off")
            .WithCompletedText("ok")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var executor = new BlockingLlmRunExecutor();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            observationRuntime: observation,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "gpt-4o-mini",
                OffActorLlmRunExecutorEnabled = false,
            });

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
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
                DefaultModel = "gpt-4o-mini",
                OffActorLlmRunExecutorEnabled = true,
            });

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Completed!.Completion.OutputText.Should().Be("done");
        dispatch.Calls.Should().BeEmpty();
        var admission = executor.StartAdmissions.Should().ContainSingle().Subject;
        admission.Accepted.Should().BeTrue();
        admission.CommandId.Should().StartWith("start-");
        executor.StartedRequests.Should().ContainSingle();
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnStreamPlan_WhenRequestIsStreaming()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("chrono/gpt-5-chat", stream: true), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.StreamPlan.Should().NotBeNull();
        result.Accepted.Should().BeNull();
        result.StreamPlan!.LlmRequest.Model.Should().Be("gpt-5-chat");
        result.StreamPlan.LlmRequest.LlmControl!.NyxIdRoutePreference.Should().Be("route-value");
        result.StreamPlan.LlmRequest.ToolContext.Should().NotBeNull();
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey("scope_id");
        result.StreamPlan.LlmRequest.ToolContext!.Request.RequestId.Should().Be(result.StreamPlan.Normalized.CompletionId);
        result.StreamPlan.LlmRequest.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        result.StreamPlan.LlmRequest.ToolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        result.StreamPlan.LlmRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
        sessions.Registered.Should().ContainSingle();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnCompletedObservation_AndReplayTextOnlyDeltas()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_stream")
            .WithChunkText("Hel")
            .WithChunkText("lo")
            .WithToolCallDelta("call_1", "get_weather", """{"city":"SF"}""")
            .WithCompletedText("Hello")
            .WithCompletedToolCall("call_1", "get_weather", """{"city":"SF"}""")
            .WithUsage(4, 2, 6)
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch, observationRuntime: observation);
        var deltas = new List<LlmSessionRunObservedDelta>();

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
        result.Completion.ToolCalls.Should().ContainSingle();
        result.Completion.Usage.Should().Be(new TokenUsage(4, 2, 6));
        deltas.Should().Contain(x => x.TextDelta == "Hel");
        deltas.Should().Contain(x => x.TextDelta == "lo");
        deltas.All(static x => x.TextDelta != null || x.Usage != null).Should().BeTrue();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be("actor-chatcmpl_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        call.Envelope.Propagation!.CorrelationId.Should().Be("chatcmpl_stream");
        command.ResponseId.Should().Be("chatcmpl_stream");
        command.RunId.Should().Be("chatcmpl_stream:llm-run");
        command.Model.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task CreateAsync_WhenObservedRunFails_ShouldReturnFailureError_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_fail")
            .WithFailed("provider crashed")
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch, observationRuntime: observation);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "llm_run_failed",
            "provider crashed"));
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_WhenObservedRunIsCancelled_ShouldReturnCancelledError_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_stream")
            .WithCancelled()
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch, observationRuntime: observation);

        var result = await facade.StreamAsync(BuildStreamPlan(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            409,
            "run_cancelled",
            "LLM run was cancelled."));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenObservedRunDoesNotTerminate_ShouldReturnTimeout_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_timeout")
            .WithoutTerminal()
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: observation,
            observationTimeout: TimeSpan.FromMilliseconds(50));

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(504);
        result.Error.Code.Should().Be("response_timeout");
        result.Error.Message.Should().Contain("Timed out waiting");
        result.Completed.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldHonorIngressObservationTimeout_WhenNoExplicitTimeoutGiven()
    {
        // The observation timeout is configurable via ResponsesIngressOptions (raised from the old
        // hardcoded 30s so long agentic turns aren't cut at 30s). With no explicit ctor timeout, the
        // facade must use the ingress-configured one — here 1s + a non-terminating run → 504.
        var sessions = new RecordingSessionPort();
        var observation = ObservationScenarioBuilder.ForResponse("chatcmpl_ingress_timeout")
            .WithoutTerminal()
            .Build();
        var dispatch = new RecordingActorDispatchPort(observation);
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: observation,
            observationTimeoutSeconds: 1);

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(504);
        result.Error.Code.Should().Be("response_timeout");
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchFails_ShouldReturnError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new InvalidOperationException("dispatch failed"));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: ObservationScenarioBuilder.ForResponse("chatcmpl_stream").Build());

        var result = await facade.StreamAsync(BuildStreamPlan(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Internal server error."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchRequiresAuthentication_ShouldReturnAuthenticationError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdAuthenticationRequiredException("test-provider"));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: ObservationScenarioBuilder.ForResponse("chatcmpl_stream").Build());

        var result = await facade.StreamAsync(BuildStreamPlan(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchReturnsUpstreamError_ShouldReturnMappedError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new NyxIdUpstreamException(
            NyxIdUpstreamFailureKind.RateLimited,
            429,
            "route-a",
            "model-a",
            "rate limited"));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: ObservationScenarioBuilder.ForResponse("chatcmpl_stream").Build());

        var result = await facade.StreamAsync(BuildStreamPlan(), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            429,
            "ratelimited",
            "rate limited"));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchIsCancelled_ShouldReturnClientClosedError_WithoutWritingSessionStatus()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(ct => new OperationCanceledException(ct));
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationRuntime: ObservationScenarioBuilder.ForResponse("chatcmpl_stream").Build());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.StreamAsync(BuildStreamPlan(), static (_, _) => ValueTask.CompletedTask, cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            499,
            "client_closed_request",
            "Client closed request."));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithResponseFormat_ShouldCarryFormatIntoLlmRequest()
    {
        var facade = CreateFacade();

        var result = await facade.CreateAsync(
            BuildRequest("gpt-4o-mini", stream: true, responseFormat: LLMResponseFormat.JsonObject),
            CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.StreamPlan.Should().NotBeNull();
        result.StreamPlan!.Normalized.ResponseFormat.Should().BeSameAs(LLMResponseFormat.JsonObject);
        result.StreamPlan.LlmRequest.ResponseFormat.Should().BeSameAs(LLMResponseFormat.JsonObject);
    }

    [Fact]
    public async Task CreateAsync_WithToolPayloads_ShouldWriteTypedToolArgumentsSchemaAndChoiceHint()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(GAgentToolHintAction("member-1")),
            toolClassificationService: new StaticResponsesToolClassificationService(
                new ResponsesToolClassification(
                    [
                        new ResponsesApplicationToolDeclaration(
                            "get_weather",
                            "Get weather",
                            """{"type":"object","properties":{"city":{"type":"string"}}}""",
                            "schema-1"),
                    ],
                    [],
                    [],
                    [],
                    ["get_weather"])));

        var result = await facade.CreateAsync(
            BuildRequest(
                "gpt-4o-mini",
                chatMessages:
                [
                    new ChatMessage
                    {
                        Role = "assistant",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "call-1",
                                Name = "get_weather",
                                ArgumentsJson = """{"city":"Paris"}""",
                            },
                        ],
                    },
                ],
                declaredTools:
                [
                    new ResponsesApplicationToolDeclaration(
                        "get_weather",
                        "Get weather",
                        """{"type":"object","properties":{"city":{"type":"string"}}}""",
                        "schema-1"),
                ]),
            CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Messages.Should().ContainSingle().Which.ToolCalls.Should().ContainSingle()
            .Which.Arguments.Fields["city"].StringValue.Should().Be("Paris");
        command.ToolSelection.ToolChoiceHintArguments.Fields["actor_id"].StringValue.Should().Be("member-1");
        command.ToolSelection.OwnedToolNames.Should().ContainSingle("get_weather");
        var declaration = command.ToolSelection.ForwardedTools.Should().ContainSingle().Subject;
        declaration.Parameters.Fields["type"].StringValue.Should().Be("object");
        declaration.Parameters.Fields["properties"].StructValue.Fields["city"].StructValue.Fields["type"]
            .StringValue.Should().Be("string");
    }

    public static TheoryData<ChatCompletionsCommandRequest, string> InvalidRequests() => new()
    {
        { BuildRequest(" "), "model_required" },
        { BuildRequest("gpt-4o-mini", maxTokens: 0), "invalid_max_tokens" },
        { BuildRequest("gpt-4o-mini", temperature: 3), "invalid_temperature" },
        { BuildRequest("gpt-4o-mini", chatMessages: []), "invalid_messages" },
    };

    private static ChatCompletionsCommandRequest BuildRequest(
        string model,
        bool stream = false,
        double? temperature = null,
        int? maxTokens = 100,
        IReadOnlyList<ChatMessage>? chatMessages = null,
        LLMResponseFormat? responseFormat = null,
        IReadOnlyList<ResponsesApplicationToolDeclaration>? declaredTools = null) =>
        new(
            model,
            stream,
            false,
            temperature,
            maxTokens,
            chatMessages ?? [ChatMessage.User("hello")],
            declaredTools ?? [],
            responseFormat);

    [Fact]
    public async Task CreateAsync_ShouldPersistRouteToolSetNameIntoRunCommand()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ModelName = "chrono/gpt-5-chat",
                    ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                },
            }));

        var result = await facade.CreateAsync(BuildRequest("chrono/gpt-5-chat"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        // Off-grain run re-resolves this name to re-materialize the route tool set.
        command.ToolSelection.ToolSetName.Should().Be("workspace.default");
    }

    private static ChatCompletionsCommandFacade CreateFacade(
        ILlmSessionRegistrationPort? sessionPort = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        RecordingActorDispatchPort? dispatchPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolClassificationService? toolClassificationService = null,
        IResponsesDirectToolPlanService? directToolPlanService = null,
        ObservationScenarioRuntime? observationRuntime = null,
        ILlmSessionRunObservationService? observationService = null,
        TimeSpan? observationTimeout = null,
        string? defaultIngressModel = null,
        int? observationTimeoutSeconds = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ResponsesIngressOptions? ingressOptions = null,
        ILlmRunExecutor? llmRunExecutor = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        var runtime = observationRuntime ?? ObservationScenarioBuilder.ForResponse("chatcmpl_default")
            .WithCompletedText("ok")
            .Build();
        dispatchPort?.BindObservationRuntime(runtime);
        var ingressOpts = ingressOptions;
        if (ingressOpts is null && (defaultIngressModel is not null || observationTimeoutSeconds is not null))
        {
            ingressOpts = new ResponsesIngressOptions();
            if (defaultIngressModel is not null) ingressOpts.DefaultModel = defaultIngressModel;
            if (observationTimeoutSeconds is not null) ingressOpts.ObservationTimeoutSeconds = observationTimeoutSeconds.Value;
        }
        return new ChatCompletionsCommandFacade(
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            dispatchPort ?? new RecordingActorDispatchPort(runtime),
            toolClassificationService ?? new StaticResponsesToolClassificationService(),
            directToolPlanService ?? new StaticResponsesDirectToolPlanService(),
            observationService ?? new LlmSessionRunObservationService(runtime.ScopePreparationPort, runtime.ProjectionPort),
            NullLogger<ChatCompletionsCommandFacade>.Instance,
            observationTimeout,
            ingressOpts is null ? null : Options.Create(ingressOpts),
            ownerLlmConfigSource,
            llmRunExecutor);
    }

    private static ResponsesCallerScopeResolutionContext CallerScopeContext(string bearerToken) =>
        new(bearerToken, null, null);

    private static ChatCompletionsCreateCommandPlan BuildStreamPlan() =>
        new(
            new NormalizedChatCompletionsCommand(
                "chatcmpl_stream",
                "gpt-4o-mini",
                true,
                false,
                null,
                100,
                [ChatMessage.User("hello")],
                [],
                null),
            new LlmSessionRegistrationResult("actor-chatcmpl_stream", "chatcmpl_stream"),
            new LLMRequest
            {
                RequestId = "chatcmpl_stream",
                Model = "gpt-4o-mini",
                Messages = [ChatMessage.User("hello")],
                ToolContext = BuildToolContext("chatcmpl_stream"),
            },
            new ResponsesToolClassification([], [], [], [], []),
            ResponsesToolChoiceHintPlan.Empty,
            DateTimeOffset.UtcNow);

    private static AgentToolExecutionContext BuildToolContext(string responseId) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(responseId, null),
            Credentials = new AgentToolCredentials("token", null, null),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", responseId),
            Routing = new LLMRequestRoutingContext(null, "route-value", null, null),
        };

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction GAgentToolHintAction(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ModelName = "gpt-4o-mini",
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

    private sealed class StaticCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey));
    }

    private sealed class FailingCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
            throw new ResponsesCallerScopeUnavailableException("caller unavailable");
    }

    private sealed class StaticResponsesRouteResolver(string? routeValue) : IResponsesRouteResolver
    {
        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct) =>
            Task.FromResult(routeValue);
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

    private sealed class StubOwnerLlmConfigSource(OwnerLlmConfig? config = null, bool throwOnGet = false)
        : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
        {
            if (throwOnGet)
                throw new InvalidOperationException("simulated owner-config lookup failure");
            return Task.FromResult(config ?? OwnerLlmConfig.Empty);
        }
    }

    private sealed class StaticResponsesChatRouteDecisionPort(ChatRouteAction action)
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
                UsedFallback = false,
            });
        }
    }

    private sealed class StaticResponsesToolClassificationService(
        ResponsesToolClassification? classification = null) : IResponsesToolClassificationService
    {
        public ValueTask<ResponsesToolClassification> ClassifyAsync(
            IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
            ResponsesToolProviderContext context,
            IEnumerable<IResponsesToolProvider>? additionalProviders = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(classification ?? new ResponsesToolClassification([], [], [], [], []));
    }

    private sealed class RecordingResponsesToolClassificationService : IResponsesToolClassificationService
    {
        public int Calls { get; private set; }

        public ValueTask<ResponsesToolClassification> ClassifyAsync(
            IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
            ResponsesToolProviderContext context,
            IEnumerable<IResponsesToolProvider>? additionalProviders = null,
            CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new ResponsesToolClassification([], [], [], [], []));
        }
    }

    private sealed class StaticResponsesDirectToolPlanService(
        ResponsesDirectToolPlan? plan = null) : IResponsesDirectToolPlanService
    {
        public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction) =>
            plan ?? ResponsesDirectToolPlan.Success(
                [],
                ResponsesToolChoiceHints.Create(
                    routeAction?.ForwardToModel?.ToolChoiceHint?.ToolName,
                    routeAction?.ForwardToModel?.ToolChoiceHint?.PrefilledArguments),
                routeAction?.ForwardToModel?.ToolSetRef?.Name ?? string.Empty);
    }

    private sealed class StaticLlmSessionRunObservationService(
        LlmSessionRunObservedResult result,
        IReadOnlyList<LlmSessionRunObservedDelta>? deltas = null) : ILlmSessionRunObservationService
    {
        public LlmSessionRunObservationRequest? LastRequest { get; private set; }

        public static StaticLlmSessionRunObservationService Completed(string outputText) =>
            new(
                new LlmSessionRunObservedResult(
                    null,
                    new LlmSessionCompletionSnapshot(outputText, [], DateTimeOffset.UtcNow, null, null),
                    null),
                [new LlmSessionRunObservedDelta(outputText, null)]);

        public async Task<LlmSessionRunObservedResult> ObserveAsync(
            LlmSessionRunObservationRequest request,
            Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var admission = await request.DispatchAsync(ct);
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
        public List<LlmRunExecutorRequest> StartedRequests { get; } = [];

        public List<DispatchAdmission> StartAdmissions { get; } = [];

        public TaskCompletionSource ExecuteStarted { get; } =
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

        public Task ExecuteAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            ExecuteStarted.SetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly Func<CancellationToken, Exception?> _exceptionFactory;
        private ObservationScenarioRuntime? _observationRuntime;

        public RecordingActorDispatchPort()
            : this((ObservationScenarioRuntime?)null)
        {
        }

        public RecordingActorDispatchPort(ObservationScenarioRuntime? observationRuntime)
            : this(static _ => null, observationRuntime)
        {
        }

        public RecordingActorDispatchPort(Func<CancellationToken, Exception?> exceptionFactory)
            : this(exceptionFactory, null)
        {
        }

        public RecordingActorDispatchPort(
            Func<CancellationToken, Exception?> exceptionFactory,
            ObservationScenarioRuntime? observationRuntime)
        {
            _exceptionFactory = exceptionFactory;
            _observationRuntime = observationRuntime;
        }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public void BindObservationRuntime(ObservationScenarioRuntime observationRuntime)
        {
            _observationRuntime ??= observationRuntime;
        }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (_exceptionFactory(ct) is { } exception)
                return Task.FromException<DispatchAdmission>(exception);

            Calls.Add((actorId, envelope));
            _observationRuntime?.PublishAll();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ObservationScenarioRuntime
    {
        private readonly IReadOnlyList<EventEnvelope> _events;

        public ObservationScenarioRuntime(
            string actorId,
            string responseId,
            IReadOnlyList<EventEnvelope> events)
        {
            ActorId = actorId;
            ResponseId = responseId;
            _events = events;
            ScopePreparationPort = new StubObservationScopeLeasePreparationPort(actorId, responseId);
            ProjectionPort = new StubObservationProjectionPort(actorId, responseId);
        }

        public string ActorId { get; }

        public string ResponseId { get; }

        public StubObservationScopeLeasePreparationPort ScopePreparationPort { get; }

        public StubObservationProjectionPort ProjectionPort { get; }

        public void PublishAll()
        {
            foreach (var envelope in _events)
                ProjectionPort.Sink?.Push(envelope);
        }
    }

    private sealed class ObservationScenarioBuilder
    {
        private enum ObservationTerminalState
        {
            Completed,
            Failed,
            Cancelled,
            None,
        }

        private readonly string _responseId;
        private readonly List<EventEnvelope> _events = [];
        private TokenUsage? _usage;
        private string _outputText = string.Empty;
        private readonly List<LlmSessionRuntimeToolCall> _completedToolCalls = [];
        private ObservationTerminalState _terminalState = ObservationTerminalState.Completed;
        private string? _failureMessage;

        private ObservationScenarioBuilder(string responseId)
        {
            _responseId = responseId;
        }

        public static ObservationScenarioBuilder ForResponse(string responseId) => new(responseId);

        public ObservationScenarioBuilder WithChunkText(string deltaText)
        {
            _events.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new LlmStreamChunkObserved
                {
                    ResponseId = _responseId,
                    RunId = $"{_responseId}:llm-run",
                    DeltaText = deltaText,
                }),
            });
            return this;
        }

        public ObservationScenarioBuilder WithToolCallDelta(string callId, string toolName, string argumentsJson)
        {
            _events.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new LlmStreamChunkObserved
                {
                    ResponseId = _responseId,
                    RunId = $"{_responseId}:llm-run",
                    ToolCallDelta = new LlmSessionRuntimeToolCall
                    {
                        CallId = callId,
                        ToolName = toolName,
                        ArgumentsJson = argumentsJson,
                    },
                }),
            });
            return this;
        }

        public ObservationScenarioBuilder WithCompletedText(string outputText)
        {
            _outputText = outputText;
            return this;
        }

        public ObservationScenarioBuilder WithFailed(string failureMessage)
        {
            _terminalState = ObservationTerminalState.Failed;
            _failureMessage = failureMessage;
            return this;
        }

        public ObservationScenarioBuilder WithCancelled()
        {
            _terminalState = ObservationTerminalState.Cancelled;
            _failureMessage = null;
            return this;
        }

        public ObservationScenarioBuilder WithoutTerminal()
        {
            _terminalState = ObservationTerminalState.None;
            _failureMessage = null;
            return this;
        }

        public ObservationScenarioBuilder WithCompletedToolCall(string callId, string toolName, string resultJson)
        {
            _completedToolCalls.Add(new LlmSessionRuntimeToolCall
            {
                CallId = callId,
                ToolName = toolName,
                ArgumentsJson = resultJson,
            });
            return this;
        }

        public ObservationScenarioBuilder WithUsage(int promptTokens, int completionTokens, int totalTokens)
        {
            _usage = new TokenUsage(promptTokens, completionTokens, totalTokens);
            return this;
        }

        public ObservationScenarioRuntime Build()
        {
            switch (_terminalState)
            {
                case ObservationTerminalState.Completed:
                    var completed = new LlmRunCompleted
                    {
                        ResponseId = _responseId,
                        RunId = $"{_responseId}:llm-run",
                        OutputText = _outputText,
                        CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    };
                    completed.ForwardedToolCalls.AddRange(_completedToolCalls.Select(static call => call.Clone()));
                    if (_usage is not null)
                    {
                        completed.Usage = new LlmSessionTokenUsage
                        {
                            PromptTokens = _usage.PromptTokens,
                            CompletionTokens = _usage.CompletionTokens,
                            TotalTokens = _usage.TotalTokens,
                        };
                    }

                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(completed),
                    });
                    break;
                case ObservationTerminalState.Failed:
                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(new LlmRunFailed
                        {
                            ResponseId = _responseId,
                            RunId = $"{_responseId}:llm-run",
                            FailureMessage = _failureMessage ?? "LLM run failed.",
                            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        }),
                    });
                    break;
                case ObservationTerminalState.Cancelled:
                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(new LlmRunCancelled
                        {
                            ResponseId = _responseId,
                            RunId = $"{_responseId}:llm-run",
                            CancelledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        }),
                    });
                    break;
                case ObservationTerminalState.None:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported terminal state '{_terminalState}'.");
            }

            return new ObservationScenarioRuntime(
                actorId: "actor-" + _responseId,
                responseId: _responseId,
                events: _events.ToArray());
        }
    }

    private sealed class StubObservationScopeLeasePreparationPort(string actorId, string responseId)
        : ILlmSessionObservationScopeLeasePreparationPort
    {
        public Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
            string requestedActorId,
            string requestedResponseId,
            CancellationToken ct = default) =>
            Task.FromResult<LlmSessionObservationScopeLeasePreparation?>(
                new LlmSessionObservationScopeLeasePreparation(actorId, responseId));

        public Task ReleaseAsync(
            LlmSessionObservationScopeLeasePreparation preparation,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubObservationProjectionPort(string actorId, string responseId)
        : ILlmSessionObservationProjectionPort
    {
        public IEventSink<EventEnvelope>? Sink { get; private set; }

        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
            string requestedActorId,
            string requestedResponseId,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            Sink = sink;
            return Task.FromResult<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>(
                    new StubObservationProjectionLease(actorId, responseId),
                    new NoOpAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            ILlmSessionObservationProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(new NoOpAsyncDisposable());

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            Sink = null;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            ILlmSessionObservationProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record StubObservationProjectionLease(string ActorId, string ResponseId)
        : ILlmSessionObservationProjectionLease;

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public Exception? RegisterException { get; init; }

        public Task<LlmSessionRegistrationResult> RegisterAsync(LlmSessionRecord record, CancellationToken ct = default)
        {
            if (RegisterException is not null)
                return Task.FromException<LlmSessionRegistrationResult>(RegisterException);

            Registered.Add(record);
            return Task.FromResult(new LlmSessionRegistrationResult("actor-" + record.ResponseId, record.ResponseId));
        }

        public Task UpdateStatusAsync(string sessionActorId, string responseId, LlmSessionStatus status, CancellationToken ct = default)
        {
            UpdatedStatuses.Add((sessionActorId, responseId, status));
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(
            string sessionActorId,
            string responseId,
            string runId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ResolveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
