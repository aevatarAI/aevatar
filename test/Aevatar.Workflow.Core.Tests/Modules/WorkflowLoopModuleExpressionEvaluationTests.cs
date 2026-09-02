using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public class WorkflowLoopModuleExpressionEvaluationTests
{
    [Fact]
    public async Task DispatchStep_ShouldEvaluateExpressionsInParameters()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "first",
                    Type = "transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["value"] = "${concat('v=', input)}",
                    },
                },
                new StepDefinition
                {
                    Id = "second",
                    Type = "transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["value"] = "${concat('prev=', first, ', input=', input)}",
                    },
                },
            ],
        };

        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);
        const string runId = "run-1";

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = runId,
            Input = "hello",
        }), ctx, CancellationToken.None);

        var firstReq = ctx.Published.Single(x => x.Event is StepRequestEvent).Event as StepRequestEvent;
        firstReq.Should().NotBeNull();
        firstReq!.StepId.Should().Be("first");
        firstReq.Input.Should().Be("hello");
        firstReq.Parameters["value"].Should().Be("v=hello");

        ctx.Published.Clear();

        await module.HandleAsync(Wrap(new StepCompletedEvent
        {
            StepId = "first",
            RunId = runId,
            Success = true,
            Output = "out1",
        }), ctx, CancellationToken.None);

        var secondReq = ctx.Published.Single(x => x.Event is StepRequestEvent).Event as StepRequestEvent;
        secondReq.Should().NotBeNull();
        secondReq!.StepId.Should().Be("second");
        secondReq.Input.Should().Be("out1");
        secondReq.Parameters["value"].Should().Be("prev=out1, input=out1");
    }

    [Fact]
    public async Task DispatchStep_ShouldPreserveEscapedExpressionOpenInParameters()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "tool",
                    Type = "tool_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["code"] = """
                            const user = `$${event.user_id}`;
                            echo "$${HOME}"
                            """,
                        ["mixed"] = "literal=$${input}; evaluated=${input}",
                    },
                },
            ],
        };

        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-escaped",
            Input = "hello",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event
            .Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters["code"].Should().Contain("const user = `${event.user_id}`;");
        request.Parameters["code"].Should().Contain("echo \"${HOME}\"");
        request.Parameters["mixed"].Should().Be("literal=${input}; evaluated=hello");
    }

    [Fact]
    public async Task DispatchStep_ShouldEvaluateTypedTransformOperationBeforeDispatch()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "group",
                    Type = "transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["op"] = "group_by",
                        ["group_by"] = "${input}",
                        ["value_field"] = "amount",
                        ["aggregate"] = "sum",
                    },
                    TransformOperation = new TransformOperationSpec
                    {
                        Kind = TransformOperationKind.GroupBy,
                        Key = "${input}",
                        Value = "amount",
                        Aggregate = TransformAggregateKind.Sum,
                    },
                },
            ],
        };
        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-transform-operation",
            Input = "department",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event
            .Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters["group_by"].Should().Be("department");
        request.StepParameters.TransformOperation.Kind.Should().Be(TransformOperationKind.GroupBy);
        request.StepParameters.TransformOperation.Key.Should().Be("department");
        request.StepParameters.TransformOperation.Value.Should().Be("amount");
        request.StepParameters.TransformOperation.Aggregate.Should().Be(TransformAggregateKind.Sum);
    }

    [Fact]
    public async Task DispatchStep_ShouldEvaluateTypedConnectorApprovalBeforeDispatch()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "connector",
                    Type = "connector_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["connector"] = "service_proxy",
                        ["operation"] = "create_resource",
                    },
                    ConnectorApprovalOptions = new ConnectorApprovalOptionsDefinition
                    {
                        ServiceRef = "${concat('service-', input)}",
                        NodeId = "${concat('node-', input)}",
                        HttpVerb = "post",
                        Resource = "${concat('/resources/', input)}",
                        PermissionScope = "resources.write",
                        ExpirationSeconds = 300,
                        StatusCheckIntervalSeconds = 3,
                        Destructive = true,
                        TeamId = "team-alpha",
                        MemberId = "member-alpha",
                        WorkflowId = "workflow-alpha",
                        PublishedServiceId = "published-service-alpha",
                        PolicyReason = "external-write",
                    },
                },
            ],
        };
        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-connector-approval",
            Input = "alpha",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event
            .Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters.Should().Contain("connector", "service_proxy");
        request.Parameters.Should().Contain("operation", "create_resource");
        var approval = request.StepParameters.ConnectorApproval;
        approval.Policy.Should().Be(WorkflowExternalActionApprovalPolicy.Required);
        approval.ServiceRef.Should().Be("service-alpha");
        approval.NodeId.Should().Be("node-alpha");
        approval.HttpVerb.Should().Be("post");
        approval.Resource.Should().Be("/resources/alpha");
        approval.PermissionScope.Should().Be("resources.write");
        approval.ExpirationSeconds.Should().Be(300);
        approval.StatusCheckIntervalSeconds.Should().Be(3);
        approval.Destructive.Should().BeTrue();
        approval.TeamId.Should().Be("team-alpha");
        approval.MemberId.Should().Be("member-alpha");
        approval.WorkflowId.Should().Be("workflow-alpha");
        approval.PublishedServiceId.Should().Be("published-service-alpha");
        approval.PolicyReason.Should().Be("external-write");
    }

    [Fact]
    public async Task DispatchStep_WhenLlmCallOmitsRole_ShouldAssignImplicitAssistantTarget()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "llm",
                    Type = "llm_call",
                },
            ],
        };

        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-implicit-assistant",
            Input = "hello",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event.Should().BeOfType<StepRequestEvent>().Subject;
        request.TargetRole.Should().Be("assistant");
    }

    [Fact]
    public async Task StartWorkflow_ShouldHydrateTypedWorkflowRuntimeBeforeFirstStepDispatch()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "tool",
                    Type = "tool_call",
                },
            ],
        };

        var ctx = new CapturingContext();
        var stateHost = (IWorkflowExecutionStateHost)ctx.Agent;
        var module = new WorkflowExecutionKernel(workflow, stateHost);

        await module.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "child-run",
            Input = "hello",
            WorkflowRuntime = new WorkflowToolRuntimeContextPayload
            {
                ParentActorId = " parent-actor ",
                ParentRunId = " parent-run ",
                ParentStepId = " parent-step ",
                RootRunId = " root-run ",
                Depth = 3,
            },
        }), ctx, CancellationToken.None);

        stateHost.ExecutionContextSnapshot.WorkflowRuntime.Should().NotBeNull();
        stateHost.ExecutionContextSnapshot.WorkflowRuntime!.ParentActorId.Should().Be("parent-actor");
        stateHost.ExecutionContextSnapshot.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        stateHost.ExecutionContextSnapshot.WorkflowRuntime.ParentStepId.Should().Be("parent-step");
        stateHost.ExecutionContextSnapshot.WorkflowRuntime.RootRunId.Should().Be("root-run");
        stateHost.ExecutionContextSnapshot.WorkflowRuntime.Depth.Should().Be(3);
        ctx.Published.Single(x => x.Event is StepRequestEvent).Event
            .Should().BeOfType<StepRequestEvent>().Which.StepId.Should().Be("tool");
    }

    [Fact]
    public async Task DispatchStep_WhenNotifyTemplateUsesExpressions_ShouldEvaluateBeforeNotifyModulePublishesNotification()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "notify",
                    Type = "notify",
                    Presentation = new StepPresentation
                    {
                        DeliveryTargetId = "agent-${input}",
                        InteractionTemplateSpec = new InteractionTemplateSpec
                        {
                            TemplateId = "tpl-${input}",
                            TemplateVariable =
                            {
                                ["title"] = "Deploy ${input}",
                                ["run"] = "${run}",
                            },
                        },
                    },
                },
            ],
        };
        var ctx = new CapturingContext();
        var module = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);
        var start = new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-template",
            Input = "prod",
        };
        start.Parameters["run"] = "run-template";

        await module.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event
            .Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters.Should().NotContainKey("delivery_target_id");
        request.StepParameters.DeliveryTargetId.Should().Be("agent-prod");
        request.StepParameters.InteractionTemplateSpec.TemplateId.Should().Be("tpl-prod");
        request.StepParameters.InteractionTemplateSpec.TemplateVariable["title"].Should().Be("Deploy prod");
        request.StepParameters.InteractionTemplateSpec.TemplateVariable["run"].Should().Be("run-template");

        var notifyCtx = new RecordingWorkflowContext();
        await new NotifyModule().HandleAsync(Wrap(request), notifyCtx, CancellationToken.None);

        var notification = notifyCtx.Published.Select(x => x.Event)
            .OfType<WorkflowInteractionNotificationEvent>()
            .Should().ContainSingle().Subject;
        notification.DeliveryTargetId.Should().Be("agent-prod");
        notification.InteractionTemplate.TemplateId.Should().Be("tpl-prod");
        notification.InteractionTemplate.TemplateVariable["title"].Should().Be("Deploy prod");
        notification.InteractionTemplate.TemplateVariable["run"].Should().Be("run-template");
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
    };

    private sealed class CapturingContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public IAgent Agent { get; } = new StubWorkflowRunAgent("agent-1", "run-1");
        public IServiceProvider Services { get; } = new NullServiceProvider();
        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent evt, TopologyAudience direction = TopologyAudience.Children, CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = options;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            _ = period;
            return ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, options, ct);
        }

        public Task CancelDurableCallbackAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }
    }

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "agent-1";

        public string RunId => "run-template";

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> =>
            Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");
    }

    private sealed class StubWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

        public string Id => id;

        public string RunId { get; } = runId;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            ExecutionContextState.WorkflowRuntime = null;
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _executionStates.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates.Remove(scopeKey);
            return Task.CompletedTask;
        }

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

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.ClearWorkflowRuntime)
            state.WorkflowRuntime = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
            };
        }

        if (delta.WorkflowRuntime != null)
        {
            state.WorkflowRuntime = new WorkflowToolRuntimeContextState
            {
                ParentActorId = delta.WorkflowRuntime.ParentActorId,
                ParentRunId = delta.WorkflowRuntime.ParentRunId,
                ParentStepId = delta.WorkflowRuntime.ParentStepId,
                RootRunId = delta.WorkflowRuntime.RootRunId,
                Depth = delta.WorkflowRuntime.Depth,
            };
        }
    }
}
