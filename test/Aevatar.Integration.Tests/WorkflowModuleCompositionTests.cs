using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Foundation.Abstractions.EventModules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class WorkflowModuleCompositionTests
{
    [Theory]
    [InlineData("parallel")]
    [InlineData("parallel_fanout")]
    [InlineData("fan_out")]
    [InlineData("race")]
    [InlineData("select")]
    [InlineData("map_reduce")]
    [InlineData("mapreduce")]
    [InlineData("cache")]
    [InlineData("evaluate")]
    [InlineData("judge")]
    [InlineData("reflect")]
    public void WorkflowImplicitModuleDependencyExpander_ShouldAddLlmCall_ForImplicitModules(string moduleName)
    {
        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            moduleName,
        };
        var expander = new WorkflowImplicitModuleDependencyExpander();

        expander.Expand(workflow: null, moduleNames);

        moduleNames.Should().Contain("llm_call");
    }

    [Fact]
    public void WorkflowImplicitModuleDependencyExpander_WhenNoImplicitModules_ShouldNotAddLlmCall()
    {
        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "transform",
            "assign",
        };
        var expander = new WorkflowImplicitModuleDependencyExpander();

        expander.Expand(workflow: null, moduleNames);

        moduleNames.Should().NotContain("llm_call");
    }

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
            new WorkflowStepTypeModuleDependencyExpander(),
            new WorkflowImplicitModuleDependencyExpander(),
        ];

        foreach (var expander in expanders.OrderBy(x => x.Order))
            expander.Expand(workflow, moduleNames);

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
            new WorkflowStepTypeModuleDependencyExpander(),
            new WorkflowImplicitModuleDependencyExpander(),
        ];

        foreach (var expander in expanders.OrderBy(x => x.Order))
            expander.Expand(workflow, moduleNames);

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
        var moduleFactory = provider.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();
        var corePack = packs.Should().ContainSingle(x => x.GetType() == typeof(WorkflowCoreModulePack)).Subject;

        moduleFactory.Should().BeOfType<WorkflowModuleFactory>();
        corePack.DependencyExpanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowStepTypeModuleDependencyExpander));
        corePack.DependencyExpanders.Should().ContainSingle(x => x.GetType() == typeof(WorkflowImplicitModuleDependencyExpander));
        corePack.Configurators.Should().BeEmpty();
    }
}
