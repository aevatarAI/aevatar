using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SystemType = System.Type;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowAdditionalModules")]
public sealed class WorkflowAdditionalModulesCoverageTests
{
    [Fact]
    public async Task EmitModule_ShouldHandleExplicitAndFallbackPayloads()
    {
        var module = new EmitModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "emit-1",
                StepType = "emit",
                RunId = "run-1",
                Input = "source-input",
                Parameters =
                {
                    ["event_type"] = "audit",
                    ["payload"] = "{\"k\":1}",
                },
            }),
            ctx,
            CancellationToken.None);

        var emitted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        emitted.Success.Should().BeTrue();
        emitted.Metadata["emit.event_type"].Should().Be("audit");
        emitted.Metadata["emit.payload"].Should().Be("{\"k\":1}");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "emit-2",
                StepType = "emit",
                RunId = "run-1",
                Input = "fallback-payload",
            }),
            ctx,
            CancellationToken.None);

        var fallback = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        fallback.Metadata["emit.event_type"].Should().Be("custom");
        fallback.Metadata["emit.payload"].Should().Be("fallback-payload");
    }

    [Fact]
    public async Task SwitchModule_ShouldResolveExactContainsAndDefaultBranches()
    {
        var module = new SwitchModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-exact",
                StepType = "switch",
                RunId = "run-1",
                Parameters =
                {
                    ["on"] = "foo",
                    ["branch.foo"] = "s-next-foo",
                    ["branch.bar"] = "s-next-bar",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("foo");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-contains",
                StepType = "switch",
                RunId = "run-1",
                Input = "prefix BAR suffix",
                Parameters =
                {
                    ["branch.foo"] = "s-next-foo",
                    ["branch.bar"] = "s-next-bar",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("bar");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-default",
                StepType = "switch",
                RunId = "run-1",
                Input = "unmatched",
                Parameters =
                {
                    ["branch.foo"] = "s-next-foo",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("_default");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldExtractYamlAndPublishReconfigureEvent()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dynamic-1",
                StepType = "dynamic_workflow",
                RunId = "run-1",
                Input =
                    """
                    some explanation
                    ```yaml
                    name: nested_demo
                    roles: []
                    steps:
                      - id: s1
                        type: transform
                    ```
                    """,
                Parameters =
                {
                    ["original_input"] = "replay-input",
                },
            }),
            ctx,
            CancellationToken.None);

        var replacement = ctx.Published.Select(x => x.evt).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Single();
        replacement.Input.Should().Be("replay-input");
        replacement.WorkflowYaml.Should().Contain("name: nested_demo");
    }

    [Fact]
    public async Task DynamicWorkflowModule_WhenYamlValidationFails_ShouldEmitFailedStep()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dynamic-invalid",
                StepType = "dynamic_workflow",
                RunId = "run-1",
                Input =
                    """
                    ```yaml
                    name: broken
                    roles: []
                    steps:
                      - id: s1
                        type: typo_unknown_step
                    ```
                    """,
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task WorkflowYamlValidateModule_ShouldEmitCanonicalYamlOrFailure()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "validate-ok",
                StepType = "workflow_yaml_validate",
                RunId = "run-1",
                Input =
                    """
                    ```yaml
                    name: validator_demo
                    roles: []
                    steps:
                      - id: s1
                        type: transform
                    ```
                    """,
            }),
            ctx,
            CancellationToken.None);

        var success = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        success.Success.Should().BeTrue();
        success.Output.Should().Contain("```yaml");
        success.Output.Should().Contain("name: validator_demo");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "validate-fail",
                StepType = "workflow_yaml_validate",
                RunId = "run-1",
                Input = "no yaml here",
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("No workflow YAML found");
    }

    private static TestEventHandlerContext CreateContext()
    {
        var services = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();
        return new TestEventHandlerContext(services, NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "workflow-test",
            Direction = EventDirection.Self,
        };

    private sealed class TestEventHandlerContext(IServiceProvider services, ILogger logger) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "workflow-test-agent";

        public IAgent Agent { get; } = new StubAgent();

        public IServiceProvider Services { get; } = services;

        public ILogger Logger { get; } = logger;

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down, CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "workflow-test-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
