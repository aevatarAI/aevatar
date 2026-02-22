using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class OrleansKafkaTransportServiceCollectionExtensionsTests
{
    [Fact]
    public void AddKafkaClientTransport_ShouldRegisterSender_AndPersistOptions()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeOrleansKafkaClientTransport(options =>
        {
            options.BootstrapServers = "localhost:39092";
            options.TopicName = "agent-events";
            options.ConsumerGroup = "client-group";
        });

        services.Should().Contain(x => x.ServiceType == typeof(IOrleansTransportEventSender));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OrleansKafkaTransportOptions>();
        options.BootstrapServers.Should().Be("localhost:39092");
        options.TopicName.Should().Be("agent-events");
        options.ConsumerGroup.Should().Be("client-group");
    }

    [Fact]
    public void AddKafkaSiloTransport_ShouldRegisterHandlerAndSender()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeOrleansKafkaSiloTransport(options =>
        {
            options.BootstrapServers = "localhost:49092";
            options.TopicName = OrleansRuntimeConstants.KafkaEventTopicName;
            options.ConsumerGroup = "silo-group";
        });

        services.Should().Contain(x => x.ServiceType == typeof(IOrleansTransportEventSender));
        services.Should().Contain(x => x.ServiceType == typeof(IOrleansTransportEventHandler));
    }
}
