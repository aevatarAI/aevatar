using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
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
    public void AddSystemSkillOverlay_WhenEnabled_RegistersGlobalProviderWithoutReplacingFloor()
    {
        var services = new ServiceCollection();
        var floor = new StubFloorProvider();
        services.AddSingleton<IBuiltInPromptFloorProvider>(floor);

        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = true;
            o.SetName = "aevatar-system";
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemSkillOverlayProvider>()
            .Should().BeOfType<OrnnSystemSkillOverlayProvider>();
        provider.GetRequiredService<IBuiltInPromptFloorProvider>().Should().BeSameAs(floor);
    }

    [Fact]
    public void AddSystemSkillOverlay_WhenFloorRegisteredLater_KeepsIndependentRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = true;
            o.SetName = "aevatar-system";
        });
        services.AddSingleton<IBuiltInPromptFloorProvider, StubFloorProvider>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISystemSkillOverlayProvider>()
            .Should().BeOfType<OrnnSystemSkillOverlayProvider>();
        provider.GetRequiredService<IBuiltInPromptFloorProvider>()
            .Should().BeOfType<StubFloorProvider>();
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

        provider.GetRequiredService<OrnnAuthoringAgentToolSource>().Should().NotBeNull();
        provider.GetRequiredService<OrnnPublishAgentToolSource>().Should().NotBeNull();
        provider.GetRequiredService<OrnnSearchAgentToolSource>().Should().NotBeNull();
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
        provider.GetRequiredService<OrnnAuthoringAgentToolSource>().Should().NotBeNull();
        provider.GetRequiredService<OrnnPublishAgentToolSource>().Should().NotBeNull();
        provider.GetRequiredService<OrnnSearchAgentToolSource>().Should().NotBeNull();
    }

    [Fact]
    public async Task OrnnToolSources_ShouldKeepRuntimeSearchSeparateFromAuthoring()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));
        services.AddOrnnSkills();

        await using var provider = services.BuildServiceProvider();
        var searchTools = await provider.GetRequiredService<OrnnSearchAgentToolSource>()
            .DiscoverToolsAsync();
        var authoringTools = await provider.GetRequiredService<OrnnAuthoringAgentToolSource>()
            .DiscoverToolsAsync();
        var publishTools = await provider.GetRequiredService<OrnnPublishAgentToolSource>()
            .DiscoverToolsAsync();

        var tool = searchTools.Should().ContainSingle().Which;
        tool.Name.Should().Be("ornn_search_skills");
        tool.IsReadOnly.Should().BeTrue();
        searchTools.Should().NotContain(candidate =>
            candidate.Name == "ornn_publish_skill" ||
            candidate.Name == "ornn_update_skill");
        authoringTools.Select(static candidate => candidate.Name).Should().Equal(
            "ornn_publish_skill",
            "ornn_update_skill");
        publishTools.Select(static candidate => candidate.Name)
            .Should().ContainSingle().Which.Should().Be("ornn_publish_skill");
    }

    [Fact]
    public async Task AddOrnnSkills_WhenCalledTwice_ShouldRemainIdempotentWithoutAmbientSources()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));

        services.AddOrnnSkills();
        services.AddOrnnSkills();

        await using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentToolSource>().Should().BeEmpty();
        provider.GetServices<OrnnSearchAgentToolSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<OrnnSearchAgentToolSource>());
        provider.GetServices<OrnnAuthoringAgentToolSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<OrnnAuthoringAgentToolSource>());
    }

    [Fact]
    public void AddOrnnSkills_ShouldRegisterOneExactFetcherWithoutReplacingOrdinaryFetcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));

        services.AddOrnnSkills();
        services.AddOrnnSkills();

        using var provider = services.BuildServiceProvider();
        var exactFetchers = provider.GetServices<IExactRemoteSkillFetcher>().ToArray();

        exactFetchers.Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<OrnnExactRemoteSkillFetcher>());
        provider.GetRequiredService<IRemoteSkillFetcher>()
            .Should().BeOfType<OrnnRemoteSkillFetcher>();
    }

    private sealed class StubFloorProvider : IBuiltInPromptFloorProvider
    {
        public BuiltInPromptFloorLayer GetFloor() =>
            new("floor", new BuiltInPromptFloorProvenance("test-floor"));
    }

    private sealed class NotFoundHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
