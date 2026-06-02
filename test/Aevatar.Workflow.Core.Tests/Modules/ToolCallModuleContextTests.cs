using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleContextTests
{
    [Fact]
    public async Task ToolCallModule_ShouldPushWorkflowToolContext()
    {
        var tool = new FakeAgentTool(
            "context_reader",
            _ => AgentToolRequestContext.NyxIdAccessToken ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token-123",
                },
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        LastCompleted(ctx).Output.Should().Be("token-123");
    }

    [Fact]
    public async Task ToolCallModule_ShouldForwardOrgToken()
    {
        var tool = new FakeAgentTool(
            "org_context_reader",
            _ => AgentToolRequestContext.NyxIdOrgToken ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdOrgToken = "org-token-123",
                },
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        LastCompleted(ctx).Output.Should().Be("org-token-123");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetCallIdFromWorkflowExecutionIdentity()
    {
        var tool = new FakeAgentTool(
            "identity_reader",
            _ => $"{AgentToolRequestContext.RequestId}|{AgentToolRequestContext.CallId}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(ctx, AgentToolExecutionContext.Empty);

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        LastCompleted(ctx).Output.Should().Be("|workflow:run-1:call_proxy:exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldOverrideExistingCallIdWithWorkflowExecutionIdentity()
    {
        var tool = new FakeAgentTool(
            "call_id_reader",
            _ => AgentToolRequestContext.CallId ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-123", "upstream-call"),
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        LastCompleted(ctx).Output.Should().Be("workflow:run-1:call_proxy:exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldUseRunAndStepWhenExecutionIdIsMissing()
    {
        var tool = new FakeAgentTool(
            "call_id_reader",
            _ => AgentToolRequestContext.CallId ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-123", "upstream-call"),
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy");

        LastCompleted(ctx).Output.Should().Be("workflow:run-1:call_proxy");
    }

    [Fact]
    public async Task ToolCallModule_ShouldPublishToolEventsWithWorkflowExecutionCallId()
    {
        var tool = new FakeAgentTool("call_id_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        ctx.Published.Select(x => x.Event).OfType<ToolCallEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
        ctx.Published.Select(x => x.Event).OfType<ToolResultEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdOnStepCompletion()
    {
        var tool = new FakeAgentTool("execution_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        LastCompleted(ctx).ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdOnFailureStepCompletion()
    {
        var tool = new FakeAgentTool("failing_tool", _ => throw new InvalidOperationException("boom"));
        var port = new FakeExecutionPort(AgentToolExecutionResult.Failed("boom"));
        var module = CreateModule(tool, port);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdWhenToolParameterIsMissing()
    {
        var tool = new FakeAgentTool("unused", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "call_proxy",
                StepType = "tool_call",
                RunId = ctx.RunId,
                ExecutionId = "exec-1",
                Input = "{}",
            }),
            ctx,
            CancellationToken.None);

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldRestorePreviousAgentToolContext()
    {
        var tool = new FakeAgentTool(
            "restore_reader",
            _ => AgentToolRequestContext.NyxIdAccessToken ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "inner-token",
                },
            });
        var outer = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("outer-request", "outer-call"),
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "outer-token",
            },
        };

        using (AgentToolContextScope.Push(outer))
        {
            await ExecuteToolCallAsync(module, ctx, tool.Name);

            AgentToolRequestContext.Current.Should().BeSameAs(outer);
        }

        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task ToolCallModule_ShouldRestoreAgentToolContextBeforePublishingWorkflowEvents()
    {
        var tool = new FakeAgentTool(
            "publish_context_reader",
            _ =>
            {
                AgentToolRequestContext.NyxIdAccessToken.Should().Be("inner-token");
                return "ok";
            });
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "inner-token",
                },
            });
        var outer = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("outer-request", "outer-call"),
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "outer-token",
            },
        };

        using (AgentToolContextScope.Push(outer))
        {
            await ExecuteToolCallAsync(module, ctx, tool.Name);
        }

        ctx.PublishedToolContexts.Should().NotBeEmpty();
        ctx.PublishedToolContexts.Should().AllSatisfy(context => context.Should().BeSameAs(outer));
    }

    [Fact]
    public async Task ToolCallModule_WithoutToolContext_ShouldStillExecuteTools()
    {
        var tool = new FakeAgentTool("echo", argumentsJson => argumentsJson);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, input: """{"msg":"ok"}""");

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"msg":"ok"}""");
    }

    [Fact]
    public async Task ToolCallModule_ShouldNotReusePreviousRequestTokenWhenNextRequestHasNoToolContext()
    {
        var tool = new FakeAgentTool(
            "token_reader",
            _ => AgentToolRequestContext.NyxIdAccessToken ?? string.Empty);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token-123",
                },
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "first_call");
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(ctx, null);
        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "second_call");

        var completions = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().ToList();
        completions[0].Output.Should().Be("token-123");
        completions[1].Output.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_NyxIdLikeToolWithoutToken_ShouldReturnAuthenticationError()
    {
        var tool = new NyxIdLikeTool();
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        LastCompleted(ctx).Output.Should()
            .Be("""{"error":"No NyxID access token available. User must be authenticated."}""");
        tool.CapturedToken.Should().BeNull();
    }

    [Fact]
    public async Task ToolCallModule_NyxIdLikeToolWithTypedToken_ShouldEnterProxyPath()
    {
        var tool = new NyxIdLikeTool();
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token-123",
                    NyxIdOrgToken = "org-token-123",
                },
            });

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.CapturedToken.Should().Be("token-123");
        tool.CapturedOrgToken.Should().Be("org-token-123");
        LastCompleted(ctx).Output.Should().Be("""{"proxied":true}""");
    }

    [Fact]
    public async Task ToolCallModule_WhenExecutionPortMissing_ShouldFailClosedAndSkipRawToolExecution()
    {
        var tool = new CountingAgentTool("safe_tool", _ => """{"raw":true}""");
        var module = new ToolCallModule(
            [new SingleToolSource(tool)],
            NullLogger<ToolCallModule>.Instance,
            executionPort: null);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.ExecuteCalls.Should().Be(0);
        var toolResult = ctx.Published.Select(x => x.Event).OfType<ToolResultEvent>().Single();
        toolResult.Success.Should().BeFalse();
        toolResult.Error.Should().Contain("execution port is not configured");
        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("execution port is not configured");
    }

    [Fact]
    public async Task ToolCallModule_ShouldUseExecutionPortAndNotCallToolDirectly()
    {
        var tool = new CountingAgentTool("ported_tool", _ => """{"raw":true}""");
        var port = new FakeExecutionPort(AgentToolExecutionResult.Succeeded("""{"ported":true}"""));
        var module = CreateModule(tool, port);
        var ctx = new RecordingWorkflowContext();
        WorkflowToolExecutionRuntimeContextAccess.SetToolContext(
            ctx,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "token-123",
                },
            });

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            stepId: "ported_step",
            input: """{"x":1}""",
            executionId: "exec-ported");

        tool.ExecuteCalls.Should().Be(0);
        port.Requests.Should().ContainSingle();
        var request = port.Requests.Single();
        request.Tool.Should().BeSameAs(tool);
        request.ToolName.Should().Be("ported_tool");
        request.ToolCallId.Should().Be("workflow:run-1:ported_step:exec-ported");
        request.ArgumentsJson.Should().Be("""{"x":1}""");
        request.ExecutionContext.Credentials.NyxIdAccessToken.Should().Be("token-123");
        request.ExecutionContext.Request.CallId.Should().Be("workflow:run-1:ported_step:exec-ported");
        LastCompleted(ctx).Success.Should().BeTrue();
        LastCompleted(ctx).Output.Should().Be("""{"ported":true}""");
    }

    [Theory]
    [InlineData(AgentToolExecutionStatus.ApprovalDenied)]
    [InlineData(AgentToolExecutionStatus.ApprovalTimedOut)]
    [InlineData(AgentToolExecutionStatus.MiddlewareTerminated)]
    [InlineData(AgentToolExecutionStatus.Failed)]
    public async Task ToolCallModule_WhenPortReturnsNonSuccessStatus_ShouldPublishFailedEvents(
        AgentToolExecutionStatus status)
    {
        var tool = new CountingAgentTool("blocked_tool", _ => """{"raw":true}""");
        var port = new FakeExecutionPort(new AgentToolExecutionResult(status, null, $"{status} blocked"));
        var module = CreateModule(tool, port);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.ExecuteCalls.Should().Be(0);
        var toolResult = ctx.Published.Select(x => x.Event).OfType<ToolResultEvent>().Single();
        toolResult.Success.Should().BeFalse();
        toolResult.Error.Should().Contain($"{status} blocked");
        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain($"{status} blocked");
    }

    [Fact]
    public async Task ToolCallModule_WhenPortReturnsApprovalPending_ShouldPersistPendingApprovalAndSuspend()
    {
        var tool = new CountingAgentTool("approval_tool", _ => """{"raw":true}""");
        var port = new FakeExecutionPort(new AgentToolExecutionResult(
            AgentToolExecutionStatus.ApprovalPending,
            """{"approval_required":true,"request_id":"approval-123"}""",
            "approval pending"));
        var module = CreateModule(tool, port);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            stepId: "approval_step",
            input: """{"danger":true}""",
            executionId: "approval-exec");

        tool.ExecuteCalls.Should().Be(0);
        ctx.Published.Select(x => x.Event).OfType<ToolResultEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var suspended = ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().ContainSingle().Subject;
        suspended.ToolApproval.Should().NotBeNull();
        suspended.ToolApproval.ApprovalRequestId.Should().Be("approval-123");
        suspended.ToolApproval.ToolCallId.Should().Be("workflow:run-1:approval_step:approval-exec");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().ContainSingle();
    }

    private static ToolCallModule CreateModule(
        IAgentTool tool,
        IAgentToolExecutionPort? executionPort = null) =>
        new(
            [new SingleToolSource(tool)],
            NullLogger<ToolCallModule>.Instance,
            executionPort ?? new DirectFakeExecutionPort());

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId = "call_proxy",
        string input = "{}",
        string executionId = "")
    {
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = stepId,
                StepType = "tool_call",
                RunId = ctx.RunId,
                ExecutionId = executionId,
                Input = input,
                Parameters = { ["tool"] = toolName },
            }),
            ctx,
            CancellationToken.None);
    }

    private static StepCompletedEvent LastCompleted(RecordingWorkflowContext ctx) =>
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Last();

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };
    }

    private sealed class FakeAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "fake tool";

        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class CountingAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "counting fake tool";

        public string ParametersSchema => "{}";

        public int ExecuteCalls { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class NyxIdLikeTool : IAgentTool
    {
        public string Name => "nyxid_like_proxy";

        public string Description => "fake NyxID-like tool";

        public string ParametersSchema => "{}";

        public string? CapturedToken { get; private set; }

        public string? CapturedOrgToken { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = argumentsJson;
            CapturedToken = AgentToolRequestContext.NyxIdAccessToken;
            CapturedOrgToken = AgentToolRequestContext.NyxIdOrgToken;
            if (string.IsNullOrWhiteSpace(CapturedToken))
                return Task.FromResult("""{"error":"No NyxID access token available. User must be authenticated."}""");

            return Task.FromResult("""{"proxied":true}""");
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class DirectFakeExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var _ = AgentToolContextScope.Push(request.ExecutionContext);
            var result = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            return AgentToolExecutionResult.Succeeded(result);
        }
    }

    private sealed class FakeExecutionPort(AgentToolExecutionResult result) : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext, IWorkflowExecutionRuntimeContextAccessor, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<AgentToolExecutionContext?> PublishedToolContexts { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) =>
            ClearStateAsync(scopeKey, ct);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (delta.ClearLlm)
                ExecutionContextState.Llm = null;
            if (delta.ClearConnector)
                ExecutionContextState.Connector = null;
            if (delta.Llm != null)
            {
                ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
                {
                    NyxidAccessToken = delta.Llm.NyxidAccessToken,
                    ModelOverride = delta.Llm.ModelOverride,
                    NyxidRoutePreference = delta.Llm.NyxidRoutePreference,
                };
            }

            if (delta.Connector != null)
            {
                ExecutionContextState.Connector = new WorkflowConnectorExecutionContextState
                {
                    HttpAuthorization = delta.Connector.HttpAuthorization,
                };
            }

            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionContextState.Llm = null;
            ExecutionContextState.Connector = null;
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, direction));
            PublishedToolContexts.Add(AgentToolRequestContext.Current);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = targetActorId;
            _ = evt;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = dueTime;
            _ = evt;
            _ = options;
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            ScheduleSelfDurableTimeoutAsync(callbackId, dueTime + period, evt, options, ct);

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = lease;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
