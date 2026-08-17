using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowForEachModuleIdentity")]
public sealed class WorkflowForEachModuleIdentityTests : WorkflowCoreModuleTestBase
{
    [Fact]
    public async Task ForEachModule_ShouldSupportEscapedDelimiterAndJsonArrayInput()
    {
        var module = new ForEachModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-escaped",
                StepType = "foreach",
                RunId = ctx.RunId,
                Input = "a\n---\nb",
                Parameters =
                {
                    ["delimiter"] = "\\n---\\n",
                    ["sub_step_type"] = "assign",
                },
            }),
            ctx,
            CancellationToken.None);

        var escapedDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        escapedDispatches.Should().HaveCount(2);
        escapedDispatches[0].Input.Should().Be("a");
        escapedDispatches[1].Input.Should().Be("b");
        escapedDispatches.Should().OnlyContain(child => child.RunId == ctx.RunId);
        escapedDispatches.Should().OnlyContain(child => !string.IsNullOrWhiteSpace(child.ExecutionId));
        escapedDispatches.Select(child => child.ExecutionId).Should().OnlyHaveUniqueItems();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = escapedDispatches[0].StepId,
                RunId = escapedDispatches[0].RunId,
                ExecutionId = escapedDispatches[0].ExecutionId,
                Success = true,
                Output = "A",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = escapedDispatches[1].StepId,
                RunId = escapedDispatches[1].RunId,
                ExecutionId = escapedDispatches[1].ExecutionId,
                Success = true,
                Output = "B",
            }),
            ctx,
            CancellationToken.None);

        var merged = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        merged.StepId.Should().Be("foreach-escaped");
        merged.RunId.Should().Be(ctx.RunId);
        merged.Success.Should().BeTrue();
        merged.Output.Should().Be("A\n---\nB");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-json",
                StepType = "foreach",
                RunId = ctx.RunId,
                Input = "[\"x\",\"y\"]",
                Parameters = { ["sub_step_type"] = "assign" },
            }),
            ctx,
            CancellationToken.None);

        var jsonDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        jsonDispatches.Should().HaveCount(2);
        jsonDispatches[0].Input.Should().Be("x");
        jsonDispatches[1].Input.Should().Be("y");
        jsonDispatches.Should().OnlyContain(child => child.RunId == ctx.RunId);
        jsonDispatches.Should().OnlyContain(child => !string.IsNullOrWhiteSpace(child.ExecutionId));
        jsonDispatches.Select(child => child.ExecutionId).Should().OnlyHaveUniqueItems();
    }
}
