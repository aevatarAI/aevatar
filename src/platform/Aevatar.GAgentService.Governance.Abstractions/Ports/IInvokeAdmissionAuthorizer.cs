using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IInvokeAdmissionAuthorizer
{
    Task AuthorizeAsync(
        string serviceKey,
        string deploymentId,
        PreparedServiceRevisionArtifact artifact,
        ServiceEndpointDescriptor endpoint,
        ServiceInvocationRequest request,
        CancellationToken ct = default);
}
