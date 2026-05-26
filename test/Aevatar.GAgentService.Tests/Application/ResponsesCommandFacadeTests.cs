using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndReturnAcceptedDispatchReceipt()
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
            []), "token");

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Completed.Should().BeNull();
        result.Accepted!.Admission.Accepted.Should().BeTrue();
        sessions.Registered.Should().ContainSingle().Which.ResponseId.Should().StartWith("resp_");
        sessions.RecordedCompletions.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(result.Accepted.Admission.ActorId);
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be(result.Accepted.Session.ResponseId);
        command.RunId.Should().Be($"{result.Accepted.Session.ResponseId}:llm-run");
        command.Model.Should().Be("gpt-5");
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchAccepted_ShouldNotReadCompletionReadModel()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), "token");

        sessions.RecordedCompletions.Should().BeEmpty();
        result.Completed.Should().BeNull();
        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WhenForwardToStudioMember_ShouldRegisterSessionBeforeReturningForwardPlan()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToStudioMemberAction("member-1")));

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
    }

    [Fact]
    public async Task CreateAsync_WhenForwardToGAgent_ShouldReturnRouteContractErrorWithoutRegisteringSession()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            chatRouteDecisionPort: new StaticResponsesChatRouteDecisionPort(ForwardToGAgentAction("direct-actor-1")));

        var result = await facade.CreateAsync(new ResponsesCommandRequest(
            "client-model",
            "hello",
            [],
            false,
            null,
            null,
            null,
            []), "token");

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(
            500,
            "chat_route_action_not_supported",
            "ForwardToGAgent is a direct actor target and is not supported by /v1/responses. Use ForwardToStudioMember or ForwardToTeam."));
        sessions.Registered.Should().BeEmpty();
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
        call.ActorId.Should().Be("actor-resp_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be("resp_stream");
        command.RunId.Should().Be("resp_stream:llm-run");
        command.Model.Should().Be("model");
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

    [Fact]
    public void BuildCompletion_ShouldReadTypedRunFinishedOutput_WhenNoLiveDeltaWasObserved()
    {
        var completion = ResponsesForwardedCompletionRecorder.BuildCompletion(
            [
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        Result = Any.Pack(new GAgentDraftRunResultPayload
                        {
                            Output = "final from typed payload",
                        }),
                    },
                },
            ],
            DateTimeOffset.UtcNow);

        completion.OutputText.Should().Be("final from typed payload");
    }

    [Fact]
    public void BuildCompletion_ShouldPreferLiveDeltaOverTypedRunFinishedOutput()
    {
        var completion = ResponsesForwardedCompletionRecorder.BuildCompletion(
            [
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "live ",
                    },
                },
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "delta",
                    },
                },
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        Result = Any.Pack(new GAgentDraftRunResultPayload
                        {
                            Output = "duplicate final",
                        }),
                    },
                },
            ],
            DateTimeOffset.UtcNow);

        completion.OutputText.Should().Be("live delta");
    }

    private static ResponsesCommandFacade CreateFacade(
        ILlmSessionRegistrationPort? sessionPort = null,
        ILlmSessionQueryPort? queryPort = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        RecordingActorDispatchPort? dispatchPort = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new ResponsesCommandFacade(
            callerScopeResolver ?? new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            routeResolver ?? new StaticResponsesRouteResolver(null),
            effectiveSessionPort,
            queryPort ?? (effectiveSessionPort as RecordingSessionPort)?.QueryPort ?? new RecordingSessionQueryPort(),
            dispatchPort ?? new RecordingActorDispatchPort(),
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

    private static ChatRouteAction ForwardToStudioMemberAction(
        string memberId,
        string endpointId = "",
        string scopeId = "") => new()
    {
        ForwardToStudioMember = new ForwardToStudioMember
        {
            MemberId = memberId,
            EndpointId = endpointId,
            ScopeId = scopeId,
        },
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
