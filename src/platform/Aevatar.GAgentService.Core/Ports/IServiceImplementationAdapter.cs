using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core.Ports;

public interface IServiceImplementationAdapter
{
    ServiceImplementationKind ImplementationKind { get; }

    Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default);
}
