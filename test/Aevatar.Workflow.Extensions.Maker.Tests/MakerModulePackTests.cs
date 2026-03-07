using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerModulePackTests
{
    [Theory]
    [InlineData("maker_vote")]
    public void WorkflowPrimitiveRegistry_WhenMakerPackRegistered_ShouldCreateMakerPrimitives(string moduleName)
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var registry = new WorkflowPrimitiveRegistry(provider.GetServices<IWorkflowModulePack>());
        var created = registry.TryCreate(moduleName, provider, out var module);

        created.Should().BeTrue();
        module.Should().NotBeNull();
    }

    [Fact]
    public void WorkflowPrimitiveRegistry_WhenModuleNameUnknown_ShouldReturnFalse()
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var registry = new WorkflowPrimitiveRegistry(provider.GetServices<IWorkflowModulePack>());
        var created = registry.TryCreate("unknown", provider, out var module);

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
