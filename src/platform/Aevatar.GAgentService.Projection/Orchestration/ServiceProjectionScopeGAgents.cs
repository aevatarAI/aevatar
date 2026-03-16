using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

internal sealed class ServiceCatalogProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceCatalogProjectionContext>;

internal sealed class ServiceDeploymentCatalogProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceDeploymentCatalogProjectionContext>;

internal sealed class ServiceRevisionCatalogProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceRevisionCatalogProjectionContext>;

internal sealed class ServiceServingSetProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceServingSetProjectionContext>;

internal sealed class ServiceRolloutProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceRolloutProjectionContext>;

internal sealed class ServiceTrafficViewProjectionScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<ServiceTrafficViewProjectionContext>;
