using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class RoleGAgentStateCoverageTests
{
    private static readonly MethodInfo ApplyClearPendingApprovalMethod = typeof(RoleGAgent)
        .GetMethod("ApplyClearPendingApproval", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyClearPendingApproval not found.");

    private static readonly MethodInfo ApplyChatSessionStartedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyChatSessionStarted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyChatSessionStarted not found.");

    private static readonly MethodInfo ApplyChatSessionCompletedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyChatSessionCompleted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyChatSessionCompleted not found.");

    private static readonly MethodInfo ClassifyToolResultMethod = typeof(RoleGAgent)
        .GetMethod("ClassifyToolResult", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ClassifyToolResult not found.");

    private static readonly MethodInfo ResolveRequestInputPartsMethod = typeof(RoleGAgent)
        .GetMethod("ResolveRequestInputParts", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ResolveRequestInputParts not found.");

    private static readonly MethodInfo BuildRequestPreviewMethod = typeof(RoleGAgent)
        .GetMethod("BuildRequestPreview", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildRequestPreview not found.");

    private static readonly MethodInfo DetectPendingApprovalFromHistoryMethod = typeof(RoleGAgent)
        .GetMethod("DetectPendingApprovalFromHistory", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("DetectPendingApprovalFromHistory not found.");

    private static readonly MethodInfo BuildContinuationPromptMethod = typeof(RoleGAgent)
        .GetMethod("BuildContinuationPrompt", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildContinuationPrompt not found.");

    private static readonly MethodInfo ApplyPendingApprovalMethod = typeof(RoleGAgent)
        .GetMethod("ApplyPendingApproval", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyPendingApproval not found.");

    private static readonly MethodInfo SanitizeFailureMessageMethod = typeof(RoleGAgent)
        .GetMethod("SanitizeFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SanitizeFailureMessage not found.");

    private static readonly MethodInfo ResolveTrackedSessionMethod = typeof(RoleGAgent)
        .GetMethod("ResolveTrackedSession", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ResolveTrackedSession not found.");

    private static readonly MethodInfo ExtractStateConfigOverridesMethod = typeof(RoleGAgent)
        .GetMethod("ExtractStateConfigOverrides", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ExtractStateConfigOverrides not found.");

    [Fact]
    public void ApplyClearPendingApproval_ShouldHandleMissingMismatchAndMatchBranches()
    {
        var empty = new RoleGAgentState();
        InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            empty,
            new ClearPendingApprovalEvent { RequestId = "req-1" })
            .Should()
            .BeSameAs(empty);

        var state = new RoleGAgentState
        {
            PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-1",
            },
        };

        var mismatched = InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            state,
            new ClearPendingApprovalEvent { RequestId = "req-2" });
        mismatched.PendingApproval.Should().NotBeNull();

        var cleared = InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            state,
            new ClearPendingApprovalEvent());
        cleared.PendingApproval.Should().BeNull();
    }

    [Fact]
    public void ApplyPendingApproval_ShouldStorePendingState()
    {
        var pending = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
        };

        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyPendingApprovalMethod,
            new RoleGAgentState(),
            new PendingToolApprovalPersistedEvent
            {
                Pending = pending,
            });

        next.PendingApproval.Should().NotBeNull();
        next.PendingApproval!.RequestId.Should().Be("req-1");
        next.PendingApproval.ToolName.Should().Be("dangerous_tool");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldIgnoreMissingOrMismatchedPendingApproval()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-approval-ignore");
        await agent.ActivateAsync();

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-1",
            Approved = false,
        });
        agent.State.PendingApproval.Should().BeNull();

        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-2",
            Approved = false,
        });

        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RequestId.Should().Be("req-1");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldClearPending_WhenDenied()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-approval-denied");
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-1",
            Approved = false,
            Reason = "user denied",
        });

        agent.State.PendingApproval.Should().BeNull();
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldExecuteToolAndDispatchContinuation_WhenApproved()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(
            provider,
            "role-approval-approved",
            toolSources:
            [
                new StaticToolSource(
                [
                    new DelegateTool("dangerous_tool", argumentsJson => $"RESULT:{argumentsJson}")
                ])
            ]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{\"value\":1}",
        };
        agent.State.PendingApproval.Metadata["nyxid.access_token"] = "token-1";

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-1",
            Approved = true,
            Reason = "approved",
        });

        agent.State.PendingApproval.Should().BeNull();
        AgentToolRequestContext.CurrentMetadata.Should().BeNull();
        publisher.Published
            .OfType<ChatRequestEvent>()
            .Should()
            .ContainSingle(x =>
                x.ScopeId == "session-a" &&
                x.Metadata["nyxid.access_token"] == "token-1" &&
                x.Prompt.Contains("dangerous_tool") &&
                x.Prompt.Contains("RESULT:{\"value\":1}"));
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldClearPendingAndRethrow_WhenContinuationDispatchFails()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(
            provider,
            "role-approval-dispatch-fails",
            toolSources:
            [
                new StaticToolSource(
                [
                    new DelegateTool("dangerous_tool", _ => "{\"ok\":true}")
                ])
            ]);
        agent.EventPublisher = new ThrowingEventPublisher();
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = "req-1",
                Approved = true,
            }))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");

        agent.State.PendingApproval.Should().BeNull();
        AgentToolRequestContext.CurrentMetadata.Should().BeNull();
    }

    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldIgnoreMissingOrMismatchedPendingApproval()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-timeout-ignore");
        await agent.ActivateAsync();

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().BeNull();

        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-2",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RequestId.Should().Be("req-1");
    }

    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldClearPending_WhenRemoteHandlerMissing()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-timeout-no-remote");
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().BeNull();
    }

    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldClearPending_WhenRemoteDenied()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-denied",
            new StubRemoteApprovalHandler(_ => Task.FromResult(ToolApprovalResult.Denied("not approved"))));
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        agent.State.PendingApproval.Metadata["nyxid.access_token"] = "token-1";

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().BeNull();
        AgentToolRequestContext.CurrentMetadata.Should().BeNull();
    }

    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldClearPending_WhenRemoteHandlerThrows()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-throws",
            new StubRemoteApprovalHandler(_ => throw new InvalidOperationException("remote failed")));
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().BeNull();
    }

    [Fact]
    public void ApplyChatSessionStateTransitions_ShouldAssignSequence_AndPreserveCompletedOutputs()
    {
        var started = InvokePrivateStatic<RoleGAgentState>(
            ApplyChatSessionStartedMethod,
            new RoleGAgentState(),
            new RoleChatSessionStartedEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Name = "photo.png",
                    },
                },
            });

        started.MessageCount.Should().Be(1);
        started.Sessions["session-a"].Sequence.Should().Be(1);
        started.Sessions["session-a"].Prompt.Should().Be("hello");
        started.Sessions["session-a"].InputParts.Should().ContainSingle();

        started.Sessions["session-a"].Sequence = 0;
        var completed = InvokePrivateStatic<RoleGAgentState>(
            ApplyChatSessionCompletedMethod,
            started,
            new RoleChatSessionCompletedEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                Content = "done",
                ReasoningContent = "because",
                ContentEmitted = true,
                ToolCalls =
                {
                    new ToolCallEvent
                    {
                        CallId = "call-1",
                        ToolName = "search",
                        ArgumentsJson = "{}",
                    },
                },
                OutputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Text,
                        Text = "done",
                    },
                },
            });

        completed.MessageCount.Should().Be(2);
        completed.Sessions["session-a"].Completed.Should().BeTrue();
        completed.Sessions["session-a"].FinalContent.Should().Be("done");
        completed.Sessions["session-a"].FinalReasoningContent.Should().Be("because");
        completed.Sessions["session-a"].ToolCalls.Should().ContainSingle(x => x.CallId == "call-1");
        completed.Sessions["session-a"].OutputParts.Should().ContainSingle(x => x.Text == "done");
    }

    [Fact]
    public void DetectPendingApprovalFromHistory_ShouldParseApprovalPayload_AndCopyMetadata()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-history-approval");
        var history = GetHistory(agent);
        history.Add(new ChatMessage
        {
            Role = "tool",
            Content = "{\"approval_required\":true,\"request_id\":\"req-1\",\"tool_name\":\"dangerous_tool\",\"tool_call_id\":\"call-1\",\"arguments\":\"{\\\"x\\\":1}\"}",
        });

        var request = new ChatRequestEvent
        {
            SessionId = "session-a",
        };
        request.Metadata["trace-id"] = "trace-1";

        var pending = InvokePrivateInstance<PendingToolApprovalState?>(
            DetectPendingApprovalFromHistoryMethod,
            agent,
            request);

        pending.Should().NotBeNull();
        pending!.RequestId.Should().Be("req-1");
        pending.SessionId.Should().Be("session-a");
        pending.ToolName.Should().Be("dangerous_tool");
        pending.ToolCallId.Should().Be("call-1");
        pending.ArgumentsJson.Should().Be("{\"x\":1}");
        pending.IsDestructive.Should().BeTrue();
        pending.Metadata["trace-id"].Should().Be("trace-1");
    }

    [Fact]
    public void DetectPendingApprovalFromHistory_ShouldIgnoreInvalidOrNonApprovalToolMessages()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-history-ignore");
        var history = GetHistory(agent);
        history.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "not a tool result",
        });
        history.Add(new ChatMessage
        {
            Role = "tool",
            Content = "not-json",
        });
        history.Add(new ChatMessage
        {
            Role = "tool",
            Content = "{\"approval_required\":false}",
        });

        InvokePrivateInstance<PendingToolApprovalState?>(
                DetectPendingApprovalFromHistoryMethod,
                agent,
                new ChatRequestEvent { SessionId = "session-a" })
            .Should()
            .BeNull();
    }

    [Fact]
    public void ClassifyToolResult_ShouldHandleNullStructuredErrorsAndInvalidJson()
    {
        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            (object?)null)
            .Should()
            .Be((true, null));

        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            "{\"ok\":true}")
            .Should()
            .Be((true, null));

        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            "{\"error\":\"  boom  \"}")
            .Should()
            .Be((false, "boom"));

        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            "{\"error\":{\"code\":\"boom\"}}")
            .Should()
            .Be((false, "{\"code\":\"boom\"}"));

        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            "{\"error\":null}")
            .Should()
            .Be((true, null));

        InvokePrivateStatic<(bool Success, string? Error)>(
            ClassifyToolResultMethod,
            "{not-json")
            .Should()
            .Be((true, null));
    }

    [Fact]
    public void BuildContinuationPrompt_AndSanitizeFailureMessage_ShouldHandleFallbackBranches()
    {
        var prompt = InvokePrivateStatic<string>(
            BuildContinuationPromptMethod,
            new PendingToolApprovalState
            {
                ToolName = "dangerous_tool",
            },
            (string?)null);

        prompt.Should().Contain("dangerous_tool");
        prompt.Should().Contain("(no output)");

        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, "  boom  ").Should().Be("boom");
        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, " ").Should().Be("LLM request failed.");
        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, (object?)null).Should().Be("LLM request failed.");
    }

    [Fact]
    public void ResolveRequestInputParts_AndBuildRequestPreview_ShouldRespectPromptAndMediaBranches()
    {
        var multimodalRequest = new ChatRequestEvent
        {
            Prompt = "describe this",
        };
        multimodalRequest.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Name = "photo.png",
        });

        var parts = InvokePrivateStatic<IReadOnlyList<ContentPart>>(
            ResolveRequestInputPartsMethod,
            multimodalRequest);
        parts.Should().HaveCount(2);
        parts[0].Kind.Should().Be(ContentPartKind.Text);
        parts[1].Kind.Should().Be(ContentPartKind.Image);

        InvokePrivateStatic<string>(BuildRequestPreviewMethod, multimodalRequest)
            .Should()
            .Be("describe this");

        var promptlessRequest = new ChatRequestEvent();
        promptlessRequest.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Video,
            Name = "clip.mp4",
        });

        InvokePrivateStatic<string>(BuildRequestPreviewMethod, promptlessRequest)
            .Should()
            .Be("video");

        InvokePrivateStatic<IReadOnlyList<ContentPart>>(
                ResolveRequestInputPartsMethod,
                new ChatRequestEvent())
            .Should()
            .ContainSingle(x => x.Kind == ContentPartKind.Text && x.Text == string.Empty);
    }

    [Fact]
    public async Task GetDescriptionAsync_ShouldIncludeRoleNameAndActorId()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-description");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "helper",
        });

        (await agent.GetDescriptionAsync()).Should().Be("RoleGAgent[helper]:role-description");
    }

    [Fact]
    public void ResolveTrackedSession_ShouldReturnMatch_AndRejectPromptOrInputMismatch()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-session-state");
        agent.State.Sessions["session-a"] = new RoleChatSessionState
        {
            Prompt = "hello",
            Sequence = 1,
        };
        agent.State.Sessions["session-a"].InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Name = "photo.png",
        });

        InvokePrivateInstance<RoleChatSessionState?>(
            ResolveTrackedSessionMethod,
            agent,
            new ChatRequestEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Name = "photo.png",
                    },
                },
            })
            .Should()
            .NotBeNull();

        FluentActions.Invoking(() => InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent
                {
                    SessionId = "session-a",
                    Prompt = "bye",
                }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*different prompt*");

        FluentActions.Invoking(() => InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent
                {
                    SessionId = "session-a",
                    Prompt = "hello",
                    InputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Audio,
                            Name = "voice.wav",
                        },
                    },
                }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*different multimodal input*");

        InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent())
            .Should()
            .BeNull();
    }

    [Fact]
    public void ExtractStateConfigOverrides_ShouldReturnEmpty_WhenStateHasNoOverrides()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-config-empty");

        var overrides = InvokePrivateInstance<object>(
            ExtractStateConfigOverridesMethod,
            agent,
            new RoleGAgentState());

        GetProperty<string?>(overrides, "ProviderName").Should().BeNull();
        GetProperty<string?>(overrides, "Model").Should().BeNull();
        GetProperty<string?>(overrides, "SystemPrompt").Should().BeNull();
        GetProperty<double?>(overrides, "Temperature").Should().BeNull();
        GetProperty<int?>(overrides, "MaxTokens").Should().BeNull();
        GetProperty<bool?>(overrides, "EnableSummarization").Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeRoleAgent_ShouldNormalizeExtensions_AndExposeAdditionalOverrides()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-config-full");
        await agent.ActivateAsync();

        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "worker",
            ProviderName = "mock",
            Model = "model-a",
            SystemPrompt = "be helpful",
            EventModules = "  module-a  ",
            EventRoutes = "  route-a  ",
            MaxPromptTokenBudget = 2048,
            CompressionThreshold = 512,
            EnableSummarization = true,
        });

        agent.State.EventModules.Should().Be("module-a");
        agent.State.EventRoutes.Should().Be("route-a");
        agent.EffectiveConfig.MaxPromptTokenBudget.Should().Be(2048);
        agent.EffectiveConfig.CompressionThreshold.Should().Be(0.99);
        agent.EffectiveConfig.EnableSummarization.Should().BeTrue();

        var overrides = InvokePrivateInstance<object>(
            ExtractStateConfigOverridesMethod,
            agent,
            agent.State);
        GetProperty<string?>(overrides, "ProviderName").Should().Be("mock");
        GetProperty<string?>(overrides, "Model").Should().Be("model-a");
        GetProperty<string?>(overrides, "SystemPrompt").Should().Be("be helpful");
        GetProperty<int?>(overrides, "MaxPromptTokenBudget").Should().Be(2048);
        GetProperty<double?>(overrides, "CompressionThreshold").Should().Be(512);
        GetProperty<bool?>(overrides, "EnableSummarization").Should().BeTrue();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static RoleGAgent CreateRoleAgent(
        IServiceProvider provider,
        string actorId,
        IToolApprovalHandler? remoteApprovalHandler = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
    {
        var agent = new TestRoleGAgent(remoteApprovalHandler, toolSources ?? Enumerable.Empty<IAgentToolSource>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }

    private static ChatHistory GetHistory(RoleGAgent agent)
    {
        return (ChatHistory)typeof(AIGAgentBase<RoleGAgentState>)
            .GetProperty("History", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
    }

    private static T InvokePrivateStatic<T>(MethodInfo method, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static T InvokePrivateInstance<T>(MethodInfo method, object instance, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(instance, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static T? GetProperty<T>(object instance, string propertyName)
    {
        return (T?)instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance);
    }

    private sealed class TestRoleGAgent(
        IToolApprovalHandler? remoteApprovalHandler,
        IEnumerable<IAgentToolSource> toolSources)
        : RoleGAgent(toolSources: toolSources)
    {
        protected override IToolApprovalHandler? ResolveRemoteApprovalHandler() => remoteApprovalHandler;
    }

    private sealed class StubRemoteApprovalHandler(Func<ToolApprovalRequest, Task<ToolApprovalResult>> handler)
        : IToolApprovalHandler
    {
        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct) =>
            handler(request);
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }

    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            throw new InvalidOperationException("dispatch failed");
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = evt;
            _ = audience;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            return Task.CompletedTask;
        }
    }
}
