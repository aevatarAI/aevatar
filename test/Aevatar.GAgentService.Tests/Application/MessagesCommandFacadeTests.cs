using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class MessagesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndExecuteAnthropicDefaultRoute()
    {
        var completion = new RecordingCompletionService(new ResponsesCompletionResult("hello", null, []));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeNull();
        result.Completed!.Completion.OutputText.Should().Be("hello");
        sessions.Registered.Should().ContainSingle().Which.PreviousResponseId.Should().BeEmpty();
        sessions.RecordedCompletions.Should().ContainSingle().Which.OutputText.Should().Be("hello");
        sessions.UpdatedStatuses.Should().BeEmpty();
        completion.LastRequest!.Model.Should().Be("claude-sonnet");
        completion.LastRequest.Messages.Should().ContainSingle(message => message.Role == "user" && message.Content == "hello");
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnStreamPlan_WhenRequestIsStreaming()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("anthropic/claude", stream: true), "token");

        result.Error.Should().BeNull();
        result.StreamPlan.Should().NotBeNull();
        result.Completed.Should().BeNull();
        result.StreamPlan!.LlmRequest.Model.Should().Be("claude");
        sessions.Registered.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateAsync_WhenRouteCarriesToolSet_ShouldAddRouteTools()
    {
        var completion = new RecordingCompletionService(new ResponsesCompletionResult("ok", null, []));
        var facade = CreateFacade(
            completionService: completion,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ToolSetRouteAction("workspace.default")),
            toolSetRegistry: new StaticToolSetRegistry([
                new StubAgentTool("use_skill", "Use a skill"),
                new StubAgentTool("ornn_search_skills", "Search skills"),
            ]));

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeNull();
        completion.LastRequest.Should().NotBeNull();
        completion.LastRequest!.Tools!.Select(static tool => tool.Name)
            .Should().Contain(["use_skill", "ornn_search_skills"]);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAuthenticationError_AndMarkSessionFailed()
    {
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: _ => new NyxIdAuthenticationRequiredException("test-provider"));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_error",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnUpstreamError_AndMarkSessionFailed()
    {
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: _ => new NyxIdUpstreamException(
                NyxIdUpstreamFailureKind.ServiceUnavailable,
                503,
                "route-a",
                "claude-sonnet",
                "service unavailable"));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            503,
            "serviceunavailable",
            "service unavailable"));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnClientClosedRequest_AndMarkSessionCancelled()
    {
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: ct => new OperationCanceledException(ct));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask, cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            499,
            "client_closed_request",
            "Client closed request."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Cancelled);
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnApiError_AndMarkSessionFailed()
    {
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: _ => new InvalidOperationException("provider crashed"));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Internal server error."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    private static MessagesCommandRequest BuildRequest(string model, bool stream = false) =>
        new(
            model,
            100,
            [ChatMessage.User("hello")],
            [],
            false,
            null,
            null,
            null,
            null,
            stream,
            false,
            null);

    private static MessagesCommandFacade CreateFacade(
        IResponsesCompletionApplicationService? completionService = null,
        ILlmSessionRegistrationPort? sessionPort = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        IToolSetRegistry? toolSetRegistry = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new MessagesCommandFacade(
            new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            completionService ?? new RecordingCompletionService(new ResponsesCompletionResult("ok", null, [])),
            new ResponsesToolClassificationService([], NullLogger<ResponsesToolClassificationService>.Instance),
            new ResponsesDirectToolPlanService(toolSetRegistry ?? new EmptyToolSetRegistry()),
            new StaticLlmProviderFactory(),
            NullLogger<MessagesCommandFacade>.Instance);
    }

    private static MessagesCreateCommandPlan BuildStreamPlan() =>
        new(
            new NormalizedMessagesRequest(
                "msg_stream",
                "claude-sonnet",
                100,
                true,
                null,
                [ChatMessage.User("hello")],
                [],
                false),
            new LlmSessionRegistrationResult("actor-msg_stream", "msg_stream"),
            new LLMRequest
            {
                RequestId = "msg_stream",
                Model = "claude-sonnet",
                Messages = [ChatMessage.User("hello")],
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResponsesToolClassification([], [], [], []),
            ResponsesToolChoiceHintPlan.Empty);

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction ToolSetRouteAction(string toolSetName) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ModelName = string.Empty,
            ToolSetRef = new ChatRouteToolSetRef { Name = toolSetName },
        },
    };

    private sealed class StaticCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(string nyxIdAccessToken, CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey));
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

    private sealed class StubAgentTool(string name, string description) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

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
        Func<CancellationToken, Exception>? streamExceptionFactory = null) : IResponsesCompletionApplicationService
    {
        public LLMRequest? LastRequest { get; private set; }

        public Task<ResponsesCompletionResult> CollectAsync(
            ILLMProvider provider,
            LLMRequest request,
            IReadOnlyDictionary<string, string> toolContextMetadata,
            ResponsesToolClassification toolClassification,
            CancellationToken ct = default)
        {
            LastRequest = request;
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
            if (streamExceptionFactory?.Invoke(ct) is { } ex)
                throw ex;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public RecordingSessionQueryPort QueryPort { get; } = new();

        public Task<LlmSessionRegistrationResult> RegisterAsync(LlmSessionRecord record, CancellationToken ct = default)
        {
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

        public Task RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            RecordedCompletions.Add(completion.Clone());
            QueryPort.Snapshot = QueryPort.Snapshot is null
                ? BuildSnapshot(responseId, completion)
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

        private static LlmSessionSnapshot BuildSnapshot(string responseId, LlmSessionCompletion completion) =>
            new(
                responseId,
                "scope-1",
                "owner-1",
                LlmSessionOriginKind.ApiKey,
                null,
                LlmSessionStatus.Completed,
                DateTimeOffset.UtcNow,
                TimeSpan.FromHours(1),
                null,
                "actor-" + responseId,
                1,
                "event-1",
                Completion: ToSnapshot(completion));

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
