using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
            routeResolver: new StaticResponsesRouteResolver("route-value"),
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
        result.Accepted.Should().NotBeNull();
        result.Completed.Should().BeNull();
        result.Accepted!.Admission.Accepted.Should().BeTrue();
        sessions.Registered.Should().ContainSingle().Which.ResponseId.Should().StartWith("resp_");
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5");
        command.RoutePreference.Should().Be("route-value");
        command.ScopeId.Should().Be("scope-1");
        command.BearerToken.Should().Be("token");
        result.Accepted.Admission.CommandId.Should().NotBeNullOrWhiteSpace();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        toolContext.Request.RequestId.Should().Be(command.ResponseId);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Caller.ResponseId.Should().Be(command.ResponseId);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_WhenFallbackRouteHasModel_ShouldPreserveExplicitPrefixedRequestModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver("/api/v1/proxy/s/chrono-llm"),
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
        result.Accepted.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.4-mini");
        command.RoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task CreateAsync_WhenDefaultRouteHasModel_ShouldPreserveExplicitPrefixedRequestModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            routeResolver: new StaticResponsesRouteResolver("/api/v1/proxy/s/chrono-llm"),
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
        result.Accepted.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.4-mini");
        command.RoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task CreateAsync_WhenRoutePinsGAgentTool_ShouldRegisterSessionAndExecuteThroughLlm()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(GAgentToolHintAction("member-1")),
            toolSetRegistry: new StaticToolSetRegistry([
                new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"),
            ]));

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
        result.Accepted.Should().NotBeNull();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_gagent");
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
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(action),
            toolSetRegistry: new StaticToolSetRegistry([
                new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"),
            ]));

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
        result.Accepted.Should().NotBeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_gagent");
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
        result.Accepted.Should().BeNull();
        result.Completed.Should().BeNull();
        result.StreamPlan.Should().BeNull();
        sessions.Registered.Should().BeEmpty();
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
        sessions.RecordedToolCalls.Should().BeEmpty();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_ShouldRejectInvisibleResponse_AndUpdateVisibleResponse()
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
        sessionPort.UpdatedStatuses.Should().ContainSingle(update => update.Status == LlmSessionStatus.Cancelled);
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
            UpdateStatusException = new InvalidOperationException("cannot cancel completed response"),
        };
        var facade = CreateFacade(sessionPort: sessionPort, queryPort: queryPort);

        var result = await facade.CancelAsync("resp_active", CallerScopeContext("token"));

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            400,
            "response_cancel_rejected",
            "cannot cancel completed response"));
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
            routeResolver: new StaticResponsesRouteResolver("route-value"),
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
        result.StreamPlan.LlmRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
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
    public async Task StreamAsync_ShouldReturnTimeout_AndMarkSessionCancelled()
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
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Cancelled);
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

    private static ResponsesCommandFacade CreateFacade(
        IResponsesCompletionApplicationService? completionService = null,
        ILlmSessionRegistrationPort? sessionPort = null,
        ILlmSessionQueryPort? queryPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        IToolSetRegistry? toolSetRegistry = null,
        IActorDispatchPort? dispatchPort = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new ResponsesCommandFacade(
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            routeResolver ?? new StaticResponsesRouteResolver(null),
            effectiveSessionPort,
            queryPort ?? (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            dispatchPort ?? new RecordingActorDispatchPort(),
            new ResponsesToolClassificationService([], NullLogger<ResponsesToolClassificationService>.Instance),
            new ResponsesDirectToolPlanService(toolSetRegistry ?? new EmptyToolSetRegistry()),
            NullLogger<ResponsesCommandFacade>.Instance);
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
            new ResponsesToolClassification([], [], [], []),
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
        LlmSessionStatus status = LlmSessionStatus.Accepted) =>
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
            "event-1");

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ResponsesCallerScopeResolutionContext CallerScopeContext(string bearerToken) =>
        new(bearerToken, null, null);

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

    private sealed class StaticResponsesRouteResolver(string? routeValue) : IResponsesRouteResolver
    {
        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct) =>
            Task.FromResult(routeValue);
    }

    private sealed class StaticResponsesChatRouteDecisionPort(
        ChatRouteAction action,
        bool usedFallback = false,
        string matchedRuleId = "")
        : IResponsesChatRouteDecisionPort
    {
        public Task<ChatRouteDecision> ResolveAsync(
            ResponsesCallerScope callerScope,
            string model,
            ToolMode toolMode,
            string contentHint,
            CancellationToken ct = default)
            => Task.FromResult(new ChatRouteDecision
            {
                Action = action.Clone(),
                UsedFallback = usedFallback,
                MatchedRuleId = matchedRuleId,
            });
    }

    private sealed class EmptyToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                toolSetRef?.Name ?? string.Empty,
                $"Tool set '{toolSetRef?.Name}' is not registered.",
                []));
    }

    private sealed class StaticToolSetRegistry(IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["workspace.default"];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            ToolSetResolveResult.Success(
                toolSetRef?.Name ?? "workspace.default",
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

    private sealed class StaticLlmProviderFactory : ILLMProviderFactory
    {
        private readonly ILLMProvider _provider = new StaticLlmProvider();

        public ILLMProvider GetProvider(string name) => _provider;

        public ILLMProvider GetDefault() => _provider;

        public IReadOnlyList<string> GetAvailableProviders() => [_provider.Name];
    }

    private sealed class StaticLlmProvider : ILLMProvider
    {
        public string Name => "test";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LLMStreamChunk { DeltaContent = "unused", IsLast = true };
        }
    }

    private sealed class RecordingCompletionService(
        ResponsesCompletionResult result,
        Func<CancellationToken, Exception>? streamExceptionFactory = null,
        ToolCall? hintProbeCall = null) : IResponsesCompletionApplicationService
    {
        public LLMRequest? LastRequest { get; private set; }

        public ResponsesToolClassification? LastToolClassification { get; private set; }

        public ToolCall? LastHintProbeResult { get; private set; }

        public Task<ResponsesCompletionResult> CollectAsync(
            ILLMProvider provider,
            LLMRequest request,
            AgentToolExecutionContext toolContext,
            ResponsesToolClassification toolClassification,
            CancellationToken ct = default)
        {
            LastRequest = request;
            LastToolClassification = toolClassification;
            if (hintProbeCall is not null)
                LastHintProbeResult = ResponsesToolContext.ApplyToolChoiceHint(hintProbeCall);
            return Task.FromResult(result);
        }

        public Task<ResponsesCompletionResult> StreamAsync(
            ILLMProvider provider,
            LLMRequest request,
            AgentToolExecutionContext toolContext,
            ResponsesToolClassification toolClassification,
            Func<string, CancellationToken, ValueTask> onTextDelta,
            CancellationToken ct = default)
        {
            LastRequest = request;
            LastToolClassification = toolClassification;
            if (hintProbeCall is not null)
                LastHintProbeResult = ResponsesToolContext.ApplyToolChoiceHint(hintProbeCall);
            if (streamExceptionFactory?.Invoke(ct) is { } ex)
                throw ex;
            return Task.FromResult(result);
        }
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

        public List<LlmSessionForwardedToolCall> RecordedToolCalls { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId, string SchemaHash, string ResultJson)> ToolResults { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId)> ResolvedToolResults { get; } = [];

        public RecordingSessionQueryPort QueryPort { get; } = new();

        public Exception? UpdateStatusException { get; init; }

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

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default)
        {
            RecordedToolCalls.Add(call);
            return Task.CompletedTask;
        }

        public Task<DispatchAdmission> RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            RecordedCompletions.Add(completion.Clone());
            QueryPort.Snapshot = QueryPort.Snapshot is null
                ? BuildSnapshot(responseId, "scope-1", LlmSessionStatus.Completed) with { Completion = ToSnapshot(completion) }
                : QueryPort.Snapshot with { Completion = ToSnapshot(completion) };
            return Task.FromResult(DispatchAdmissionFactory.Create(
                sessionActorId,
                new EventEnvelope { Id = $"{responseId}:completion" }));
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

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(string responseId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
    }

}
