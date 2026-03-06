using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowModuleCompositionTests
{
    [Fact]
    public void AddAevatarWorkflow_ShouldRegisterCorePackWithStatelessModulesOnly()
    {
        var provider = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();

        var packs = provider.GetServices<IWorkflowModulePack>().ToList();
        var moduleFactory = provider.GetRequiredService<IEventModuleFactory>();
        var corePack = packs.Should().ContainSingle(x => x.GetType() == typeof(WorkflowCoreModulePack)).Subject;
        var names = corePack.Modules.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        moduleFactory.Should().BeOfType<WorkflowModuleFactory>();
        names.Should().Contain(["conditional", "switch", "checkpoint", "assign", "vote", "tool_call", "connector_call", "transform", "retrieve_facts", "guard", "emit", "workflow_yaml_validate", "dynamic_workflow"]);
        names.Should().NotContain(["workflow_loop", "while", "delay", "wait_signal", "human_input", "human_approval", "llm_call", "evaluate", "reflect", "parallel", "map_reduce", "foreach", "race"]);
    }

    [Fact]
    public void WorkflowModuleFactory_ShouldFailFastOnDuplicateModuleNamesAcrossPacks()
    {
        var provider = new ServiceCollection().BuildServiceProvider();

        Action act = () => new WorkflowModuleFactory(
            provider,
            [
                new TestPack("pack-a", WorkflowModuleRegistration.Create<TestModule>("dup")),
                new TestPack("pack-b", WorkflowModuleRegistration.Create<TestModule>("dup")),
            ]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate workflow module name 'dup'*");
    }

    [Fact]
    public void MakerModulePack_ShouldExposeOnlyMakerSpecificStatelessModules()
    {
        var pack = new MakerModulePack();
        var names = pack.Modules.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().Contain(["maker_vote", "maker_recursive", "maker_recursive_solve"]);
        names.Should().NotContain("workflow_loop");
    }

    private sealed class TestPack(string name, params WorkflowModuleRegistration[] modules) : IWorkflowModulePack
    {
        public string Name { get; } = name;

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = modules;
    }

    private sealed class TestModule : IEventModule
    {
        public string Name => "dup";

        public int Priority => 5;

        public bool CanHandle(EventEnvelope envelope) => false;

        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
