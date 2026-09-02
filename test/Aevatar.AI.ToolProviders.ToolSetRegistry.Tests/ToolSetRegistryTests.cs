using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry.Tests;

public sealed class ToolSetRegistryTests
{
    [Fact]
    public void Resolve_ShouldReturnConfiguredSources_WhenNameIsKnown()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FirstSource>();
        services.AddSingleton<SecondSource>();
        services.AddToolSetRegistry(options =>
            options.AddToolSet(
                "test.default",
                [
                    sp => sp.GetRequiredService<FirstSource>(),
                    sp => sp.GetRequiredService<SecondSource>(),
                ]));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolSetRegistry>();

        var result = registry.Resolve(" test.default ");

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Name.Should().Be("test.default");
        result.Sources.Select(static source => source.GetType())
            .Should()
            .Equal(typeof(FirstSource), typeof(SecondSource));
    }

    [Fact]
    public void Resolve_ShouldReturnIncludedSourcesBeforeOwnSources()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FirstSource>();
        services.AddSingleton<SecondSource>();
        services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                "workspace.default",
                [sp => sp.GetRequiredService<FirstSource>()]);
            options.AddToolSet(
                "lark.self_notify",
                ["workspace.default"],
                [sp => sp.GetRequiredService<SecondSource>()]);
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolSetRegistry>();

        var result = registry.Resolve("lark.self_notify");

        result.IsSuccess.Should().BeTrue();
        result.Name.Should().Be("lark.self_notify");
        result.Sources.Select(static source => source.GetType())
            .Should()
            .Equal(typeof(FirstSource), typeof(SecondSource));
    }

    [Fact]
    public void Resolve_ShouldFailFast_WhenIncludedToolSetIsUnknown()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FirstSource>();
        services.AddToolSetRegistry(options =>
            options.AddToolSet(
                "lark.self_notify",
                ["workspace.default"],
                [sp => sp.GetRequiredService<FirstSource>()]));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolSetRegistry>();

        var act = () => registry.Resolve("lark.self_notify");

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*includes unknown tool set 'workspace.default'*");
    }

    [Fact]
    public void Resolve_ShouldReturnStructuredError_WhenNameIsUnknown()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FirstSource>();
        services.AddToolSetRegistry(options =>
            options.AddToolSet("known.set", [sp => sp.GetRequiredService<FirstSource>()]));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolSetRegistry>();

        var result = registry.Resolve("missing.set");

        result.IsSuccess.Should().BeFalse();
        result.Sources.Should().BeEmpty();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(ToolSetResolveError.UnknownNameCode);
        result.Error.Name.Should().Be("missing.set");
        result.Error.RegisteredNames.Should().Equal("known.set");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ShouldReturnStructuredError_WhenNameIsEmpty(string? name)
    {
        var services = new ServiceCollection();
        services.AddSingleton<FirstSource>();
        services.AddToolSetRegistry(options =>
            options.AddToolSet("known.set", [sp => sp.GetRequiredService<FirstSource>()]));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolSetRegistry>();

        var result = registry.Resolve(name);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(ToolSetResolveError.EmptyNameCode);
        result.Error.RegisteredNames.Should().Equal("known.set");
    }

    private sealed class FirstSource : EmptyToolSource;

    private sealed class SecondSource : EmptyToolSource;

    private abstract class EmptyToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([]);
    }
}
