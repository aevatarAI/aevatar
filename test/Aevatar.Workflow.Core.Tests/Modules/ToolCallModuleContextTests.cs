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
    public async Task ToolCallModule_ShouldPublishToolEventsWithWorkflowExecutionCallId()
    {
        var tool = new FakeAgentTool("call_id_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().CallId
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
        var module = CreateModule(tool);
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
    public async Task ToolCallModule_ShouldExecuteTypedWorkflowToolRequest()
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
    public async Task ToolCallModule_ShouldPreferExplicitArgumentsParameter()
    {
        var tool = new CapturingWorkflowTool("echo");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "call_proxy",
                StepType = "tool_call",
                RunId = ctx.RunId,
                Input = """{"from":"input"}""",
                Parameters =
                {
                    ["tool"] = tool.Name,
                    ["arguments"] = """{"from":"parameters"}""",
                },
            }),
            ctx,
            CancellationToken.None);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ArgumentsJson.Should().Be("""{"from":"parameters"}""");
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldPassTypedWorkflowToolExecutionRequestToDirectTool()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            BearerToken = " typed-token ",
        };
        ctx.ExecutionContextState.WorkflowRuntime = new WorkflowToolRuntimeContextState
        {
            ParentActorId = "parent-actor",
            ParentRunId = "parent-run",
            ParentStepId = "parent-step",
            RootRunId = "root-run",
            Depth = 2,
        };
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
        });

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            input: """{"operation":"read"}""",
            executionId: "exec-1");

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ArgumentsJson.Should().Be("""{"operation":"read"}""");
        tool.LastRequest.RunId.Should().Be("run-1");
        tool.LastRequest.StepId.Should().Be("call_proxy");
        tool.LastRequest.ExecutionId.Should().Be("exec-1");
        tool.LastRequest.CallId.Should().Be("workflow:run-1:call_proxy:exec-1");
        tool.LastRequest.ScopeId.Should().Be("scope-1");
        tool.LastRequest.CallerCredential.BearerToken.Should().Be("typed-token");
        tool.LastRequest.RuntimeContext.ParentActorId.Should().Be("agent-1");
        tool.LastRequest.RuntimeContext.ParentRunId.Should().Be("run-1");
        tool.LastRequest.RuntimeContext.ParentStepId.Should().Be("call_proxy");
        tool.LastRequest.RuntimeContext.RootRunId.Should().Be("root-run");
        tool.LastRequest.RuntimeContext.Depth.Should().Be(2);
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenToolReturnsManagedHandoff_ShouldLeaveParentStepPending()
    {
        var handoff = new WorkflowManagedHandoffOutcome
        {
            ParentActorId = "parent-actor",
            ParentRunId = "run-1",
            ParentStepId = "call_proxy",
            InvocationId = "run-1:workflow_tool:call_proxy:call-1",
            ChildRunId = "run-1:workflow_tool:call_proxy:call-1",
        };
        var tool = new ManagedHandoffWorkflowTool("aevatar_start_workflow", handoff);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        var completed = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.ManagedHandoff.Should().NotBeNull();
        completed.ManagedHandoff.InvocationId.Should().Be(handoff.InvocationId);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_ShouldFallbackToEmptyScopeIdWhenContextScopeIdIsNull()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { ScopeIdOverride = null };

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ScopeId.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldNotUseRequestMetadataAsCallerCredential()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
        });

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.CallerCredential.BearerToken.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenToolApprovalPending_ShouldPersistPendingApprovalAndSuspendWithoutCompletingStep()
    {
        var tool = new ApprovalPendingWorkflowTool("dangerous_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, input: """{"danger":true}""", executionId: "exec-1");

        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var suspended = ctx.Published.Single(x => x.Event is WorkflowSuspendedEvent).Event
            .Should().BeOfType<WorkflowSuspendedEvent>().Subject;
        suspended.RunId.Should().Be("run-1");
        suspended.StepId.Should().Be("call_proxy");
        suspended.SuspensionType.Should().Be("tool_approval");
        suspended.ToolApproval.Should().NotBeNull();
        suspended.ToolApproval.ExecutionId.Should().Be("exec-1");
        suspended.ToolApproval.ToolName.Should().Be(tool.Name);
        suspended.ToolApproval.ToolCallId.Should().Be("workflow:run-1:call_proxy:exec-1");
        suspended.ToolApproval.ApprovalRequestId.Should().Be("approval-1");
        suspended.ToolApproval.ArgumentsJson.Should().Be("""{"danger":true}""");
        ctx.Published.Single(x => x.Event is WorkflowSuspendedEvent).Direction
            .Should().Be(TopologyAudience.ParentAndChildren);
        var state = ctx.LoadState<ToolCallModuleState>("tool_call");
        state.PendingApprovals.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenApprovalResumeApproved_ShouldReplayOriginalToolWithGrantAndCompleteStep()
    {
        var tool = new ApprovalPendingWorkflowTool("dangerous_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, input: """{"danger":true}""", executionId: "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "call_proxy",
                ExecutionId = "exec-1",
                ApprovalRequestId = "approval-1",
                Approved = true,
            }),
            ctx,
            CancellationToken.None);

        tool.ExecuteCalls.Should().Be(2);
        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ApprovalGrant.Should().NotBeNull();
        tool.LastRequest.ApprovalGrant!.ApprovalRequestId.Should().Be("approval-1");
        tool.LastRequest.ApprovalGrant.Approved.Should().BeTrue();
        tool.LastRequest.ArgumentsJson.Should().Be("""{"danger":true}""");
        var completed = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.ResultJson.Should().Be("""{"approved":true}""");
        LastCompleted(ctx).Success.Should().BeTrue();
        LastCompleted(ctx).ExecutionId.Should().Be("exec-1");
        LastCompleted(ctx).Output.Should().Be("""{"approved":true}""");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenApprovalResumeRejected_ShouldClearStateAndPublishFailedCompletion()
    {
        var tool = new ApprovalPendingWorkflowTool("dangerous_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "call_proxy",
                ExecutionId = "exec-1",
                ApprovalRequestId = "approval-1",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var toolCompleted = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single();
        toolCompleted.Success.Should().BeFalse();
        toolCompleted.Error.Should().Contain("tool approval rejected");
        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.ExecutionId.Should().Be("exec-1");
        completed.Error.Should().Contain("tool approval rejected");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenApprovalResumeMissingTypedKeys_ShouldIgnoreResumeAndKeepPendingState()
    {
        var tool = new ApprovalPendingWorkflowTool("dangerous_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "call_proxy",
                Approved = true,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().ContainSingle();
        tool.ExecuteCalls.Should().Be(1);
    }

    [Fact]
    public void IWorkflowTool_ShouldExposeOnlyTypedWorkflowExecutionMethod()
    {
        var executeMethods = typeof(IWorkflowTool)
            .GetMethods()
            .Where(method => method.Name == nameof(IWorkflowTool.ExecuteAsync))
            .ToList();

        executeMethods.Should().ContainSingle();
        executeMethods[0].GetParameters().First().ParameterType.Should().Be(typeof(WorkflowToolExecutionRequest));
        executeMethods[0].GetParameters().Should().NotContain(parameter => parameter.ParameterType == typeof(string));
    }

    private static ToolCallModule CreateModule(IWorkflowTool tool) =>
        new(
            [new SingleToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);

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

    private sealed class FakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class CountingAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class CapturingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public WorkflowToolExecutionRequest? LastRequest { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(WorkflowToolExecutionResult.Success("""{"typed":true}"""));
        }
    }

    private sealed class ApprovalPendingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public WorkflowToolExecutionRequest? LastRequest { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            LastRequest = request;
            if (request.ApprovalGrant?.Approved == true)
                return Task.FromResult(WorkflowToolExecutionResult.Success("""{"approved":true}"""));

            return Task.FromResult(WorkflowToolExecutionResult.PendingApproval(new WorkflowToolApprovalPendingOutcome(
                "approval-1",
                Name,
                request.CallId,
                request.ArgumentsJson)));
        }
    }

    private sealed class ManagedHandoffWorkflowTool(string name, WorkflowManagedHandoffOutcome handoff) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowToolExecutionResult("""{"status":"accepted"}""", handoff));
        }
    }

    private sealed class SingleToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
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

        public string? ScopeIdOverride { get; init; } = "scope-1";

        public string ScopeId => ScopeIdOverride!;

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

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
            if (delta.ClearCallerCredential)
                ExecutionContextState.CallerCredential = null;
            if (delta.Llm != null)
            {
                ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
                {
                    ModelOverride = delta.Llm.ModelOverride,
                    UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                    RoutePreference = delta.Llm.RoutePreference,
                };
                if (delta.Llm.HasMaxToolRoundsOverride)
                    ExecutionContextState.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
            }

            if (delta.CallerCredential != null)
            {
                ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
                {
                    BearerToken = delta.CallerCredential.BearerToken,
                };
            }

            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
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
