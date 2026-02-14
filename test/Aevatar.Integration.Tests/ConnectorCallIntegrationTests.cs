using Aevatar;
using Aevatar.AI;
using Aevatar.Cognitive;
using Aevatar.Cognitive.Connectors;
using Aevatar.Connectors;
using Aevatar.DependencyInjection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ConnectorCallIntegrationTests
{
    [Fact]
    public async Task ConnectorCall_ShouldInvokeRegisteredConnector_AndPublishMetadata()
    {
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new FakeConnector("fake_connector", "echo://done"));
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
        step.Metadata["connector.name"].Should().Be("fake_connector");
        step.Metadata["connector.type"].Should().Be("fake");
        step.Metadata["connector.operation"].Should().Be("summarize");
        step.Metadata["connector.fake.marker"].Should().Be("ok");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("echo://done:hello connector");
    }

    [Fact]
    public async Task ConnectorCall_WhenMissingAndSkip_ShouldKeepInput()
    {
        await using var env = BuildEnvironment(new InMemoryConnectorRegistry());

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
        step.Metadata["connector.skipped"].Should().Be("true");
        step.Metadata["connector.skip_reason"].Should().Be("connector_not_found");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("original-input");
    }

    [Fact]
    public async Task ConnectorCall_WhenConnectorFailsAndContinue_ShouldKeepInput()
    {
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new FakeFailConnector("unstable_connector"));
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
        step.Metadata["connector.continued_on_error"].Should().Be("true");
        step.Metadata["connector.error"].Should().Be("boom");

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("input-keep");
    }

    [Fact]
    public async Task ConnectorCall_WhenRoleHasConnectorsAllowlist_AndConnectorInList_ShouldSucceed()
    {
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new FakeConnector("allowed_connector", "ok"));
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
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new FakeConnector("other_connector", "ok"));
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
        services.AddAevatarRuntime();
        services.AddAevatarCognitive();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        return new TestEnvironment(provider, runtime);
    }

    private static async Task<WorkflowRunResult> RunWorkflowAsync(
        ServiceProvider provider,
        IActorRuntime runtime,
        string workflowYaml,
        string input)
    {
        var actor = await runtime.CreateAsync<WorkflowGAgent>("wf-root-" + Guid.NewGuid().ToString("N")[..8]);
        var setWf = new SetWorkflowEvent { WorkflowYaml = workflowYaml, WorkflowName = "connector_flow" };
        var initEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(setWf),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
        await actor.HandleEventAsync(initEnvelope);

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(actor.Id);
        var stepCompletions = new List<StepCompletedEvent>();
        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload == null) return Task.CompletedTask;

            var typeUrl = envelope.Payload.TypeUrl;
            if (typeUrl.Contains("StepCompletedEvent"))
            {
                stepCompletions.Add(envelope.Payload.Unpack<StepCompletedEvent>());
            }
            else if (typeUrl.Contains("WorkflowCompletedEvent"))
            {
                workflowCompleted.TrySetResult(envelope.Payload.Unpack<WorkflowCompletedEvent>());
            }

            return Task.CompletedTask;
        });

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent { Prompt = input, SessionId = "test-session" }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await workflowCompleted.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(actor.Id);
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

    private sealed record TestEnvironment(ServiceProvider Provider, IActorRuntime Runtime) : IAsyncDisposable
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
