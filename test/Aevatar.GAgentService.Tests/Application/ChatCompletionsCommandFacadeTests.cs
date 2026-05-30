using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
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

        var result = await facade.CreateAsync(BuildRequest("gpt-4o-mini"), CallerScopeContext("token"));

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
        sessions.Registered.Should().ContainSingle();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAcceptedDispatchReceipt()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

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

    private static ChatCompletionsCommandRequest BuildRequest(string model, bool stream = false) =>
        new(
            model,
            stream,
            false,
            null,
            100,
            [ChatMessage.User("hello")],
            []);

    private static ChatCompletionsCommandFacade CreateFacade(
        ILlmSessionRegistrationPort? sessionPort = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        RecordingActorDispatchPort? dispatchPort = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new ChatCompletionsCommandFacade(
            new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            dispatchPort ?? new RecordingActorDispatchPort(),
            new StaticResponsesToolClassificationService(),
            new StaticResponsesDirectToolPlanService(),
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
                []),
            new LlmSessionRegistrationResult("actor-chatcmpl_stream", "chatcmpl_stream"),
            new LLMRequest
            {
                RequestId = "chatcmpl_stream",
                Model = "gpt-4o-mini",
                Messages = [ChatMessage.User("hello")],
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResponsesToolClassification([], [], [], []),
            ResponsesToolChoiceHintPlan.Empty,
            DateTimeOffset.UtcNow);

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private sealed class StaticCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default) =>
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

    private sealed class StaticResponsesToolClassificationService : IResponsesToolClassificationService
    {
        public ValueTask<ResponsesToolClassification> ClassifyAsync(
            IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
            ResponsesToolProviderContext context,
            IEnumerable<IResponsesToolProvider>? additionalProviders = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(new ResponsesToolClassification([], [], [], []));
    }

    private sealed class StaticResponsesDirectToolPlanService : IResponsesDirectToolPlanService
    {
        public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction) =>
            ResponsesDirectToolPlan.Empty;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

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
