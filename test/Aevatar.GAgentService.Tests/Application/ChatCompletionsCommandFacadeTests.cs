using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ChatCompletionsCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndDispatchLlmRun()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("chrono/gpt-4o-mini"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Accepted!.Admission.Accepted.Should().BeTrue();
        sessions.Registered.Should().ContainSingle().Which.ScopeId.Should().Be("scope-1");
        sessions.RecordedCompletions.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(result.Accepted.Session.ActorId);
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be(result.Accepted.Session.ResponseId);
        command.RunId.Should().Be($"{result.Accepted.Session.ResponseId}:llm-run");
        command.Model.Should().Be("gpt-4o-mini");
        command.ScopeId.Should().Be("scope-1");
        command.BearerToken.Should().Be("token");
        command.Messages.Should().ContainSingle().Which.Content.Should().Be("hello");
        var toolContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        toolContext.Request.RequestId.Should().Be(command.ResponseId);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Caller.ResponseId.Should().Be(command.ResponseId);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
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
    public async Task CreateAsync_WhenDispatchIsCancelled_ShouldMarkSessionCancelled()
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
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Cancelled);
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
    public async Task StreamAsync_ShouldReturnAcceptedDispatchReceipt()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan());

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Completion.Should().BeNull();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be("actor-chatcmpl_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be("chatcmpl_stream");
        command.RunId.Should().Be("chatcmpl_stream:llm-run");
        command.Model.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchFails_ShouldReturnError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(_ => new InvalidOperationException("dispatch failed"));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), CancellationToken.None);

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
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_required",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        result.Accepted.Should().BeNull();
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
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), CancellationToken.None);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            429,
            "ratelimited",
            "rate limited"));
        result.Accepted.Should().BeNull();
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_WhenDispatchIsCancelled_ShouldReturnClientClosedError_AndMarkSessionCancelled()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(ct => new OperationCanceledException(ct));
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.StreamAsync(BuildStreamPlan(), cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            499,
            "client_closed_request",
            "Client closed request."));
        result.Accepted.Should().BeNull();
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Cancelled);
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
                    [])));

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

    private static ChatCompletionsCommandFacade CreateFacade(
        ILlmSessionRegistrationPort? sessionPort = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        RecordingActorDispatchPort? dispatchPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolClassificationService? toolClassificationService = null,
        IResponsesDirectToolPlanService? directToolPlanService = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new ChatCompletionsCommandFacade(
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            dispatchPort ?? new RecordingActorDispatchPort(),
            toolClassificationService ?? new StaticResponsesToolClassificationService(),
            directToolPlanService ?? new StaticResponsesDirectToolPlanService(),
            NullLogger<ChatCompletionsCommandFacade>.Instance);
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
            new ResponsesToolClassification([], [], [], []),
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

    private sealed class StaticResponsesChatRouteDecisionPort(ChatRouteAction action)
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
                UsedFallback = false,
            });
    }

    private sealed class StaticResponsesToolClassificationService(
        ResponsesToolClassification? classification = null) : IResponsesToolClassificationService
    {
        public ValueTask<ResponsesToolClassification> ClassifyAsync(
            IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
            ResponsesToolProviderContext context,
            IEnumerable<IResponsesToolProvider>? additionalProviders = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(classification ?? new ResponsesToolClassification([], [], [], []));
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
            return ValueTask.FromResult(new ResponsesToolClassification([], [], [], []));
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
                    routeAction?.ForwardToModel?.ToolChoiceHint?.PrefilledArguments));
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly Func<CancellationToken, Exception?> _exceptionFactory;

        public RecordingActorDispatchPort()
            : this(static _ => null)
        {
        }

        public RecordingActorDispatchPort(Func<CancellationToken, Exception?> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory;
        }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (_exceptionFactory(ct) is { } exception)
                return Task.FromException<DispatchAdmission>(exception);

            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
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

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<DispatchAdmission> RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            RecordedCompletions.Add(completion.Clone());
            return Task.FromResult(new DispatchAdmission(
                true,
                $"{responseId}:completion",
                DateTimeOffset.UtcNow,
                sessionActorId,
                $"{responseId}:completion"));
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
