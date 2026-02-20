using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerModuleFactoryTests
{
    [Theory]
    [InlineData("maker_vote")]
    [InlineData("maker_recursive")]
    [InlineData("maker_recursive_solve")]
    public void TryCreate_WhenKnownModuleName_ShouldReturnModule(string moduleName)
    {
        var factory = new MakerModuleFactory();

        var created = factory.TryCreate(moduleName, out var module);

        created.Should().BeTrue();
        module.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_WhenUnknownModuleName_ShouldReturnFalse()
    {
        var factory = new MakerModuleFactory();

        var created = factory.TryCreate("unknown", out var module);

        created.Should().BeFalse();
        module.Should().BeNull();
    }
}

public class MakerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWorkflowMakerExtensions_ShouldRegisterMakerModuleFactory()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IEventModuleFactory>().ToList();

        factories.Should().ContainSingle(x => x is MakerModuleFactory);
    }

    [Fact]
    public void AddWorkflowMakerExtensions_WhenCalledTwice_ShouldRemainIdempotent()
    {
        var services = new ServiceCollection();

        services.AddWorkflowMakerExtensions();
        services.AddWorkflowMakerExtensions();

        using var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IEventModuleFactory>().ToList();

        factories.Count(x => x is MakerModuleFactory).Should().Be(1);
    }
}
