using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndExecuteRoutedNonStreamingRequest()
    {
        var completion = new RecordingCompletionService(new ResponsesCompletionResult(
            "done",
            new TokenUsage(1, 2, 3),
            []));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            completionService: completion,
            sessionPort: sessions,
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
            []), "token");

        result.Error.Should().BeNull();
        result.Completed.Should().NotBeNull();
        result.Completed!.Completion.OutputText.Should().Be("done");
        sessions.Registered.Should().ContainSingle().Which.ResponseId.Should().StartWith("resp_");
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("done");
        completion.LastRequest.Should().NotBeNull();
        completion.LastRequest!.Model.Should().Be("gpt-5");
        completion.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        completion.LastRequest.LlmControl.Should().NotBeNull();
        completion.LastRequest.LlmControl!.NyxIdRoutePreference.Should().Be("route-value");
        completion.LastRequest.CallerContext!.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task CreateAsync_WhenForwardToGAgent_ShouldRegisterSessionBeforeReturningForwardPlan()
    {
        var completion = new RecordingCompletionService(new ResponsesCompletionResult("unused", null, []));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            completionService: completion,
            sessionPort: sessions,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToGAgentAction("member-1")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), "token");

        result.Error.Should().BeNull();
        result.Forward.Should().NotBeNull();
        sessions.Registered.Should().ContainSingle();
        result.Forward!.Session.ResponseId.Should().Be(sessions.Registered[0].ResponseId);
        result.Forward.Session.ActorId.Should().Be("actor-" + sessions.Registered[0].ResponseId);
        completion.LastRequest.Should().BeNull("forwarded Responses bypass provider execution but must keep session lifecycle");
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
            []), "token");

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

        var invisible = await facade.CancelAsync("resp_1", "token");

        invisible.Error!.Code.Should().Be("response_scope_mismatch");
        sessionPort.UpdatedStatuses.Should().BeEmpty();

        queryPort.Snapshot = BuildSnapshot("resp_1", scopeId: "scope-1");
        var cancelled = await facade.CancelAsync("resp_1", "token");

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

        var result = await facade.CancelAsync("resp_expired", "token");

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

        var result = await facade.CancelAsync("resp_active", "token");

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            400,
            "response_cancel_rejected",
            "cannot cancel completed response"));
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
            "authentication_required",
            "NyxID authentication required for provider 'test-provider'. Please sign in."));
        sessions.UpdatedStatuses.Should().ContainSingle().Which.Status.Should().Be(LlmSessionStatus.Failed);
    }

    [Fact]
    public async Task StreamAsync_ShouldRecordCompletionFact_AndReturnSessionCompletion()
    {
        var completion = new RecordingCompletionService(new ResponsesCompletionResult(
            "stream done",
            new TokenUsage(4, 5, 9),
            []));
        var sessions = new RecordingSessionPort();
        sessions.QueryPort.Snapshot = BuildSnapshot("resp_stream", "scope-1");
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("stream done");
        result.Completion.Usage.Should().Be(new TokenUsage(4, 5, 9));
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.OutputText.Should().Be("stream done");
        sessions.UpdatedStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnUpstreamError_AndMarkSessionFailed()
    {
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: _ => new NyxIdUpstreamException(
                NyxIdUpstreamFailureKind.RateLimited,
                429,
                "route-a",
                "model-a",
                "rate limited"));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);

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
        var completion = new RecordingCompletionService(
            new ResponsesCompletionResult("unused", null, []),
            streamExceptionFactory: ct => new OperationCanceledException(ct));
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(completionService: completion, sessionPort: sessions);
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

    [Fact]
    public async Task ForwardAsync_WhenTargetResolutionIsCancelled_ShouldRecordFailureCompletion()
    {
        var sessions = new RecordingSessionPort();
        var queryPort = new RecordingSessionQueryPort
        {
            Snapshot = BuildSnapshot("resp_forward", "scope-1"),
        };
        var forwarding = new ResponsesForwardingApplicationService(
            teamEntryMemberResolver: new CancellingTeamEntryMemberResolver(),
            memberPublishedServiceResolver: new StaticMemberPublishedServiceResolver("unused"),
            staticGAgentStreamInvocationPort: new RecordingStaticGAgentStreamInvocationPort(),
            completionRecorder: new ResponsesForwardedCompletionRecorder(sessions, queryPort),
            logger: NullLogger<ResponsesForwardingApplicationService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await forwarding.ForwardAsync(
            BuildForwardPlan(ForwardToTeamAction("team-1", "chat")),
            "token",
            onEventAsync: null,
            cts.Token);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            408,
            "request_timeout",
            "Request timed out."));
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.FailureCode.Should().Be("request_timeout");
    }

    private static ResponsesCommandFacade CreateFacade(
        IResponsesCompletionApplicationService? completionService = null,
        ILlmSessionRegistrationPort? sessionPort = null,
        ILlmSessionQueryPort? queryPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new ResponsesCommandFacade(
            new StaticLlmProviderFactory(),
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            routeResolver ?? new StaticResponsesRouteResolver(null),
            effectiveSessionPort,
            queryPort ?? (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            completionService ?? new RecordingCompletionService(new ResponsesCompletionResult("ok", null, [])),
            [],
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
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResponsesToolClassification([], [], [], []),
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

    private static ResponsesForwardCommandResult BuildForwardPlan(ChatRouteAction action) =>
        new(
            new NormalizedResponsesRequest(
                "resp_forward",
                "msg_forward",
                "model",
                "hello",
                true,
                null,
                null,
                null,
                [],
                []),
            new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
            action,
            new LlmSessionRegistrationResult("actor-resp_forward", "resp_forward"),
            null,
            DateTimeOffset.UtcNow);

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction ForwardToGAgentAction(string actorId) => new()
    {
        ForwardToGagent = new ForwardToGAgent { ActorId = actorId },
    };

    private static ChatRouteAction ForwardToTeamAction(string teamId, string endpointId) => new()
    {
        ForwardToTeam = new ForwardToTeam { TeamId = teamId, EndpointId = endpointId },
    };

    private sealed class StaticCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(string nyxIdAccessToken, CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey));
    }

    private sealed class ThrowingCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(string nyxIdAccessToken, CancellationToken ct = default) =>
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

        public List<LlmSessionForwardedToolCall> RecordedToolCalls { get; } = [];

        public List<LlmSessionCompletion> RecordedCompletions { get; } = [];

        public Exception? UpdateStatusException { get; init; }

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
            var current = QueryPort.Snapshot;
            if (current is not null)
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

    private sealed class RecordingSessionQueryPort : ILlmSessionQueryPort
    {
        public LlmSessionSnapshot? Snapshot { get; set; }

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(string responseId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
    }

    private sealed class CancellingTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class StaticMemberPublishedServiceResolver(string publishedServiceId)
        : IMemberPublishedServiceResolver
    {
        public Task<MemberPublishedServiceResolution> ResolveAsync(
            MemberPublishedServiceResolveRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new MemberPublishedServiceResolution(
                request.ScopeId,
                request.MemberId,
                publishedServiceId));
    }

    private sealed class RecordingStaticGAgentStreamInvocationPort : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Invocation should not start when target resolution is cancelled.");
    }
}
