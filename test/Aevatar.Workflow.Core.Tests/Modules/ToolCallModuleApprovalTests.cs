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

public sealed class ToolCallModuleApprovalTests
{
    [Fact]
    public async Task PendingApproval_ShouldPersistStateAndPublishSuspensionWithoutCompletion()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-approval");

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1",
            [fileRef],
            "idem-approval-1");

        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var suspended = ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().ContainSingle().Subject;
        suspended.RunId.Should().Be("run-1");
        suspended.StepId.Should().Be("danger_step");
        suspended.SuspensionType.Should().Be("tool_approval");
        suspended.ToolApproval.Should().NotBeNull();
        suspended.ToolApproval.ExecutionId.Should().Be("exec-1");
        suspended.ToolApproval.ToolName.Should().Be("danger");
        suspended.ToolApproval.ToolCallId.Should().Be("workflow:run-1:danger_step:exec-1");
        suspended.ToolApproval.ApprovalRequestId.Should().Be("approval-1");
        var state = ctx.LoadState<ToolCallModuleState>("tool_call");
        state.PendingApprovals.Should().ContainKey("run-1:danger_step:exec-1:workflow:run-1:danger_step:exec-1:approval-1");
        var pendingState = state.PendingApprovals.Values.Should().ContainSingle().Subject;
        pendingState.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-approval");
        pendingState.IdempotencyKey.Should().Be("idem-approval-1");
    }

    [Fact]
    public async Task ApprovedResume_ShouldReplayOriginalToolArgumentsWithTypedGrantAndClearPendingState()
    {
        var issuedAt = new DateTimeOffset(2026, 7, 31, 10, 11, 12, TimeSpan.Zero);
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Success("""{"executed":true}"""));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-replay");

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1",
            [fileRef],
            "idem-approval-1",
            issuedAt);
        ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.IssuedAtUnixMs.Should().Be(issuedAt.ToUnixTimeMilliseconds());
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx,
            CancellationToken.None);

        tool.Requests.Should().HaveCount(2);
        tool.Requests[1].ArgumentsJson.Should().Be("""{"danger":true}""");
        tool.Requests.Select(request => request.IssuedAtUnixMs)
            .Should().OnlyContain(value => value == issuedAt.ToUnixTimeMilliseconds());
        tool.Requests.Count(request => request.ApprovalGrant is not null).Should().Be(1);
        tool.Requests[1].InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-replay");
        tool.Requests[1].IdempotencyKey.Should().Be("idem-approval-1");
        tool.Requests[1].ApprovalGrant.Should().NotBeNull();
        var grant = tool.Requests[1].ApprovalGrant!;
        grant.ApprovalRequestId.Should().Be("approval-1");
        grant.ToolName.Should().Be("danger");
        grant.ToolCallId.Should().Be("workflow:run-1:danger_step:exec-1");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().Success.Should().BeTrue();
        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"executed":true}""");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedResume_WhenToolReturnsTypedFailure_ShouldPublishFailedToolAndStepOutcomes()
    {
        const string resultJson = """{"error":true,"status":503}""";
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Failed(
                    resultJson,
                    "NYXID_PROXY_HTTP_503",
                    "The service request failed."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx,
            CancellationToken.None);

        var toolCompleted = ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Single();
        toolCompleted.Success.Should().BeFalse();
        toolCompleted.ResultJson.Should().Be(resultJson);
        toolCompleted.Error.Should().Contain("NYXID_PROXY_HTTP_503");

        var stepCompleted = ctx.Published.Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        stepCompleted.Success.Should().BeFalse();
        stepCompleted.Output.Should().Be(resultJson);
        stepCompleted.Error.Should().Contain("The service request failed.");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedResume_WhenPreTerminalFailureIsRetryable_ShouldKeepPendingAndRetryTurn()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Failed(
                    """{"error":"tool_admission_unavailable"}""",
                    "tool_admission_unavailable",
                    "The durable tool admission ledger is unavailable.",
                    terminalInvoked: false,
                    retryable: true));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1");
        ctx.Published.Clear();

        var action = () => module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The durable tool admission ledger is unavailable.");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task RejectedResume_ShouldFailClosedAndClearPendingState()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, "danger_step", """{"danger":true}""", "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = false,
                Feedback = "blocked",
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx,
            CancellationToken.None);

        tool.Requests.Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().Success.Should().BeFalse();
        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("approval rejected");
        completed.Error.Should().Contain("blocked");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task MismatchedResume_ShouldIgnoreWithoutClearingPendingState()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, "danger_step", """{"danger":true}""", "exec-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "other-approval",
                },
            }),
            ctx,
            CancellationToken.None);

        tool.Requests.Should().ContainSingle();
        ctx.Published.Should().BeEmpty();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().ContainSingle();
    }

    private static ToolCallModule CreateModule(IWorkflowTool tool) =>
        new([new SingleToolSource(tool)], NullLogger<ToolCallModule>.Instance);

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId,
        string input,
        string executionId,
        IReadOnlyList<WorkflowFileRef>? inputFileRefs = null,
        string idempotencyKey = "",
        DateTimeOffset? issuedAt = null)
    {
        var request = new StepRequestEvent
        {
            StepId = stepId,
            StepType = "tool_call",
            RunId = ctx.RunId,
            ExecutionId = executionId,
            IdempotencyKey = idempotencyKey,
            Input = input,
            Parameters = { ["tool"] = toolName },
        };
        request.InputFileRefs.Add(inputFileRefs?.Select(static fileRef => fileRef.Clone()) ?? []);

        await module.HandleAsync(
            Envelope(request, issuedAt),
            ctx,
            CancellationToken.None);
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"artifact-{fileId}",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
        };

    private static EventEnvelope Envelope(IMessage evt, DateTimeOffset? issuedAt = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(issuedAt ?? DateTimeOffset.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class ScriptedWorkflowTool(
        string name,
        Func<WorkflowToolExecutionRequest, WorkflowToolExecutionResult> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public List<WorkflowToolExecutionRequest> Requests { get; } = [];

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(execute(request));
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
        public string ScopeId => "scope-1";
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

        public Any? GetExecutionState(string scopeKey) => _states.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) =>
            ClearStateAsync(scopeKey, ct);

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = delta;
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = options;
            return Task.FromResult(new RuntimeCallbackLease(AgentId, "callback-1", 1, RuntimeCallbackBackend.InMemory));
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
