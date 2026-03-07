using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.PrimitiveExecutors;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowAdditionalModules")]
public sealed class WorkflowAdditionalModulesCoverageTests
{
    [Fact]
    public async Task EmitPrimitiveExecutor_ShouldHandleExplicitAndFallbackPayloads()
    {
        var module = new EmitPrimitiveExecutor();
        var ctx = CreateContext();
        var primitive = CreatePrimitiveContext(ctx);

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        var emitted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        emitted.Success.Should().BeTrue();
        emitted.Metadata["emit.event_type"].Should().Be("audit");
        emitted.Metadata["emit.payload"].Should().Be("{\"k\":1}");

        ctx.Published.Clear();

        await module.HandleAsync(
            new StepRequestEvent
            {
                StepId = "emit-2",
                StepType = "emit",
                RunId = "run-1",
                Input = "fallback-payload",
            },
            primitive,
            CancellationToken.None);

        var fallback = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        fallback.Metadata["emit.event_type"].Should().Be("custom");
        fallback.Metadata["emit.payload"].Should().Be("fallback-payload");
    }

    [Fact]
    public async Task SwitchPrimitiveExecutor_ShouldResolveExactContainsAndDefaultBranches()
    {
        var module = new SwitchPrimitiveExecutor();
        var ctx = CreateContext();
        var primitive = CreatePrimitiveContext(ctx);

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("foo");
        ctx.Published.Clear();

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("bar");
        ctx.Published.Clear();

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Metadata["branch"].Should().Be("_default");
    }

    [Fact]
    public async Task DynamicWorkflowPrimitiveExecutor_ShouldExtractYamlAndPublishChildRunInvocation()
    {
        var module = new DynamicWorkflowPrimitiveExecutor();
        var ctx = CreateContext();
        var primitive = CreatePrimitiveContext(ctx);

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        var invocation = ctx.Published.Select(x => x.evt).OfType<DynamicWorkflowInvokeRequestedEvent>().Single();
        invocation.ParentRunId.Should().Be("run-1");
        invocation.ParentStepId.Should().Be("dynamic-1");
        invocation.Input.Should().Be("replay-input");
        invocation.WorkflowYaml.Should().Contain("name: nested_demo");
        invocation.WorkflowName.Should().Be("nested_demo");
    }

    [Fact]
    public async Task DynamicWorkflowPrimitiveExecutor_WhenYamlValidationFails_ShouldEmitFailedStep()
    {
        var module = new DynamicWorkflowPrimitiveExecutor();
        var ctx = CreateContext();
        var primitive = CreatePrimitiveContext(ctx);

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task WorkflowYamlValidatePrimitiveExecutor_ShouldEmitCanonicalYamlOrFailure()
    {
        var module = new WorkflowYamlValidatePrimitiveExecutor();
        var ctx = CreateContext();
        var primitive = CreatePrimitiveContext(ctx);

        await module.HandleAsync(
            new StepRequestEvent
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
            },
            primitive,
            CancellationToken.None);

        var success = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        success.Success.Should().BeTrue();
        success.Output.Should().Contain("```yaml");
        success.Output.Should().Contain("name: validator_demo");

        ctx.Published.Clear();

        await module.HandleAsync(
            new StepRequestEvent
            {
                StepId = "validate-fail",
                StepType = "workflow_yaml_validate",
                RunId = "run-1",
                Input = "no yaml here",
            },
            primitive,
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
        return new TestEventHandlerContext(services, new TestAgent("workflow-test-agent"), NullLogger.Instance);
    }

    private static WorkflowPrimitiveExecutionContext CreatePrimitiveContext(TestEventHandlerContext ctx)
    {
        var knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            ctx.Services
                .GetServices<IWorkflowPrimitivePack>()
                .SelectMany(pack => pack.Executors)
                .SelectMany(module => module.Names));
        knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        return ctx.CreatePrimitiveContext(new HashSet<string>(knownStepTypes, StringComparer.OrdinalIgnoreCase));
    }
}
