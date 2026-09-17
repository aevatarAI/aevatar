using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class AIAbstractionsProtoCoverageTests
{
    [Theory]
    [InlineData(AgentToolInvocationSurface.Unspecified)]
    [InlineData(AgentToolInvocationSurface.HumanSession)]
    [InlineData(AgentToolInvocationSurface.WorkflowToolCall)]
    [InlineData(AgentToolInvocationSurface.WorkflowLlmToolLoop)]
    public void AgentToolExecutionContext_ShouldRoundTripInvocationSurface(
        AgentToolInvocationSurface invocationSurface)
    {
        var payload = (AgentToolExecutionContext.Empty with
        {
            InvocationSurface = invocationSurface,
        }).ToPayload();

        var copy = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        copy.InvocationSurface.Should().Be(invocationSurface);
    }

    [Fact]
    public void AgentToolExecutionContext_ShouldRoundTripDedicatedSourceReadableCredential()
    {
        var payload = (AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "delegation-alpha",
                "org-alpha",
                "sender-alpha",
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                "source-alpha",
                AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
        }).ToPayload();

        var copy = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        copy.Credentials.NyxIdAccessToken.Should().Be("delegation-alpha");
        copy.Credentials.NyxIdOrgToken.Should().Be("org-alpha");
        copy.Credentials.SenderNyxIdAccessToken.Should().Be("sender-alpha");
        copy.Credentials.SourceReadableNyxIdAccessToken.Should().Be("source-alpha");
        copy.Credentials.NyxIdCredentialAuthority.Should().Be(
            AgentToolNyxIdCredentialAuthority.ToolExecutionContext);
    }

    [Fact]
    public void LLMControlContext_ShouldMergeIntoTypedContexts_AndRoundTripPayload()
    {
        var control = new LLMControlContext(
            NyxIdAccessToken: " token-1 ",
            NyxIdOrgToken: " org-1 ",
            SenderNyxIdAccessToken: " sender-1 ",
            ModelOverride: " model-a ",
            NyxIdRoutePreference: " route-a ",
            MaxToolRoundsOverride: 7,
            UserMemoryPrompt: " remember ");
        var baseToolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "base-token",
                NyxIdOrgToken = "base-org",
                SenderNyxIdAccessToken = "base-sender",
            },
            Routing = LLMRequestRoutingContext.Empty with
            {
                ModelOverride = "base-model",
                NyxIdRoutePreference = "base-route",
                MaxToolRoundsOverride = 3,
                UserMemoryPrompt = "base-memory",
            },
        };
        var toolContext = control.ToToolContext(baseToolContext);
        var routingContext = control.ToRoutingContext(new LLMRequestRoutingContext(
            "base-model",
            "base-route",
            3,
            "base-memory"));
        var payload = control.ToPayload();
        var roundTripped = LLMControlContextMapper.FromPayload(payload);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("token-1");
        toolContext.Credentials.NyxIdOrgToken.Should().Be("org-1");
        toolContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-1");
        toolContext.Routing.ModelOverride.Should().Be("model-a");
        toolContext.Routing.NyxIdRoutePreference.Should().Be("route-a");
        toolContext.Routing.MaxToolRoundsOverride.Should().Be(7);
        toolContext.Routing.UserMemoryPrompt.Should().Be("remember");
        routingContext.Should().Be(new LLMRequestRoutingContext("model-a", "route-a", 7, "remember"));
        roundTripped.Should().Be(new LLMControlContext("token-1", "org-1", "sender-1", "model-a", "route-a", 7, "remember"));
        payload.HasMaxToolRoundsOverride.Should().BeTrue();
    }
    [Fact]
    public void LLMControlContext_ShouldKeepBaseValues_WhenControlValuesAreBlank()
    {
        var control = new LLMControlContext(" ", null, "\t", "", " ", null, null);
        var baseToolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "base-token",
                SenderNyxIdAccessToken = "base-sender",
            },
            Routing = LLMRequestRoutingContext.Empty with
            {
                ModelOverride = "base-model",
                NyxIdRoutePreference = "base-route",
                MaxToolRoundsOverride = 5,
                UserMemoryPrompt = "base-memory",
            },
        };
        control.ToToolContext(baseToolContext).Should().Be(baseToolContext);
        control.ToRoutingContext(baseToolContext.Routing).Should().Be(baseToolContext.Routing);
        LLMControlContextMapper.FromPayload(null).Should().Be(LLMControlContext.Empty);
        control.ToPayload().HasMaxToolRoundsOverride.Should().BeFalse();
    }

    [Fact]
    public void ProtoMessages_ShouldRoundTripAndClone()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "call-1",
            ToolName = "ornn_publish_skill",
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
            IsDestructive = false,
            SideEffectKind = "ornn.publish.skill",
            Effect = AgentToolReceiptEffect.Mutating,
            SubjectKind = "ornn.skill",
            SubjectId = "skill-1",
            SubjectVersion = "1.0",
            SubjectHash = "hash-1",
            ApprovalRequestId = "approval-1",
            ErrorCode = "",
            ErrorMessage = "",
            ResultJson = """{"guid":"skill-1","version":"1.0","skillHash":"hash-1"}""",
            ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
            {
                ParentActorId = "parent-actor",
                ParentRunId = "parent-run",
                ParentStepId = "parent-step",
                InvocationId = "invoke-1",
                ChildRunId = "child-run-1",
            },
        };
        var receiptRoundTrip = RoundTrip(receipt, AgentToolReceipt.Parser);
        receiptRoundTrip.SubjectId.Should().Be("skill-1");
        receiptRoundTrip.SubjectHash.Should().Be("hash-1");
        receiptRoundTrip.Effect.Should().Be(AgentToolReceiptEffect.Mutating);
        receiptRoundTrip.ManagedWorkflowHandoff.InvocationId.Should().Be("invoke-1");
        var request = RoundTrip(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
            Headers = { ["correlation_id"] = "c-1" },
            TimeoutMs = 2500,
            ScopeId = "scope-1",
            ConnectorHttpAuthorization = "Bearer connector-token",
            CallerNyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
            CallerSourceReadableNyxIdBearerToken = "source-readable-token",
            LlmControl = new LLMControlContextPayload
            {
                NyxIdAccessToken = "access-token",
                NyxIdOrgToken = "org-token",
                SenderNyxIdAccessToken = "sender-token",
                ModelOverride = "model-a",
                NyxIdRoutePreference = "/api/v1/proxy/s/llm",
                MaxToolRoundsOverride = 7,
                UserMemoryPrompt = "remember",
            },
            InputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Image,
                    Uri = "https://example.com/cat.png",
                    MediaType = "image/png",
                    Name = "cat",
                },
            },
        }, ChatRequestEvent.Parser);
        request.Headers["correlation_id"].Should().Be("c-1");
        request.TimeoutMs.Should().Be(2500);
        request.ScopeId.Should().Be("scope-1");
        request.ConnectorHttpAuthorization.Should().Be("Bearer connector-token");
        request.CallerNyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        request.CallerSourceReadableNyxIdBearerToken.Should().Be("source-readable-token");
        request.LlmControl.ModelOverride.Should().Be("model-a");
        request.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/llm");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(7);
        request.InputParts.Should().ContainSingle();
        request.InputParts[0].Kind.Should().Be(ChatContentPartKind.Image);
        var response = RoundTrip(new ChatResponseEvent
        {
            Content = "world",
            SessionId = "session-1",
        }, ChatResponseEvent.Parser);
        response.Content.Should().Be("world");
        var textStart = RoundTrip(new TextMessageStartEvent
        {
            SessionId = "session-1",
            AgentId = "agent-1",
        }, TextMessageStartEvent.Parser);
        textStart.AgentId.Should().Be("agent-1");
        var textContent = RoundTrip(new TextMessageContentEvent
        {
            SessionId = "session-1",
            Delta = "delta",
        }, TextMessageContentEvent.Parser);
        textContent.Delta.Should().Be("delta");
        var textReasoning = RoundTrip(new TextMessageReasoningEvent
        {
            SessionId = "session-1",
            Delta = "reasoning-delta",
        }, TextMessageReasoningEvent.Parser);
        textReasoning.Delta.Should().Be("reasoning-delta");
        var textEnd = RoundTrip(new TextMessageEndEvent
        {
            SessionId = "session-1",
            Content = "done",
        }, TextMessageEndEvent.Parser);
        textEnd.Content.Should().Be("done");
        var toolCall = RoundTrip(new ToolCallEvent
        {
            ToolName = "search",
            ArgumentsJson = "{\"q\":\"x\"}",
            CallId = "call-1",
        }, ToolCallEvent.Parser);
        toolCall.ToolName.Should().Be("search");
        var toolResult = RoundTrip(new ToolResultEvent
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
            Success = true,
            Error = "",
            Receipt = receipt.Clone(),
        }, ToolResultEvent.Parser);
        toolResult.Success.Should().BeTrue();
        toolResult.Receipt.SubjectId.Should().Be("skill-1");
        var tokenUsage = RoundTrip(new TokenUsagePayload
        {
            PromptTokens = 2,
            CompletionTokens = 3,
            TotalTokens = 5,
        }, TokenUsagePayload.Parser);
        tokenUsage.TotalTokens.Should().Be(5);
        var tokenUsageEvent = RoundTrip(new ChatTokenUsageEvent
        {
            SessionId = "session-1",
            Usage = new TokenUsagePayload
            {
                PromptTokens = 11,
                CompletionTokens = 13,
                TotalTokens = 24,
            },
            Model = "nyxid-model",
        }, ChatTokenUsageEvent.Parser);
        tokenUsageEvent.SessionId.Should().Be("session-1");
        tokenUsageEvent.Usage.TotalTokens.Should().Be(24);
        tokenUsageEvent.Model.Should().Be("nyxid-model");
        var sessionStarted = RoundTrip(new RoleChatSessionStartedEvent
        {
            SessionId = "session-1",
            Prompt = "hello",
            InputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Text,
                    Text = "hello",
                },
            },
        }, RoleChatSessionStartedEvent.Parser);
        sessionStarted.Prompt.Should().Be("hello");
        sessionStarted.InputParts.Should().ContainSingle();
        var sessionCompleted = RoundTrip(new RoleChatSessionCompletedEvent
        {
            SessionId = "session-1",
            Content = "done",
            ReasoningContent = "thinking",
            Prompt = "hello",
            ContentEmitted = true,
            Usage = new TokenUsagePayload
            {
                PromptTokens = 17,
                CompletionTokens = 19,
                TotalTokens = 36,
            },
            Model = "nyxid-model",
            TerminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-20T01:02:03Z")),
            ToolCalls =
            {
                new ToolCallEvent
                {
                    ToolName = "search",
                    ArgumentsJson = "{\"q\":\"x\"}",
                    CallId = "call-1",
                },
            },
            ToolReceipts = { receipt.Clone() },
            OutputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Image,
                    Uri = "https://example.com/output.png",
                    MediaType = "image/png",
                },
            },
        }, RoleChatSessionCompletedEvent.Parser);
        sessionCompleted.Content.Should().Be("done");
        sessionCompleted.ReasoningContent.Should().Be("thinking");
        sessionCompleted.ContentEmitted.Should().BeTrue();
        sessionCompleted.Usage.TotalTokens.Should().Be(36);
        sessionCompleted.Model.Should().Be("nyxid-model");
        sessionCompleted.TerminalTime.ToDateTimeOffset().Should()
            .Be(DateTimeOffset.Parse("2026-07-20T01:02:03Z"));
        sessionCompleted.ToolCalls.Should().ContainSingle();
        sessionCompleted.ToolReceipts.Should().ContainSingle(x => x.SubjectHash == "hash-1");
        sessionCompleted.OutputParts.Should().ContainSingle();
        var initialize = RoundTrip(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            Temperature = 0.3,
            MaxTokens = 120,
            MaxToolRounds = 3,
            MaxHistoryMessages = 40,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
            VoiceSessionDefaults =
            {
                ["voice_presence"] = new VoiceSessionDefaults
                {
                    Voice = "verse",
                    Instructions = "agent voice defaults",
                    SampleRateHz = 16000,
                    TurnDetectionMode = VoiceTurnDetectionMode.ServerVad,
                    VadDetectionThreshold = 0.4f,
                    VadPrefixPaddingMs = 120,
                    VadSilenceDurationMs = 240,
                },
            },
        }, InitializeRoleAgentEvent.Parser);
        initialize.RoleName.Should().Be("assistant");
        initialize.HasTemperature.Should().BeTrue();
        initialize.VoiceSessionDefaults["voice_presence"].Voice.Should().Be("verse");
        var overrides = RoundTrip(new AIAgentConfigOverrides
        {
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            Temperature = 0.4,
            MaxTokens = 128,
            MaxToolRounds = 2,
            MaxHistoryMessages = 16,
        }, AIAgentConfigOverrides.Parser);
        overrides.ProviderName.Should().Be("mock");
        var state = RoundTrip(new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 7,
            ConfigOverrides = overrides,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
            PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-1",
                SessionId = "session-approval",
                ToolName = "ssh_exec",
                ToolCallId = "call-1",
                ArgumentsJson = "{}",
                IsDestructive = true,
                TimeoutCallbackId = "timeout-1",
                RemoteApprovalId = "remote-1",
                RemoteStatusCheckAttempt = 2,
                RemoteApprovalExpiresAtUnixMs = 123456,
                ToolContext = new AgentToolExecutionContext(
                    new AgentToolRequestIdentity("req-1", "call-1"),
                    AgentToolCredentials.Empty,
                    new AgentToolCallerContext("scope-a", "owner-a", "response-a"),
                    new AgentToolChannelContext("telegram", "sender-a", "registration-a", "message-a", "platform-message-a", "delivery-target-a", ChannelWorkflowResultDeliveryCredentialTestData.Create("reply-a"), "bot-reg-a"),
                    new AgentToolSenderBindingContext("binding-a"),
                    new LLMRequestRoutingContext("model-a", "route-a", 4, "remember-a"),
                    new AgentToolConnectedServicesContext("""{"service":"telegram"}"""),
                    AgentSkillRecoveryContext.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["trace-id"] = "trace-from-context",
                    }).ToPayload(),
            },
            VoicePresence =
            {
                ["voice_presence"] = new VoicePresenceRuntimeState
                {
                    Status = VoicePresenceRuntimeStatus.AudioDraining,
                    CurrentResponseId = 12,
                    LastDrainAckResponseId = 11,
                    LastDrainAckPlayoutSequence = 3400,
                    NextResponseId = 13,
                    ActiveProviderResponseId = "provider-response-12",
                    ActiveSessionConfig = new VoiceSessionConfig
                    {
                        Voice = "verse",
                        Instructions = "active voice",
                        SampleRateHz = 16000,
                        TurnDetectionMode = VoiceTurnDetectionMode.Disabled,
                    },
                },
            },
            VoiceSessionDefaults =
            {
                ["voice_presence"] = new VoiceSessionDefaults
                {
                    Voice = "marin",
                    SampleRateHz = 16000,
                    TurnDetectionMode = VoiceTurnDetectionMode.ClientVad,
                },
            },
            Sessions =
            {
                ["session-1"] = new RoleChatSessionState
                {
                    Prompt = "hello",
                    Completed = true,
                    FinalContent = "done",
                    FinalReasoningContent = "thinking",
                    Sequence = 7,
                    ContentEmitted = true,
                    InputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Text,
                            Text = "hello",
                        },
                    },
                    OutputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Image,
                            Uri = "https://example.com/output.png",
                            MediaType = "image/png",
                        },
                    },
                    ToolCalls =
                    {
                        new ToolCallEvent
                        {
                            ToolName = "search",
                            ArgumentsJson = "{\"q\":\"x\"}",
                            CallId = "call-1",
                        },
                    },
                    ToolReceipts = { receipt.Clone() },
                },
            },
        }, RoleGAgentState.Parser);
        state.RoleName.Should().Be("assistant");
        state.MessageCount.Should().Be(7);
        state.EventModules.Should().Be("demo");
        state.EventRoutes.Should().Be("event.type == X -> demo");
        state.Sessions["session-1"].FinalContent.Should().Be("done");
        state.Sessions["session-1"].Sequence.Should().Be(7);
        state.Sessions["session-1"].InputParts.Should().ContainSingle();
        state.Sessions["session-1"].OutputParts.Should().ContainSingle();
        state.Sessions["session-1"].ToolCalls.Should().ContainSingle();
        state.Sessions["session-1"].ToolReceipts.Should().ContainSingle(x => x.SubjectId == "skill-1");
        state.PendingApproval.Should().NotBeNull();
        state.PendingApproval!.RemoteApprovalId.Should().Be("remote-1");
        state.PendingApproval.ToolContext.Should().NotBeNull();
        state.PendingApproval.RemoteStatusCheckAttempt.Should().Be(2);
        state.PendingApproval.RemoteApprovalExpiresAtUnixMs.Should().Be(123456);
        state.PendingApproval.ToolContext.Should().NotBeNull();
        var pendingContext = AgentToolExecutionContextMapper.FromPayload(state.PendingApproval.ToolContext);
        pendingContext.Request.RequestId.Should().Be("req-1");
        pendingContext.Request.CallId.Should().Be("call-1");
        pendingContext.Credentials.Should().Be(AgentToolCredentials.Empty);
        pendingContext.Caller.ScopeId.Should().Be("scope-a");
        pendingContext.Channel.SenderId.Should().Be("sender-a");
        pendingContext.Channel.DeliveryTargetId.Should().Be("delivery-target-a");
        pendingContext.SenderBinding.BindingId.Should().Be("binding-a");
        pendingContext.Routing.ModelOverride.Should().Be("model-a");
        pendingContext.Routing.MaxToolRoundsOverride.Should().Be(4);
        pendingContext.ConnectedServices.ContextJson.Should().Be("""{"service":"telegram"}""");
        pendingContext.ExternalMetadata.Should().ContainKey("trace-id").WhoseValue.Should().Be("trace-from-context");
        state.VoicePresence["voice_presence"].CurrentResponseId.Should().Be(12);
        state.VoicePresence["voice_presence"].ActiveProviderResponseId.Should().Be("provider-response-12");
        state.VoicePresence["voice_presence"].ActiveSessionConfig.TurnDetectionMode.Should().Be(VoiceTurnDetectionMode.Disabled);
        state.VoiceSessionDefaults["voice_presence"].Voice.Should().Be("marin");
    }

    [Fact]
    public void PendingToolApprovalState_ShouldRoundTripTypedToolContextAndRemoteBinding()
    {
        var pending = RoundTrip(new PendingToolApprovalState
        {
            RequestId = "req-typed",
            SessionId = "session-typed",
            ToolName = "dangerous_tool",
            ToolCallId = "call-typed",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-typed",
            RemoteStatusCheckAttempt = 2,
            RemoteApprovalExpiresAtUnixMs = 123_456,
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("req-typed", "call-typed"),
                Credentials = new AgentToolCredentials("token-should-only-appear-in-this-explicit-roundtrip", null, null),
                Caller = new AgentToolCallerContext("scope-typed", "owner-typed", "response-typed"),
                Channel = new AgentToolChannelContext("lark", "sender-1", "registration-1", "message-1", "platform-message-1", "delivery-target-1", ChannelWorkflowResultDeliveryCredentialTestData.Create("reply-1"), "bot-reg-1"),
                SenderBinding = new AgentToolSenderBindingContext("binding-1", "nyx-user-1"),
                Routing = new LLMRequestRoutingContext("model-typed", "route-typed", 4, "memory-typed"),
                ConnectedServices = new AgentToolConnectedServicesContext("{\"service\":\"ok\"}"),
                ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["trace-id"] = "trace-typed",
                },
            }).ToPayload(),
        }, PendingToolApprovalState.Parser);
        pending.ToolContext.Should().NotBeNull();
        pending.ToolContext.Request.RequestId.Should().Be("req-typed");
        pending.ToolContext.Caller.ScopeId.Should().Be("scope-typed");
        pending.ToolContext.Channel.Platform.Should().Be("lark");
        pending.ToolContext.Channel.DeliveryTargetId.Should().Be("delivery-target-1");
        (pending.ToolContext.SenderBinding.BindingId, pending.ToolContext.SenderBinding.NyxUserId).Should().Be(("binding-1", "nyx-user-1"));
        pending.ToolContext.Routing.MaxToolRoundsOverride.Should().Be(4);
        pending.ToolContext.ConnectedServices.ContextJson.Should().Be("{\"service\":\"ok\"}");
        pending.ToolContext.ExternalMetadata["trace-id"].Should().Be("trace-typed");
        pending.RemoteApprovalId.Should().Be("remote-typed");
        pending.RemoteStatusCheckAttempt.Should().Be(2);
        pending.RemoteApprovalExpiresAtUnixMs.Should().Be(123_456);
    }
    [Fact]
    public void ProtoMessages_ShouldSupportMergeAndDescriptors()
    {
        var target = new ChatRequestEvent();
        target.MergeFrom(new ChatRequestEvent
        {
            Prompt = "p1",
            SessionId = "s1",
            Headers = { ["k1"] = "v1" },
        });
        target.Prompt.Should().Be("p1");
        target.SessionId.Should().Be("s1");
        target.Headers["k1"].Should().Be("v1");
        target.Clone().Should().BeEquivalentTo(target);
        target.ToString().Should().Contain("prompt");
        AiMessagesReflection.Descriptor.Should().NotBeNull();
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ChatRequestEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ChatResponseEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageStartEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageContentEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageReasoningEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageEndEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolCallEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolResultEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionStartedEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionCompletedEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(InitializeRoleAgentEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(AIAgentConfigOverrides));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionState));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleGAgentState));
    }
    [Fact]
    public void ProtoMessages_ShouldValidateNullAssignments()
    {
        var request = new ChatRequestEvent();
        var response = new ChatResponseEvent();
        var textStart = new TextMessageStartEvent();
        var toolCall = new ToolCallEvent();
        var toolResult = new ToolResultEvent();
        var sessionStarted = new RoleChatSessionStartedEvent();
        var initialize = new InitializeRoleAgentEvent();
        var state = new RoleGAgentState();
        Action setRequestPrompt = () => request.Prompt = null!;
        Action setResponseContent = () => response.Content = null!;
        Action setTextStartSession = () => textStart.SessionId = null!;
        Action setToolCallName = () => toolCall.ToolName = null!;
        Action setToolResultCallId = () => toolResult.CallId = null!;
        Action setSessionStartedId = () => sessionStarted.SessionId = null!;
        Action setInitRoleName = () => initialize.RoleName = null!;
        Action setStateRoleName = () => state.RoleName = null!;
        setRequestPrompt.Should().Throw<ArgumentNullException>();
        setResponseContent.Should().Throw<ArgumentNullException>();
        setTextStartSession.Should().Throw<ArgumentNullException>();
        setToolCallName.Should().Throw<ArgumentNullException>();
        setToolResultCallId.Should().Throw<ArgumentNullException>();
        setSessionStartedId.Should().Throw<ArgumentNullException>();
        setInitRoleName.Should().Throw<ArgumentNullException>();
        setStateRoleName.Should().Throw<ArgumentNullException>();
    }
    [Fact]
    public void ProtoMessages_ShouldCoverGeneratedBranchesAndUnknownFields()
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            MaxTokens = 128,
        };
        initialize.MergeFrom((InitializeRoleAgentEvent)null!);
        initialize.Equals((object?)null).Should().BeFalse();
        var overrides = new AIAgentConfigOverrides
        {
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            MaxTokens = 128,
        };
        overrides.MergeFrom((AIAgentConfigOverrides)null!);
        overrides.Equals((object?)null).Should().BeFalse();
        var state = new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 1,
            ConfigOverrides = overrides,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
            Sessions =
            {
                ["session-1"] = new RoleChatSessionState
                {
                    Prompt = "hello",
                    Completed = true,
                    FinalContent = "done",
                    FinalReasoningContent = "thinking",
                    Sequence = 1,
                    ContentEmitted = true,
                    ToolCalls =
                    {
                        new ToolCallEvent
                        {
                            ToolName = "search",
                            ArgumentsJson = "{\"q\":\"x\"}",
                            CallId = "call-1",
                        },
                    },
                },
            },
        };
        state.MergeFrom((RoleGAgentState)null!);
        state.Equals((object?)null).Should().BeFalse();
        var submitted = RoundTrip(new RemoteToolApprovalSubmittedEvent
        {
            RequestId = "req-1",
            RemoteApprovalId = "remote-1",
            StatusCheckAttempt = 1,
            ExpiresAtUnixMs = 123456,
        }, RemoteToolApprovalSubmittedEvent.Parser);
        submitted.RemoteApprovalId.Should().Be("remote-1");
        var statusCheck = RoundTrip(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-approval",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        }, ToolApprovalRemoteStatusCheckFiredEvent.Parser);
        statusCheck.RemoteApprovalId.Should().Be("remote-1");
        var parsedResponse = ChatResponseEvent.Parser.ParseFrom(new byte[]
        {
            10, 1, (byte)'x',
            18, 1, (byte)'s',
            0x98, 0x06, 0x01,
        });
        parsedResponse.Content.Should().Be("x");
        parsedResponse.SessionId.Should().Be("s");
        parsedResponse.ToByteArray().Length.Should().BeGreaterThan(4);
    }
    [Fact]
    public void AgentProfileTurnAuthorityContracts_ShouldRoundTripTypedStateEventWithoutSensitiveFields()
    {
        var authority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey { SessionId = "session-authority", Attempt = 2 },
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
                { ProfileId = "profile-a", ProfileVersion = "v3", PolicyRevision = "policy-7", IntentId = "intent-a" },
            SelectedExactSkillRef = new ExactRemoteSkillRef { Guid = "skill-guid", LiteralVersion = "1.2.3" },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            DegradationReasons = { AgentProfileTurnDegradationReason.ToolNameCollision },
            AuthorityCeilingToolNames = { "search", "task" },
        };
        var committed = RoundTrip(new AgentProfileTurnAuthorityCommittedEvent
        {
            CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
            Authority = authority,
        }, AgentProfileTurnAuthorityCommittedEvent.Parser);
        var state = RoundTrip(
            new RoleGAgentState { AgentProfileTurnAuthority = authority },
            RoleGAgentState.Parser);
        state.AgentProfileTurnAuthority.Should().BeEquivalentTo(authority);
        committed.Authority.Should().BeEquivalentTo(authority);
        new[] { (int)AgentProfileTurnAuthorityKind.RestrictedEmpty, (int)AgentProfileTurnAuthorityKind.Recovery, (int)AgentProfileTurnAuthorityKind.Selected }.Should().Equal(1, 2, 3);
        new[] { (int)AgentProfileTurnAuthorityCommitKind.Initial, (int)AgentProfileTurnAuthorityCommitKind.RetryStarted, (int)AgentProfileTurnAuthorityCommitKind.Reconcile }.Should().Equal(1, 2, 3);
        ((int)AgentProfileTurnDegradationReason.MaterializationFailed).Should().Be(15);
        RoleGAgentState.Descriptor.Fields.InFieldNumberOrder().Select(field => (field.FieldNumber, field.Name))
            .Should().Contain((13, "agent_profile_turn_authority"));
        AgentProfileTurnAuthorityCommittedEvent.Descriptor.Fields.InFieldNumberOrder().Select(field => (field.FieldNumber, field.Name))
            .Should().Equal((1, "commit_kind"), (2, "authority"));
        AgentProfileTurnAuthorityState.Descriptor.Fields.InFieldNumberOrder().Select(field => (field.FieldNumber, field.Name))
            .Should().Equal((1, "reconciliation_key"), (2, "candidate_route"), (3, "selected_exact_skill_ref"),
                (4, "authority_kind"), (5, "degradation_reasons"), (6, "authority_ceiling_tool_names"));
        var forbiddenFragments = new[] { "body", "prompt", "tool_object", "token", "credential", "header", "model_argument", "diagnostic", "metadata", "adapter", "runtime_instance" };
        new[] { AgentProfileTurnAuthorityState.Descriptor, AgentProfileTurnAuthorityCommittedEvent.Descriptor }
            .SelectMany(descriptor => descriptor.Fields.InDeclarationOrder()).Select(field => field.Name)
            .Should().NotContain(name => forbiddenFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
    private static T RoundTrip<T>(T message, MessageParser<T> parser)
        where T : class, IMessage<T>, new()
    {
        var bytes = message.ToByteArray();
        var parsed = parser.ParseFrom(bytes);
        parsed.Should().Be(message);
        var merged = new T();
        merged.MergeFrom(message);
        merged.Should().Be(message);
        return parsed;
    }

}
