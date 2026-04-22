using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChannelRuntime_RegistersDirectCallbackBindingProjectionServices_ForInMemoryStore()
    {
        var services = new ServiceCollection();

        var act = () => services.AddChannelRuntime();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*IHostedService*");
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotDirectCallbackBindingDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
    }

    [Fact]
    public void AddChannelRuntime_RegistersDirectCallbackBindingProjectionServices_ForElasticsearchStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
            })
            .Build();
        var services = new ServiceCollection();

        var act = () => services.AddChannelRuntime(configuration);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*IHostedService*");
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotDirectCallbackBindingDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
    }
}
