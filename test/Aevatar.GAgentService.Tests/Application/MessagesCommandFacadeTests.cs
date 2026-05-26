using Aevatar.AI.Abstractions.LLMProviders;
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

public sealed class MessagesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndExecuteAnthropicDefaultRoute()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeNull();
        result.Completed!.Completion.OutputText.Should().Be("hello");
        sessions.Registered.Should().ContainSingle().Which.PreviousResponseId.Should().BeEmpty();
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("hello");
    }

    [Fact]
    public async Task CreateAsync_WhenCompletionIsCommittedButNotObserved_ShouldReturnServiceUnavailable()
    {
        var sessions = new RecordingSessionPort
        {
            ObserveCompletionInQueryPort = false,
        };
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("hello");
        result.Completed.Should().BeNull();
        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            503,
            "response_completion_not_observed",
            "Response completion was committed but is not yet visible in the read model."));
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
        var facade = CreateFacade(chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(new ChatRouteAction
        {
            ForwardToGagent = new ForwardToGAgent { ActorId = "direct-actor-1" },
        }));

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            501,
            "chat_route_action_not_supported",
            "ForwardToGAgent is not supported by /v1/messages in v1."));
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectForwardToStudioMemberRoute()
    {
        var facade = CreateFacade(chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(new ChatRouteAction
        {
            ForwardToStudioMember = new ForwardToStudioMember { MemberId = "member-1" },
        }));

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), "token");

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            501,
            "chat_route_action_not_supported",
            "ForwardToStudioMember is not supported by /v1/messages in v1."));
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnAuthenticationError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        sessions.NextDispatchCompletion = Failure("authentication_required", "NyxID authentication required for provider 'test-provider'. Please sign in.");
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            401,
            "authentication_error",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldRecordCompletionFact_AndReturnSessionCompletion()
    {
        var sessions = new RecordingSessionPort();
        sessions.NextDispatchCompletion = Completion("message stream done", new TokenUsage(2, 3, 5));
        sessions.QueryPort.Snapshot = BuildSnapshot("msg_stream");
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("message stream done");
        result.Completion.Usage.Should().Be(new TokenUsage(2, 3, 5));
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("message stream done");
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnUpstreamError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        sessions.NextDispatchCompletion = Failure("serviceunavailable", "service unavailable");
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            502,
            "serviceunavailable",
            "service unavailable"));
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnClientClosedRequest_AndMarkSessionCancelled()
    {
        var sessions = new RecordingSessionPort();
        sessions.NextDispatchCompletion = Failure("client_closed_request", "Client closed request.");
        var facade = CreateFacade(sessionPort: sessions);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask, cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            499,
            "client_closed_request",
            "Client closed request."));
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnApiError_AndMarkSessionFailed()
    {
        var sessions = new RecordingSessionPort();
        sessions.NextDispatchCompletion = Failure("api_error", "Internal server error.");
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "api_error",
            "Internal server error."));
        sessions.UpdatedStatuses.Should().BeEmpty();
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
        ILlmSessionRegistrationPort? sessionPort = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort(effectiveSessionPort as RecordingSessionPort);
        return new MessagesCommandFacade(
            new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            dispatch,
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
            new ResponsesToolClassification([], [], [], []));

    private static LlmSessionSnapshot BuildSnapshot(string responseId) =>
        new(
            responseId,
            "scope-1",
            "owner-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Accepted,
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            null,
            "actor-" + responseId,
            1,
            "event-1");

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

    private sealed class RecordingActorDispatchPort(RecordingSessionPort? sessions) : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (sessions is not null && envelope.Payload.Is(LlmRunRequested.Descriptor))
            {
                var command = envelope.Payload.Unpack<LlmRunRequested>();
                sessions.RecordCompletionAsync(
                    actorId,
                    command.ResponseId,
                    sessions.NextDispatchCompletion?.Clone()
                    ?? Completion("hello"),
                    ct);
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingSessionPort : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> UpdatedStatuses { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public bool ObserveCompletionInQueryPort { get; init; } = true;

        public LlmSessionCompletion? NextDispatchCompletion { get; set; }

        public RecordingSessionQueryPort QueryPort { get; } = new();

        public Task<LlmSessionRegistrationResult> RegisterAsync(LlmSessionRecord record, CancellationToken ct = default)
        {
            Registered.Add(record);
            var actorId = "actor-" + record.ResponseId;
            QueryPort.Snapshot = new LlmSessionSnapshot(
                record.ResponseId,
                record.ScopeId,
                record.OwnerSubject,
                record.OriginKind,
                string.IsNullOrWhiteSpace(record.PreviousResponseId) ? null : record.PreviousResponseId,
                record.Status,
                record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
                record.Ttl?.ToTimeSpan() ?? TimeSpan.FromHours(1),
                record.CancelledAt?.ToDateTimeOffset(),
                actorId,
                1,
                $"{record.ResponseId}:registered");
            return Task.FromResult(new LlmSessionRegistrationResult(actorId, record.ResponseId));
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
            var current = QueryPort.Snapshot;
            current ??= BuildSnapshot(responseId) with
            {
                ActorId = sessionActorId,
            };
            if (current is not null && ObserveCompletionInQueryPort)
            {
                QueryPort.Snapshot = current with
                {
                    Status = string.IsNullOrWhiteSpace(completion.FailureCode)
                        ? LlmSessionStatus.Completed
                        : LlmSessionStatus.Failed,
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:completion",
                    Completion = ToSnapshot(completion),
                };
            }
            return Task.CompletedTask;
        }

        private static LlmSessionCompletionSnapshot ToSnapshot(LlmSessionCompletion completion) =>
            new(
                completion.OutputText ?? string.Empty,
                completion.ToolCalls
                    .Select(static call => new LlmSessionCompletedToolCallSnapshot(
                        call.CallId,
                        call.ToolName,
                        ResponsesJsonValues.ToBoundaryJson(call.Result)))
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

    private static LlmSessionCompletion Completion(string outputText, TokenUsage? usage = null) =>
        new()
        {
            OutputText = outputText,
            CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Usage = usage is null
                ? null
                : new LlmSessionTokenUsage
                {
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                },
        };

    private static LlmSessionCompletion Failure(string code, string message) =>
        new()
        {
            FailureCode = code,
            FailureMessage = message,
            CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed class RecordingSessionQueryPort : ILlmSessionQueryPort
    {
        public LlmSessionSnapshot? Snapshot { get; set; }

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(string responseId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
    }
}
