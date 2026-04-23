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
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IMessageComposer) &&
            descriptor.ImplementationType == typeof(LarkMessageComposer));
        services.Count(descriptor => descriptor.ServiceType == typeof(IPlatformAdapter))
            .Should().Be(0);
        services.Count(descriptor => descriptor.ServiceType == typeof(INyxChannelBotProvisioningService))
            .Should().Be(1);
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
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("ChannelBotDirectCallbackBinding", StringComparison.Ordinal));
    }
}
