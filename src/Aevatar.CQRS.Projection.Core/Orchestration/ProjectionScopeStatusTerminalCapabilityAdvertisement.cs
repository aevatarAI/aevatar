using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Advertises to the runtime fleet manifest that this silo can activate the terminal status
/// materializer (<see cref="ProjectionScopeStatusGAgent"/>). The fleet authority opens the
/// <see cref="RuntimeFleetCapability.ProjectionScopeStatusTerminalV1"/> gate only once every
/// active silo advertises this contract, which is what lets a source scope adopt the terminal
/// status route without an old binary ever observing a flipped source.
/// </summary>
internal sealed class ProjectionScopeStatusTerminalCapabilityAdvertisement
    : IRuntimeFleetCapabilityAdvertisement
{
    public RuntimeFleetMemberCapability GetCapability() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
            ReaderContractVersion = (int)ProjectionScopeStatusGAgent.ContractVersion,
            ContractId = ProjectionScopeStatusGAgent.ContractId,
        };

    public Type GetReaderImplementationType() => typeof(ProjectionScopeStatusGAgent);
}
