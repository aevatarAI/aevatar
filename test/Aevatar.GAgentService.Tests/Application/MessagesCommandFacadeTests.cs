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

public sealed class MessagesCommandFacadeTests
{
    [Fact]
    public async Task CreateAsync_ShouldRegisterSession_AndReturnAcceptedDispatchReceipt()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch);

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Completed.Should().BeNull();
        result.Accepted!.Admission.Accepted.Should().BeTrue();
        sessions.Registered.Should().ContainSingle().Which.PreviousResponseId.Should().BeEmpty();
        sessions.RecordedCompletions.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(result.Accepted.Admission.ActorId);
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be(result.Accepted.Session.ResponseId);
        command.RunId.Should().Be($"{result.Accepted.Session.ResponseId}:llm-run");
        command.Model.Should().Be("claude-sonnet");
        command.ScopeId.Should().Be("scope-1");
        command.BearerToken.Should().Be("token");
        var toolContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        toolContext.Request.RequestId.Should().Be(command.ResponseId);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Caller.OwnerSubject.Should().Be("owner-1");
        toolContext.Caller.ResponseId.Should().Be(command.ResponseId);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchAccepted_ShouldNotReadCompletionReadModel()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), CallerScopeContext("token"));

        sessions.RecordedCompletions.Should().BeEmpty();
        result.Completed.Should().BeNull();
        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnStreamPlan_WhenRequestIsStreaming()
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(sessionPort: sessions);

        var result = await facade.CreateAsync(BuildRequest("anthropic/claude", stream: true), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.StreamPlan.Should().NotBeNull();
        result.Completed.Should().BeNull();
        result.StreamPlan!.LlmRequest.Model.Should().Be("claude");
        result.StreamPlan.LlmRequest.ToolContext.Should().NotBeNull();
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        result.StreamPlan.LlmRequest.Metadata.Should().NotContainKey("scope_id");
        result.StreamPlan.LlmRequest.ToolContext!.Request.RequestId.Should().Be(result.StreamPlan.Normalized.MessageId);
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

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Completion.Should().BeNull();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be("actor-msg_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be("msg_stream");
        command.RunId.Should().Be("msg_stream:llm-run");
        command.Model.Should().Be("claude-sonnet");
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
                "claude-sonnet",
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

    private static MessagesCommandRequest BuildRequest(
        string model,
        bool stream = false,
        IReadOnlyList<ChatMessage>? chatMessages = null,
        IReadOnlyList<ResponsesApplicationToolDeclaration>? declaredTools = null) =>
        new(
            model,
            100,
            chatMessages ?? [ChatMessage.User("hello")],
            declaredTools ?? [],
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
        IResponsesChatRouteDecisionPort? chatRouteDecisionPort = null,
        RecordingActorDispatchPort? dispatchPort = null,
        IResponsesToolClassificationService? toolClassificationService = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        return new MessagesCommandFacade(
            new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            dispatchPort ?? new RecordingActorDispatchPort(),
            toolClassificationService ?? new StaticResponsesToolClassificationService(),
            new StaticResponsesDirectToolPlanService(),
            NullLogger<MessagesCommandFacade>.Instance);
    }

    private static ResponsesCallerScopeResolutionContext CallerScopeContext(string bearerToken) =>
        new(bearerToken, null, null);

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
                ToolContext = BuildToolContext("msg_stream"),
            },
            BuildToolContext("msg_stream"),
            new ResponsesToolClassification([], [], [], []),
            ResponsesToolChoiceHintPlan.Empty);

    private static AgentToolExecutionContext BuildToolContext(string responseId) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(responseId, null),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", responseId),
        };

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction GAgentToolHintAction(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ModelName = "claude-sonnet",
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

    private sealed class StaticResponsesDirectToolPlanService : IResponsesDirectToolPlanService
    {
        public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction) =>
            ResponsesDirectToolPlan.Success(
                [],
                ResponsesToolChoiceHints.Create(
                    routeAction?.ForwardToModel?.ToolChoiceHint?.ToolName,
                    routeAction?.ForwardToModel?.ToolChoiceHint?.PrefilledArguments));
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
            var actorId = "actor-" + record.ResponseId;
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
