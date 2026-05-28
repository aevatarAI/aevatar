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
        sessions.Registered.Should().ContainSingle().Which.ResponseId.Should().StartWith("resp_");
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5");
        command.RoutePreference.Should().Be("route-value");
        command.ScopeId.Should().Be("scope-1");
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
        sessions.RecordedToolCalls.Should().BeEmpty("tool set tools execute locally and are not client-forwarded tools");
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

    private static ResponsesCallerScopeResolutionContext CallerScopeContext(string bearerToken) =>
        new(bearerToken, null, null);

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
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResponsesToolClassification([], [], [], []),
            ResponsesToolChoiceHintPlan.Empty,
            DateTimeOffset.UtcNow);

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

    private sealed class StaticCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey));
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
            IReadOnlyDictionary<string, string> toolContextMetadata,
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
            IReadOnlyDictionary<string, string> toolContextMetadata,
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

        public Task RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            RecordedCompletions.Add(completion.Clone());
            QueryPort.Snapshot = QueryPort.Snapshot is null
                ? BuildSnapshot(responseId, "scope-1", LlmSessionStatus.Completed) with { Completion = ToSnapshot(completion) }
                : QueryPort.Snapshot with { Completion = ToSnapshot(completion) };
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
