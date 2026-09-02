using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdChatConversationCurrentStateRegistrationTests
{
    [Fact]
    public void AddStudioProjectionComponents_ShouldRegisterProjectorAndMetadata()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();

        services.Should().Contain(descriptor =>
            descriptor.ImplementationType ==
            typeof(NyxIdChatConversationCurrentStateProjector));
        services.Where(descriptor => descriptor.ServiceType == typeof(
                IProjectionDocumentMetadataProvider<
                    NyxIdChatConversationCurrentStateDocument>))
            .Should()
            .ContainSingle()
            .Which.ImplementationType.Should()
            .Be(typeof(NyxIdChatConversationCurrentStateDocumentMetadataProvider));
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_ShouldRegisterActorScopedStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<
                NyxIdChatConversationCurrentStateDocument,
                string>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<
                NyxIdChatConversationCurrentStateDocument,
                string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<
                NyxIdChatConversationCurrentStateDocument>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<
                NyxIdChatConversationCurrentStateDocument,
                string>>();
    }
}
