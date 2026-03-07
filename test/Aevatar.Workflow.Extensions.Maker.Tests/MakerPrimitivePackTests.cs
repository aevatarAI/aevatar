using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerPrimitivePackTests
{
    [Theory]
    [InlineData("maker_vote")]
    public void WorkflowPrimitiveExecutorRegistry_WhenMakerPackRegistered_ShouldCreateMakerPrimitives(string primitiveName)
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var registry = new WorkflowPrimitiveExecutorRegistry(provider.GetServices<IWorkflowPrimitivePack>());
        var created = registry.TryCreate(primitiveName, provider, out var executor);

        created.Should().BeTrue();
        executor.Should().NotBeNull();
    }

    [Fact]
    public void WorkflowPrimitiveExecutorRegistry_WhenModuleNameUnknown_ShouldReturnFalse()
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowMakerExtensions();
        using var provider = services.BuildServiceProvider();

        var registry = new WorkflowPrimitiveExecutorRegistry(provider.GetServices<IWorkflowPrimitivePack>());
        var created = registry.TryCreate("unknown", provider, out var executor);

        created.Should().BeFalse();
        executor.Should().BeNull();
    }
}

public class MakerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWorkflowMakerExtensions_ShouldRegisterMakerPrimitivePack()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var packs = provider.GetServices<IWorkflowPrimitivePack>().ToList();

        packs.Should().ContainSingle(x => x is MakerPrimitivePack);
    }

    [Fact]
    public void AddWorkflowMakerExtensions_WhenCalledTwice_ShouldRemainIdempotent()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();
        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var packs = provider.GetServices<IWorkflowPrimitivePack>().ToList();

        packs.Count(x => x is MakerPrimitivePack).Should().Be(1);
    }
}
