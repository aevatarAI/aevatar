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

public sealed class WorkflowDeliveryProjectionRegistrationTests
{
    [Fact]
    public void AddStudioProjectionComponents_ShouldRegisterDeliveryProjectionAndPorts()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();

        services.Should().Contain(descriptor =>
            descriptor.ImplementationType == typeof(WorkflowDeliveryCurrentStateProjector));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionDocumentMetadataProvider<WorkflowDeliveryCurrentStateDocument>) &&
            descriptor.ImplementationType ==
                typeof(WorkflowDeliveryCurrentStateDocumentMetadataProvider));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowDeliveryQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowDeliveryCommandPort));
    }

    [Fact]
    public void AddStudioProjectionReadModelProviders_ShouldRegisterDeliveryDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddStudioProjectionComponents();

        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<
                WorkflowDeliveryCurrentStateDocument,
                string>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<
                WorkflowDeliveryCurrentStateDocument,
                string>>();
    }
}
