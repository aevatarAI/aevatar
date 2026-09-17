using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelCatalogPolicyRegistrationTests
{
    [Fact]
    public void AddStudioProjectionComponents_ShouldRegisterPolicyChain()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();

        services.Should().Contain(descriptor =>
            descriptor.ImplementationType == typeof(LLMModelCatalogPolicyCurrentStateProjector));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionDocumentMetadataProvider<LLMModelCatalogPolicyCurrentStateDocument>) &&
            descriptor.ImplementationType ==
                typeof(LLMModelCatalogPolicyCurrentStateDocumentMetadataProvider));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ILLMModelCatalogPolicyQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ILLMModelCatalogPolicyCommandPort));
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_ShouldRegisterPolicyDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddStudioProjectionComponents();

        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<
                LLMModelCatalogPolicyCurrentStateDocument,
                string>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<
                LLMModelCatalogPolicyCurrentStateDocument,
                string>>();
    }
}
