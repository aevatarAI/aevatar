using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceServingProjectionInfrastructureTests
{
    [Fact]
    public void ServingMetadataProviders_ShouldExposeStableIndexNames()
    {
        new ServiceDeploymentCatalogReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-deployments");
        new ServiceServingSetReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-serving");
        new ServiceRolloutReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-rollouts");
        new ServiceRolloutCommandObservationReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-rollout-command-observations");
        new ServiceTrafficViewReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-traffic");
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterServingProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>) &&
            x.ImplementationType == typeof(ServiceServingSetReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>) &&
            x.ImplementationType == typeof(ServiceRolloutReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceRolloutCommandObservationReadModel>) &&
            x.ImplementationType == typeof(ServiceRolloutCommandObservationReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>) &&
            x.ImplementationType == typeof(ServiceTrafficViewReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceDeploymentCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceServingSetQueryReader) &&
            x.ImplementationType == typeof(ServiceServingSetQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRolloutQueryReader) &&
            x.ImplementationType == typeof(ServiceRolloutQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRolloutCommandObservationQueryReader) &&
            x.ImplementationType == typeof(ServiceRolloutCommandObservationQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceTrafficViewQueryReader) &&
            x.ImplementationType == typeof(ServiceTrafficViewQueryReader));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceDeploymentCatalogProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceDeploymentCatalogProjector>(x.ImplementationType));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceRolloutProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceRolloutProjector>(x.ImplementationType));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceRolloutProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceRolloutCommandObservationProjector>(x.ImplementationType));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<ServiceServingSetProjectionContext>) &&
            IsObservedCurrentStateMaterializerFor<ServiceServingSetProjector>(x.ImplementationType));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<ServiceTrafficViewProjectionContext>) &&
            IsObservedCurrentStateMaterializerFor<ServiceTrafficViewProjector>(x.ImplementationType));
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TProjector>(Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
    }

    private static bool IsObservedCurrentStateMaterializerFor<TProjector>(Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedCurrentStateProjectionMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
    }

}
