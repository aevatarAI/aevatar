using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Connectors;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

public class ConnectorCallIntegrationTests
{
    [Fact]
    public async Task ConnectorCall_ShouldInvokeRegisteredConnector_AndPublishMetadata()
    {
        var registry = new ConfiguredConnectorRegistry();
        await registry.RegisterAsync(ConnectorRegistration.External(new FakeConnector("fake_connector", "echo://done")));
        await using var env = BuildEnvironment(registry);

        const string yaml = """
            name: connector_flow
            steps:
              - id: connector_step
                type: connector_call
                parameters:
                  connector: fake_connector
                  operation: summarize
            """;

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, yaml, "hello connector");
        result.StepCompletions.Should().ContainSingle(x => x.StepId == "connector_step");

        var step = result.StepCompletions.Single(x => x.StepId == "connector_step");
        step.Success.Should().BeTrue();
        step.Output.Should().Be("echo://done:hello connector");
        step.Annotations["connector.name"].Should().Be("fake_connector");
        step.Annotations["connector.type"].Should().Be("fake");
        step.Annotations["connector.operation"].Should().Be("summarize");
        step.Annotations["connector.fake.marker"].Should().Be("ok");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("echo://done:hello connector");
    }

    [Fact]
    public async Task ConnectorCall_WhenMissingAndSkip_ShouldKeepInput()
    {
        await using var env = BuildEnvironment(new ConfiguredConnectorRegistry());

        const string yaml = """
            name: connector_flow_skip
            steps:
              - id: connector_step
                type: connector_call
                parameters:
                  connector: missing_connector
                  on_missing: skip
            """;

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, yaml, "original-input");
        var step = result.StepCompletions.Single(x => x.StepId == "connector_step");
        step.Success.Should().BeTrue();
        step.Output.Should().Be("original-input");
        step.Annotations["connector.skipped"].Should().Be("true");
        step.Annotations["connector.skip_reason"].Should().Be("connector_not_found");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("original-input");
    }

    [Fact]
    public async Task ConnectorCall_WhenConnectorFailsAndContinue_ShouldKeepInput()
    {
        var registry = new ConfiguredConnectorRegistry();
        await registry.RegisterAsync(ConnectorRegistration.External(new FakeFailConnector("unstable_connector")));
        await using var env = BuildEnvironment(registry);

        const string yaml = """
            name: connector_flow_continue
            steps:
              - id: connector_step
                type: connector_call
                parameters:
                  connector: unstable_connector
                  on_error: continue
            """;

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, yaml, "input-keep");
        var step = result.StepCompletions.Single(x => x.StepId == "connector_step");
        step.Success.Should().BeTrue();
        step.Output.Should().Be("input-keep");
        step.Annotations["connector.continued_on_error"].Should().Be("true");
        step.Annotations["connector.error"].Should().Be("boom");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("input-keep");
    }

    [Fact]
    public async Task ConnectorCall_WhenRoleHasConnectorsAllowlist_AndConnectorInList_ShouldSucceed()
    {
        var registry = new ConfiguredConnectorRegistry();
        await registry.RegisterAsync(ConnectorRegistration.External(new FakeConnector("allowed_connector", "ok")));
        await using var env = BuildEnvironment(registry);

        const string yaml = """
            name: role_connector_flow
            roles:
              - id: coordinator
                name: Coordinator
                system_prompt: ""
                connectors:
                  - allowed_connector
            steps:
              - id: connector_step
                type: connector_call
                role: coordinator
                parameters:
                  connector: allowed_connector
                  operation: run
            """;

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, yaml, "input");
        var step = result.StepCompletions.Single(x => x.StepId == "connector_step");
        step.Success.Should().BeTrue();
        step.Output.Should().Be("ok:input");
        result.WorkflowCompleted!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectorCall_WhenRoleHasConnectorsAllowlist_AndConnectorNotInList_ShouldFailStep()
    {
        var registry = new ConfiguredConnectorRegistry();
        await registry.RegisterAsync(ConnectorRegistration.External(new FakeConnector("other_connector", "ok")));
        await using var env = BuildEnvironment(registry);

        const string yaml = """
            name: role_connector_flow
            roles:
              - id: coordinator
                name: Coordinator
                system_prompt: ""
                connectors:
                  - only_this_one
            steps:
              - id: connector_step
                type: connector_call
                role: coordinator
                parameters:
                  connector: other_connector
                  operation: run
            """;

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, yaml, "input");
        var step = result.StepCompletions.Single(x => x.StepId == "connector_step");
        step.Success.Should().BeFalse();
        step.Error.Should().Contain("not allowed").And.Contain("other_connector");
        result.WorkflowCompleted!.Success.Should().BeFalse();
    }

    private static TestEnvironment BuildEnvironment(IConnectorRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        var workflowSnapshots = new MutableWorkflowExecutionCurrentStateQueryPort();
        services.AddSingleton(workflowSnapshots);
        services.AddSingleton<IWorkflowExecutionCurrentStateQueryPort>(workflowSnapshots);
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        return new TestEnvironment(provider, runtime, workflowSnapshots);
    }

    private static async Task<WorkflowRunResult> RunWorkflowAsync(
        ServiceProvider provider,
        IActorRuntime runtime,
        string workflowYaml,
        string input)
    {
        var definitionActor = await runtime.CreateAsync<WorkflowGAgent>("wf-root-definition-" + Guid.NewGuid().ToString("N")[..8]);
        var runActor = await runtime.CreateAsync<WorkflowRunGAgent>("wf-root-run-" + Guid.NewGuid().ToString("N")[..8]);
        provider.GetRequiredService<MutableWorkflowExecutionCurrentStateQueryPort>().Upsert(runActor.Id);

        await definitionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowYaml = workflowYaml,
                WorkflowName = "connector_flow",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        });

        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = definitionActor.Id,
                WorkflowYaml = workflowYaml,
                WorkflowName = "connector_flow",
                RunId = "connector-flow-run",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        });

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(runActor.Id);
        var stepCompletions = new List<StepCompletedEvent>();
        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ioIntentObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            var payload = envelope.Payload;
            if (payload == null) return Task.CompletedTask;

            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                stepCompletions.Add(payload.Unpack<StepCompletedEvent>());
            }
            else if (payload.Is(ToolCallIntentEvent.Descriptor) ||
                     payload.Is(ConnectorCallIntentEvent.Descriptor))
            {
                ioIntentObserved.TrySetResult();
            }
            else if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                workflowCompleted.TrySetResult(payload.Unpack<WorkflowCompletedEvent>());
            }

            return Task.CompletedTask;
        });

        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent { Prompt = input, SessionId = "test-session" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await Task.WhenAny(workflowCompleted.Task, ioIntentObserved.Task.WaitAsync(timeout.Token));
        if (first != workflowCompleted.Task)
        {
            var worker = new WorkflowStepIoWorker(provider, NullLogger<WorkflowStepIoWorker>.Instance);
            await worker.ScanPendingItemsAsync(timeout.Token);
        }

        while (!workflowCompleted.Task.IsCompleted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var completed = await workflowCompleted.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(runActor.Id);
        await runtime.DestroyAsync(definitionActor.Id);
        return new WorkflowRunResult(completed, stepCompletions);
    }

    private sealed class FakeConnector(string name, string prefix) : IConnector
    {
        public string Name { get; } = name;
        public string Type => "fake";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ConnectorResponse
            {
                Success = true,
                Output = $"{prefix}:{request.Payload}",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.fake.marker"] = "ok",
                },
            });
        }
    }

    private sealed class FakeFailConnector(string name) : IConnector
    {
        public string Name { get; } = name;
        public string Type => "fake";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ConnectorResponse
            {
                Success = false,
                Error = "boom",
            });
        }
    }

    private sealed class MutableWorkflowExecutionCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        private readonly List<WorkflowActorSnapshot> _snapshots = [];

        public bool EnableActorQueryEndpoints => true;

        public void Upsert(string actorId)
        {
            _snapshots.RemoveAll(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal));
            _snapshots.Add(new WorkflowActorSnapshot
            {
                ActorId = actorId,
                PendingIoWorkItemCount = 1,
            });
        }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.FirstOrDefault(x =>
                string.Equals(x.ActorId, actorId, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
            int take = 200,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>(_snapshots.Take(take).ToList());
        }

        public Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorProjectionState?>(null);
        }
    }

    private sealed record TestEnvironment(
        ServiceProvider Provider,
        IActorRuntime Runtime,
        MutableWorkflowExecutionCurrentStateQueryPort WorkflowSnapshots) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record WorkflowRunResult(
        WorkflowCompletedEvent? WorkflowCompleted,
        List<StepCompletedEvent> StepCompletions);
}
