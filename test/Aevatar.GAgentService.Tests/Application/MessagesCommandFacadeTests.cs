using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
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
        result.Completed!.Completion.Text.Should().Be("hello");
        sessions.Registered.Should().ContainSingle().Which.PreviousResponseId.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Completed);
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
    public async Task CreateAsync_ShouldRejectForwardToGAgentRoute()
    {
        var facade = CreateFacade(policyPort: new StaticChatRoutePolicyQueryPort(new ChatRouteAction
        {
            ForwardToGagent = new ForwardToGAgent { ActorId = "member-1" },
        }));

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            501,
            "chat_route_action_not_supported",
            "ForwardToGAgent is not supported by /v1/messages in v1."));
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
        IChatRoutePolicyQueryPort? policyPort = null) =>
        new(
            new StaticCallerScopeResolver(),
            policyPort ?? new StaticChatRoutePolicyQueryPort(ForwardToModelAction(string.Empty)),
            new ChatRouteResolver(new StaticChatRouteFallbackProvider(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            sessionPort ?? new RecordingSessionPort(),
            completionService ?? new RecordingCompletionService(new ResponsesCompletionResult("ok", null, [])),
            new StaticLlmProviderFactory(),
            NullLogger<MessagesCommandFacade>.Instance);

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
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

    private sealed class StaticChatRoutePolicyQueryPort(ChatRouteAction action) : IChatRoutePolicyQueryPort
    {
        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(OwnerScope callerScope, CancellationToken ct = default) =>
            Task.FromResult<ChatRoutePolicySnapshot?>(new ChatRoutePolicySnapshot(action, []));
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = ForwardToModelAction(modelName),
            UsedFallback = true,
        };
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

    private sealed class RecordingCompletionService(ResponsesCompletionResult result) : IResponsesCompletionApplicationService
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
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

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
