using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowModuleCompositionTests
{
    [Fact]
    public void AddAevatarWorkflow_ShouldRegisterCorePackWithPrimitiveHandlersOnly()
    {
        var provider = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();

        var packs = provider.GetServices<IWorkflowModulePack>().ToList();
        var registry = new WorkflowPrimitiveRegistry(packs);
        var corePack = packs.Should().ContainSingle(x => x.GetType() == typeof(WorkflowCoreModulePack)).Subject;
        var names = corePack.Modules.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        registry.TryCreate("conditional", provider, out var conditional).Should().BeTrue();
        conditional.Should().NotBeNull();
        names.Should().Contain(["conditional", "switch", "checkpoint", "assign", "vote", "tool_call", "connector_call", "transform", "retrieve_facts", "guard", "emit", "workflow_yaml_validate", "dynamic_workflow"]);
        names.Should().NotContain(["workflow_loop", "while", "delay", "wait_signal", "human_input", "human_approval", "llm_call", "evaluate", "reflect", "parallel", "map_reduce", "foreach", "race"]);
    }

    [Fact]
    public void WorkflowPrimitiveRegistry_ShouldFailFastOnDuplicateModuleNamesAcrossPacks()
    {
        Action act = () => new WorkflowPrimitiveRegistry(
            [
                new TestPack("pack-a", WorkflowModuleRegistration.Create<TestPrimitive>("dup")),
                new TestPack("pack-b", WorkflowModuleRegistration.Create<TestPrimitive>("dup")),
            ]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate workflow primitive name 'dup'*");
    }

    [Fact]
    public void MakerModulePack_ShouldExposeOnlyMakerSpecificPrimitives()
    {
        var pack = new MakerModulePack();
        var names = pack.Modules.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().Contain("maker_vote");
        names.Should().NotContain(["maker_recursive", "maker_recursive_solve", "workflow_loop"]);
    }

    private sealed class TestPack(string name, params WorkflowModuleRegistration[] modules) : IWorkflowModulePack
    {
        public string Name { get; } = name;

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = modules;
    }

    private sealed class TestPrimitive : IWorkflowPrimitiveHandler
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
