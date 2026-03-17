using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerModulePackTests
{
    [Theory]
    [InlineData("maker_vote")]
    [InlineData("maker_recursive")]
    [InlineData("maker_recursive_solve")]
    public void WorkflowModuleFactory_WhenMakerPackRegistered_ShouldCreateMakerModules(string moduleName)
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();
        var created = factory.TryCreate(moduleName, out var module);

        created.Should().BeTrue();
        module.Should().NotBeNull();
    }

    [Fact]
    public void WorkflowModuleFactory_WhenModuleNameUnknown_ShouldReturnFalse()
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();
        var created = factory.TryCreate("unknown", out var module);

        created.Should().BeFalse();
        module.Should().BeNull();
    }
}

public class MakerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWorkflowMakerExtensions_ShouldRegisterMakerModulePack()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var packs = provider.GetServices<IWorkflowModulePack>().ToList();

        packs.Should().ContainSingle(x => x is MakerModulePack);
    }

    [Fact]
    public void AddWorkflowMakerExtensions_WhenCalledTwice_ShouldRemainIdempotent()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();
        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var packs = provider.GetServices<IWorkflowModulePack>().ToList();

        packs.Count(x => x is MakerModulePack).Should().Be(1);
    }
}
