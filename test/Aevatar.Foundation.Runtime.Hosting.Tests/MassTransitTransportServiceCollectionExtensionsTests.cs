using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class MassTransitTransportServiceCollectionExtensionsTests
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

        services.Should().Contain(x => x.ServiceType == typeof(IMassTransitEnvelopeTransport));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MassTransitKafkaTransportOptions>();
        options.BootstrapServers.Should().Be("localhost:39092");
        options.TopicName.Should().Be("agent-events");
        options.ConsumerGroup.Should().Be("transport-group");
    }

    [Fact]
    public void AddMassTransitStreamProvider_ShouldRegisterStreamProvider_AndLifecycleManager()
    {
        var services = new ServiceCollection();

        services.AddAevatarMassTransitStreamProvider(options =>
        {
            options.StreamNamespace = "aevatar.test.events";
        });

        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamLifecycleManager));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MassTransitStreamOptions>();
        options.StreamNamespace.Should().Be("aevatar.test.events");
    }
}
