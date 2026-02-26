using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Foundation.Abstractions.EventModules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class WorkflowModuleCompositionTests
{
    [Fact]
    public void ModuleDependencyExpanders_ShouldResolveDeclaredAndImplicitModules()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "s1",
                    Type = "foreach",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["items"] = "a,b,c",
                    },
                    Children =
                    [
                        new StepDefinition
                        {
                            Id = "s1-1",
                            Type = "tool_call",
                            TargetRole = "assistant",
                        },
                    ],
                },
            ],
        };

        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IWorkflowModuleDependencyExpander[] expanders =
        [
            new WorkflowLoopModuleDependencyExpander(),
            new WorkflowStepTypeModuleDependencyExpander(),
            new WorkflowImplicitModuleDependencyExpander(),
        ];

        foreach (var expander in expanders.OrderBy(x => x.Order))
            expander.Expand(workflow, moduleNames);

        moduleNames.Should().Contain("workflow_loop");
        moduleNames.Should().Contain("foreach");
        moduleNames.Should().Contain("parallel");
        moduleNames.Should().Contain("llm_call");
        moduleNames.Should().Contain("tool_call");
    }

    [Fact]
    public void ModuleDependencyExpanders_WhenWorkflowUsesCacheDefaultChild_ShouldIncludeLlmCall()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf_cache",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "cache_1",
                    Type = "cache",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["cache_key"] = "k1",
                    },
                },
            ],
        };

        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IWorkflowModuleDependencyExpander[] expanders =
        [
            new WorkflowLoopModuleDependencyExpander(),
            new WorkflowStepTypeModuleDependencyExpander(),
            new WorkflowImplicitModuleDependencyExpander(),
        ];

        foreach (var expander in expanders.OrderBy(x => x.Order))
            expander.Expand(workflow, moduleNames);

        moduleNames.Should().Contain("workflow_loop");
        moduleNames.Should().Contain("cache");
        moduleNames.Should().Contain("llm_call");
    }

    [Fact]
    public void AddAevatarWorkflow_ShouldRegisterDefaultCompositionModulePack()
    {
        var provider = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();

        var packs = provider.GetServices<IWorkflowModulePack>().ToList();
        var moduleFactory = provider.GetRequiredService<IEventModuleFactory>();
        var corePack = packs.Should().ContainSingle(x => x.GetType() == typeof(WorkflowCoreModulePack)).Subject;

        moduleFactory.Should().BeOfType<WorkflowModuleFactory>();
        corePack.DependencyExpanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowLoopModuleDependencyExpander));
        corePack.DependencyExpanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowStepTypeModuleDependencyExpander));
        corePack.DependencyExpanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowImplicitModuleDependencyExpander));
        corePack.Configurators.Should().ContainSingle(x => x.GetType() == typeof(WorkflowLoopModuleConfigurator));
    }
}
