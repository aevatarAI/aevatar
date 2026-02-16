using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
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
    public void AddAevatarWorkflow_ShouldRegisterDefaultCompositionServices()
    {
        var provider = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();

        var expanders = provider.GetServices<IWorkflowModuleDependencyExpander>().ToList();
        var configurators = provider.GetServices<IWorkflowModuleConfigurator>().ToList();

        expanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowLoopModuleDependencyExpander));
        expanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowStepTypeModuleDependencyExpander));
        expanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowImplicitModuleDependencyExpander));
        configurators.Should().ContainSingle(x => x.GetType() == typeof(WorkflowLoopModuleConfigurator));
    }
}
