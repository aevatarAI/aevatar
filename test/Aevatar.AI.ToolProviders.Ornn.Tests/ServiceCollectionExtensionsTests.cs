using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSystemSkillOverlay_WhenEnabledWithSetName_OrnnProviderWinsOverDefaultRegisteredFirst()
    {
        // Mirror NyxidChat: the built-in default is registered first via TryAddSingleton, then the
        // Ornn overlay via AddSingleton — the Ornn provider must win (issue #2498).
        var services = new ServiceCollection();
        services.TryAddSingleton<ISystemSkillOverlayProvider>(new StubOverlayProvider());
        services.TryAddSingleton<ISystemSkillOverlayFallback>(new StubOverlayProvider());

        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = true;
            o.SetName = "aevatar-system";
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemSkillOverlayProvider>()
            .Should().BeOfType<OrnnSystemSkillOverlayProvider>();
    }

    [Fact]
    public void AddSystemSkillOverlay_WhenRegisteredBeforeDefault_StillWins()
    {
        // Reverse order: Ornn first (AddSingleton), then the default TryAddSingleton is skipped — the
        // Ornn provider must still win, so registration order does not matter.
        var services = new ServiceCollection();
        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = true;
            o.SetName = "aevatar-system";
        });
        services.TryAddSingleton<ISystemSkillOverlayProvider>(new StubOverlayProvider());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemSkillOverlayProvider>()
            .Should().BeOfType<OrnnSystemSkillOverlayProvider>();
    }

    [Theory]
    [InlineData(false, "aevatar-system")]
    [InlineData(true, "")]
    public void AddSystemSkillOverlay_WhenDisabledOrNoSetName_DoesNotRegisterProvider(bool enabled, string setName)
    {
        var services = new ServiceCollection();

        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = enabled;
            o.SetName = setName;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<ISystemSkillOverlayProvider>().Should().BeNull();
    }

    [Fact]
    public void AddOrnnSkills_WithoutNyxIdTools_ShouldBuildPublishGraphWithFullDiValidation()
    {
        var services = new ServiceCollection();

        services.AddOrnnSkills();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<OrnnAgentToolSource>().Should().NotBeNull();
    }

    [Fact]
    public void AddOrnnSkills_WhenNyxIdToolsRegisteredLater_ShouldNotShadowConfiguredNyxIdOptions()
    {
        var services = new ServiceCollection();

        services.AddOrnnSkills();
        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.example");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<NyxIdToolOptions>().BaseUrl.Should().Be("https://nyx.example");
        provider.GetRequiredService<OrnnAgentToolSource>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddOrnnSkills_WhenCalledTwice_ShouldRemainIdempotentAndReuseConcreteToolSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));

        services.AddOrnnSkills();
        services.AddOrnnSkills();

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Count(x => x is OrnnAgentToolSource).Should().Be(1);
        sources.OfType<OrnnAgentToolSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<OrnnAgentToolSource>());
    }

    [Fact]
    public void AddOrnnSkills_ShouldAliasOrdinaryAndExactPortsToOneFetcherWithoutExposingItAsToolSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));

        services.AddOrnnSkills();
        services.AddOrnnSkills();

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<OrnnRemoteSkillFetcher>();
        var ordinary = provider.GetRequiredService<IRemoteSkillFetcher>();
        var exact = provider.GetRequiredService<IExactRemoteSkillFetcher>();

        ordinary.Should().BeSameAs(concrete);
        exact.Should().BeSameAs(concrete);
        provider.GetServices<IRemoteSkillFetcher>().Should().ContainSingle();
        provider.GetServices<IExactRemoteSkillFetcher>().Should().ContainSingle();
        provider.GetRequiredService<ExactRemoteReleaseVerifier>().Should().NotBeNull();
        provider.GetServices<IAgentToolSource>().Should()
            .NotContain(source => ReferenceEquals(source, exact));
    }

    private sealed class StubOverlayProvider : ISystemSkillOverlayProvider, ISystemSkillOverlayFallback
    {
        public Aevatar.AI.Abstractions.SystemSkillOverlay? GetCurrent(SystemSkillOverlayRequest request) => null;

        public Aevatar.AI.Abstractions.SystemSkillOverlay? GetFallback() => null;
    }

    private sealed class NotFoundHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
