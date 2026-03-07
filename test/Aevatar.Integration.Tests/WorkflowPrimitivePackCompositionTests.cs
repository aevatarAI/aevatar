using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowPrimitivePackCompositionTests
{
    [Fact]
    public void AddAevatarWorkflow_ShouldRegisterCorePackWithPrimitiveHandlersOnly()
    {
        var provider = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();

        var packs = provider.GetServices<IWorkflowPrimitivePack>().ToList();
        var registry = new WorkflowPrimitiveExecutorRegistry(packs);
        var corePack = packs.Should().ContainSingle(x => x.GetType() == typeof(WorkflowCorePrimitivePack)).Subject;
        var names = corePack.Executors.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        registry.TryCreate("conditional", provider, out var conditional).Should().BeTrue();
        conditional.Should().NotBeNull();
        names.Should().Contain(["conditional", "switch", "checkpoint", "assign", "vote", "tool_call", "connector_call", "transform", "retrieve_facts", "guard", "emit", "workflow_yaml_validate", "dynamic_workflow"]);
        names.Should().NotContain(["workflow_loop", "while", "delay", "wait_signal", "human_input", "human_approval", "llm_call", "evaluate", "reflect", "parallel", "map_reduce", "foreach", "race"]);
    }

    [Fact]
    public void WorkflowPrimitiveExecutorRegistry_ShouldFailFastOnDuplicatePrimitiveNamesAcrossPacks()
    {
        Action act = () => new WorkflowPrimitiveExecutorRegistry(
            [
                new TestPack("pack-a", WorkflowPrimitiveRegistration.Create<TestPrimitive>("dup")),
                new TestPack("pack-b", WorkflowPrimitiveRegistration.Create<TestPrimitive>("dup")),
            ]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate workflow primitive name 'dup'*");
    }

    [Fact]
    public void MakerPrimitivePack_ShouldExposeOnlyMakerSpecificPrimitives()
    {
        var pack = new MakerPrimitivePack();
        var names = pack.Executors.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().Contain("maker_vote");
        names.Should().NotContain(["maker_recursive", "maker_recursive_solve", "workflow_loop"]);
    }

    private sealed class TestPack(string name, params WorkflowPrimitiveRegistration[] modules) : IWorkflowPrimitivePack
    {
        public string Name { get; } = name;

        public IReadOnlyList<WorkflowPrimitiveRegistration> Executors { get; } = modules;
    }

    private sealed class TestPrimitive : IWorkflowPrimitiveExecutor
    {
        public string Name => "dup";

        public Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext context, CancellationToken ct)
        {
            _ = request;
            _ = context;
            _ = ct;
            return Task.CompletedTask;
        }
    }
}
