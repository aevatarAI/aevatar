using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Configuration;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChannelRuntime_RegistersOnlyPublicRegistrationProjectionServices_ForInMemoryStore()
    {
        var services = new ServiceCollection();

        var result = services.AddChannelRuntime();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAevatarSecretsStore));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(LarkConversationInboxHostedService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationQueryByNyxIdentityPort));
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IPlatformAdapter))
            .Should().Be(0);
        services.Count(descriptor => descriptor.ServiceType == typeof(INyxChannelBotProvisioningService))
            .Should().Be(1);
    }

    [Fact]
    public void AddChannelRuntime_RegistersLarkInteractiveReplyProducer_SoDispatcherCanFindIt()
    {
        var services = new ServiceCollection();

        services.AddChannelRuntime();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        // The runtime can register these via factories, so assert the actual resolved graph
        // where possible, and keep the dispatcher check at the registration level because
        // its NyxIdApiClient dependency is composed by higher-level host wiring.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IInteractiveReplyDispatcher));
        provider.GetRequiredService<IInteractiveReplyCollector>().Should().NotBeNull();
        registry.GetNativeProducer(ChannelId.From("lark")).Should().BeOfType<LarkChannelNativeMessageProducer>();
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
    }

    [Fact]
    public void AddChannelRuntime_RegistersOnlyPublicRegistrationProjectionServices_ForElasticsearchStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
            })
            .Build();
        var services = new ServiceCollection();

        var result = services.AddChannelRuntime(configuration);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAevatarSecretsStore));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(LarkConversationInboxHostedService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationQueryByNyxIdentityPort));
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("ChannelBotDirectCallbackBinding", StringComparison.Ordinal));
    }
}
