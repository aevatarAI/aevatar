using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        toolContext.Caller.OwnerScopeId.Should().Be("owner-1");
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_ShouldApplyConfiguredDefaultModel_WhenCallerOmitsModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(dispatchPort: dispatch, defaultIngressModel: "chrono-llm-public/gpt-5.5");

        var result = await facade.CreateAsync(BuildRequest("  "), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
        command.RoutePreference.Should().Be("route-value");
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistRouteToolSetNameIntoRunCommand()
    {
        var dispatch = new RecordingActorDispatchPort();
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ModelName = "anthropic/claude",
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            },
        });
        var facade = CreateFacade(dispatchPort: dispatch, chatRouteDecisionPort: routeDecisionPort);

        var result = await facade.CreateAsync(BuildRequest("anthropic/claude"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        // Off-grain run re-resolves this name to re-materialize the route tool set.
        command.ToolSelection.ToolSetName.Should().Be("workspace.default");
    }

    [Fact]
    public async Task CreateAsync_ShouldUseAccountPreferredModel_WhenCallerOmitsModel()
    {
        var dispatch = new RecordingActorDispatchPort();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            defaultIngressModel: "fallback-vendor/fallback-model",
            ownerLlmConfigSource: new StubOwnerLlmConfigSource(
                OwnerConfig("chrono-llm/gpt-5.5")));

        var result = await facade.CreateAsync(BuildRequest("  "), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5.5");
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
    public async Task CreateAsync_WhenOffActorFlagIsOff_ShouldKeepLegacyRunRequestedDispatch()
    {
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "claude-sonnet",
                OffActorLlmRunExecutorEnabled = false,
            });

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        dispatch.Calls.Should().ContainSingle()
            .Which.Envelope.Payload!.Is(LlmRunRequested.Descriptor).Should().BeTrue();
        executor.StartedRequests.Should().BeEmpty();
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenOffActorFlagIsOn_ShouldAdmitExecutorStartWithoutLegacyRunDispatch()
    {
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var facade = CreateFacade(
            dispatchPort: dispatch,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "claude-sonnet",
                OffActorLlmRunExecutorEnabled = true,
            });

        var result = await facade.CreateAsync(BuildRequest("claude-sonnet"), CallerScopeContext("token"));

        result.Error.Should().BeNull();
        result.Accepted.Should().NotBeNull();
        result.Accepted!.Admission.Should().Be(executor.StartAdmissions.Should().ContainSingle().Subject);
        dispatch.Calls.Should().BeEmpty();
        result.Accepted.Admission.CommandId.Should().StartWith("start-");
        executor.StartedRequests.Should().ContainSingle();
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
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
    public async Task CreateAsync_WhenNamedSkillTriggerProvided_ShouldRouteCommandAndCarryRecoveryContext()
    {
        var dispatch = new RecordingActorDispatchPort();
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("anthropic/claude"));
        var facade = CreateFacade(dispatchPort: dispatch, chatRouteDecisionPort: routeDecisionPort);

        var result = await facade.CreateAsync(
            BuildRequest("claude-sonnet", chatMessages: [ChatMessage.User("::Goal ship today")]),
            CallerScopeContext("token"));

        result.Error.Should().BeNull();
        routeDecisionPort.LastRequest.Should().NotBeNull();
        routeDecisionPort.LastRequest!.CommandName.Should().Be("goal");
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var recovery = AgentToolExecutionContextMapper.FromPayload(command.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("goal");
        recovery.PrimarySkillName.Should().Be("goal");
        recovery.CommandArguments.Should().Be("ship today");
        recovery.OriginalCommand.Should().Be("::Goal ship today");
        recovery.DiscoveryRequested.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenDiscoveryTriggerProvided_ShouldKeepRouteCommandEmptyAndRequestDiscovery()
    {
        var dispatch = new RecordingActorDispatchPort();
        var routeDecisionPort = new StaticResponsesChatRouteDecisionPort(ForwardToModelAction("anthropic/claude"));
        var facade = CreateFacade(dispatchPort: dispatch, chatRouteDecisionPort: routeDecisionPort);

        var result = await facade.CreateAsync(
            BuildRequest("claude-sonnet", chatMessages: [ChatMessage.User("::")]),
            CallerScopeContext("token"));

        result.Error.Should().BeNull();
        routeDecisionPort.LastRequest.Should().NotBeNull();
        routeDecisionPort.LastRequest!.CommandName.Should().BeEmpty();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var recovery = AgentToolExecutionContextMapper.FromPayload(command.ToolContext).SkillRecovery;
        recovery.DiscoveryRequested.Should().BeTrue();
        recovery.CommandName.Should().BeNull();
        recovery.PrimarySkillName.Should().BeNull();
        recovery.OriginalCommand.Should().Be("::");
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnObservedCompletion_AndDispatchWithResponseCorrelation()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var observer = StaticLlmSessionRunObservationService.Completed("Hello");
        var facade = CreateFacade(sessionPort: sessions, dispatchPort: dispatch, observationService: observer);
        var deltas = new List<string>();

        var result = await facade.StreamAsync(
            BuildStreamPlan(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Error.Should().BeNull();
        result.Completion.Should().NotBeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        deltas.Should().Equal("Hello");
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.UpdatedStatuses.Should().BeEmpty();
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be("actor-msg_stream");
        call.Envelope.Propagation!.CorrelationId.Should().Be("msg_stream");
        var command = call.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ResponseId.Should().Be("msg_stream");
        command.RunId.Should().Be("msg_stream:llm-run");
        command.Model.Should().Be("claude-sonnet");
        observer.LastRequest.Should().NotBeNull();
        observer.LastRequest!.ResponseId.Should().Be("msg_stream");
        observer.LastRequest.RunId.Should().Be("msg_stream:llm-run");
    }

    [Fact]
    public async Task StreamAsync_WhenOffActorFlagIsOn_ShouldUseExecutorStartAdmissionWithoutLegacyRunDispatch()
    {
        var sessions = new RecordingSessionPort();
        var dispatch = new RecordingActorDispatchPort();
        var executor = new BlockingLlmRunExecutor();
        var observer = StaticLlmSessionRunObservationService.Completed("Hello");
        var facade = CreateFacade(
            sessionPort: sessions,
            dispatchPort: dispatch,
            observationService: observer,
            llmRunExecutor: executor,
            ingressOptions: new ResponsesIngressOptions
            {
                DefaultModel = "claude-sonnet",
                OffActorLlmRunExecutorEnabled = true,
            });
        var deltas = new List<string>();

        var result = await facade.StreamAsync(
            BuildStreamPlan(),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Error.Should().BeNull();
        result.Completion.Should().NotBeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        deltas.Should().Equal("Hello");
        dispatch.Calls.Should().BeEmpty();
        observer.LastAdmission.Should().Be(executor.StartAdmissions.Should().ContainSingle().Subject);
        observer.LastAdmission!.CommandId.Should().Be("start-msg_stream");
        var request = executor.StartedRequests.Should().ContainSingle().Subject;
        request.SessionActorId.Should().Be("actor-msg_stream");
        request.ResponseId.Should().Be("msg_stream");
        request.RunId.Should().Be("msg_stream:llm-run");
        request.Command.ResponseId.Should().Be("msg_stream");
        request.Command.Model.Should().Be("claude-sonnet");
        observer.LastRequest.Should().NotBeNull();
        observer.LastRequest!.ResponseId.Should().Be("msg_stream");
        observer.LastRequest.RunId.Should().Be("msg_stream:llm-run");
        executor.ExecuteStarted.Task.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(LlmSessionRunObservedTerminalKind.Failed, 500, "llm_run_failed", "provider crashed")]
    [InlineData(LlmSessionRunObservedTerminalKind.Cancelled, 409, "run_cancelled", "LLM run was cancelled.")]
    [InlineData(LlmSessionRunObservedTerminalKind.TimedOut, 504, "response_timeout", "Timed out waiting 30 seconds for the LLM run to emit a terminal event.")]
    public async Task StreamAsync_WhenObservedTerminalError_ShouldReturnError_WithoutWritingSessionStatus(
        LlmSessionRunObservedTerminalKind kind,
        int statusCode,
        string code,
        string message)
    {
        var sessions = new RecordingSessionPort();
        var facade = CreateFacade(
            sessionPort: sessions,
            observationService: StaticLlmSessionRunObservationService.Error(kind, statusCode, code, message));

        var result = await facade.StreamAsync(BuildStreamPlan(), (_, _) => ValueTask.CompletedTask);

        result.Error.Should().BeEquivalentTo(new ResponsesCommandError(statusCode, code, message));
        result.Completion.Should().BeNull();
        sessions.UpdatedStatuses.Should().BeEmpty();
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
                    [],
                    ["get_weather"])));

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
        command.ToolSelection.OwnedToolNames.Should().ContainSingle("get_weather");
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
        IResponsesToolClassificationService? toolClassificationService = null,
        ILlmSessionRunObservationService? observationService = null,
        string? defaultIngressModel = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ResponsesIngressOptions? ingressOptions = null,
        ILlmRunExecutor? llmRunExecutor = null)
    {
        var effectiveSessionPort = sessionPort ?? new RecordingSessionPort();
        var options = ingressOptions ?? (defaultIngressModel is null
            ? null
            : new ResponsesIngressOptions { DefaultModel = defaultIngressModel });
        return new MessagesCommandFacade(
            new StaticCallerScopeResolver(),
            chatRouteDecisionPort ?? new StaticResponsesChatRouteDecisionPort(ForwardToModelAction(string.Empty)),
            new StaticResponsesRouteResolver("route-value"),
            effectiveSessionPort,
            dispatchPort ?? new RecordingActorDispatchPort(),
            toolClassificationService ?? new StaticResponsesToolClassificationService(),
            new StaticResponsesDirectToolPlanService(),
            observationService ?? StaticLlmSessionRunObservationService.Completed("ok"),
            NullLogger<MessagesCommandFacade>.Instance,
            options is null ? null : Options.Create(options),
            ownerLlmConfigSource,
            llmRunExecutor);
    }

    private static OwnerLlmConfig OwnerConfig(string modelId) => new(
        new LLMSelection
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = LLMSelectionPolicy.GatewayRoute,
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = modelId,
            },
        },
        LLMSelectionPersistenceStatus.Ready,
        0);

    private sealed class StubOwnerLlmConfigSource(OwnerLlmConfig? config = null)
        : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(config ?? OwnerLlmConfig.Empty);
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
            new ResponsesToolClassification([], [], [], [], []),
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
        public ResponsesChatRouteDecisionRequest? LastRequest { get; private set; }

        public Task<ChatRouteDecision> ResolveAsync(
            ResponsesChatRouteDecisionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatRouteDecision
            {
                Action = action.Clone(),
                UsedFallback = usedFallback,
                MatchedRuleId = matchedRuleId,
            });
        }
    }

    private sealed class StaticResponsesToolClassificationService(
        ResponsesToolClassification? classification = null) : IResponsesToolClassificationService
    {
        public ValueTask<ResponsesToolClassification> ClassifyAsync(
            IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
            ResponsesToolProviderContext context,
            IEnumerable<IResponsesToolProvider>? additionalProviders = null,
            CancellationToken ct = default) =>
            ValueTask.FromResult(classification ?? new ResponsesToolClassification([], [], [], [], []));
    }

    private sealed class StaticResponsesDirectToolPlanService : IResponsesDirectToolPlanService
    {
        public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction) =>
            ResponsesDirectToolPlan.Success(
                [],
                ResponsesToolChoiceHints.Create(
                    routeAction?.ForwardToModel?.ToolChoiceHint?.ToolName,
                    routeAction?.ForwardToModel?.ToolChoiceHint?.PrefilledArguments),
                routeAction?.ForwardToModel?.ToolSetRef?.Name ?? string.Empty);
    }

    private sealed class StaticLlmSessionRunObservationService(
        LlmSessionRunObservedResult result,
        IReadOnlyList<LlmSessionRunObservedDelta>? deltas = null) : ILlmSessionRunObservationService
    {
        public LlmSessionRunObservationRequest? LastRequest { get; private set; }

        public DispatchAdmission? LastAdmission { get; private set; }

        public static StaticLlmSessionRunObservationService Completed(string outputText) =>
            new(
                new LlmSessionRunObservedResult(
                    null,
                    new LlmSessionCompletionSnapshot(outputText, [], DateTimeOffset.UtcNow, null, null),
                    null),
                [new LlmSessionRunObservedDelta(outputText, null)]);

        public static StaticLlmSessionRunObservationService Error(
            LlmSessionRunObservedTerminalKind kind,
            int statusCode,
            string code,
            string message) =>
            new(new LlmSessionRunObservedResult(
                null,
                null,
                new LlmSessionRunObservedError(kind, statusCode, code, message)));

        public async Task<LlmSessionRunObservedResult> ObserveAsync(
            LlmSessionRunObservationRequest request,
            Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var admission = await request.DispatchAsync(ct);
            LastAdmission = admission;
            foreach (var delta in deltas ?? [])
            {
                if (onDelta != null)
                    await onDelta(delta, ct);
            }

            return result with { Admission = admission };
        }
    }

    private sealed class BlockingLlmRunExecutor : ILlmRunExecutor
    {
        public List<LlmRunExecutorRequest> StartedRequests { get; } = [];

        public List<DispatchAdmission> StartAdmissions { get; } = [];

        public TaskCompletionSource ExecuteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> StartAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            StartedRequests.Add(request);
            var envelope = new EventEnvelope
            {
                Id = "start-" + request.ResponseId,
                Propagation = new EnvelopePropagation { CorrelationId = request.ResponseId },
                Payload = Any.Pack(new RecordLlmRunStarted
                {
                    Command = request.Command.Clone(),
                    StartedAt = request.Command.RequestedAt?.Clone(),
                }),
            };
            var admission = DispatchAdmissionFactory.Create(request.SessionActorId, envelope);
            StartAdmissions.Add(admission);
            return Task.FromResult(admission);
        }

        public Task ExecuteAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            ExecuteStarted.SetResult();
            return Task.CompletedTask;
        }
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

        public Task CancelRunAsync(
            string sessionActorId,
            string responseId,
            string runId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
