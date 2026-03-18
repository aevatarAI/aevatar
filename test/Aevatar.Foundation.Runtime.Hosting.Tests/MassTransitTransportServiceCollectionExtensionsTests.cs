using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class MassTransitTransportServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMassTransitKafkaTransport_WhenServicesIsNull_ShouldThrow()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddAevatarFoundationRuntimeMassTransitKafkaTransport();

        act.Should().Throw<ArgumentNullException>();
    }

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
    public void AddMassTransitKafkaTransport_WithoutConfigure_ShouldUseDefaultOptions()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeMassTransitKafkaTransport();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MassTransitKafkaTransportOptions>();
        options.BootstrapServers.Should().Be("localhost:9092");
        options.TopicName.Should().Be("aevatar-foundation-agent-events");
        options.ConsumerGroup.Should().Be("aevatar-foundation-kafka-streaming");
    }

    [Fact]
    public void AddMassTransitKafkaTransport_WhenBootstrapServersMissing_ShouldThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
        {
            options.BootstrapServers = " ";
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddMassTransitKafkaTransport_WhenTopicNameMissing_ShouldThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
        {
            options.TopicName = " ";
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddMassTransitKafkaTransport_WhenConsumerGroupMissing_ShouldThrow()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(options =>
        {
            options.ConsumerGroup = " ";
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddMassTransitStreamProvider_ShouldRegisterStreamProvider_AndLifecycleManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMassTransitEnvelopeTransport, StubMassTransitEnvelopeTransport>();

        services.AddAevatarMassTransitStreamProvider(options =>
        {
            options.StreamNamespace = "aevatar.test.events";
        });

        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamLifecycleManager));
        services.Should().Contain(x => x.ServiceType == typeof(MassTransitStreamProvider));
        services.Should().Contain(x => x.ServiceType == typeof(IActorEventSubscriptionProvider) &&
                                       x.ImplementationType == typeof(MassTransitActorEventSubscriptionProvider));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MassTransitStreamOptions>();
        options.StreamNamespace.Should().Be("aevatar.test.events");
        provider.GetRequiredService<MassTransitStreamProvider>().Should().NotBeNull();
        provider.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamLifecycleManager>().Should().NotBeNull();
    }

    private sealed class StubMassTransitEnvelopeTransport : IMassTransitEnvelopeTransport
    {
        public Task PublishAsync(
            string streamNamespace,
            string streamId,
            byte[] payload,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            Func<MassTransitEnvelopeRecord, Task> handler,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
