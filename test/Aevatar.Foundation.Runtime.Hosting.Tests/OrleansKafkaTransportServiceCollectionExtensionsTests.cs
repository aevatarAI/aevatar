using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class OrleansKafkaTransportServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMassTransitKafkaTransport_ShouldRegisterTransport_AndPersistOptions()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
        {
            options.BootstrapServers = "localhost:39092";
            options.TopicName = "agent-events";
            options.ConsumerGroup = "transport-group";
        });

        services.Should().Contain(x => x.ServiceType == typeof(IKafkaEnvelopeTransport));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MassTransitKafkaTransportOptions>();
        options.BootstrapServers.Should().Be("localhost:39092");
        options.TopicName.Should().Be("agent-events");
        options.ConsumerGroup.Should().Be("transport-group");
    }

    [Fact]
    public void AddMassTransitKafkaStreamProvider_ShouldRegisterStreamProvider_AndLifecycleManager()
    {
        var services = new ServiceCollection();

        services.AddAevatarMassTransitKafkaStreamProvider(options =>
        {
            options.StreamNamespace = "aevatar.test.events";
        });

        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamLifecycleManager));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.MassTransitKafkaStreamOptions>();
        options.StreamNamespace.Should().Be("aevatar.test.events");
    }
}
