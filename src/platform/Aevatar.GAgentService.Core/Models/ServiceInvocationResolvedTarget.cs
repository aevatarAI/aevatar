using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Core;

public sealed record ServiceInvocationResolvedTarget(
    ServiceCatalogSnapshot Service,
    PreparedServiceRevisionArtifact Artifact,
    ServiceEndpointDescriptor Endpoint);
