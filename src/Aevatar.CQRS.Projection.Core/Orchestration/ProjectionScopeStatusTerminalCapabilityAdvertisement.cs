using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Advertises to the runtime fleet manifest that this silo can activate the terminal status
/// materializer (<see cref="ProjectionScopeStatusGAgent"/>). The fleet authority opens the
/// <see cref="RuntimeFleetCapability.ProjectionScopeStatusTerminalV2"/> capability under the
/// distinct Phase-A bridge contract. This deliberately stops satisfying the old V2 route gate:
/// mixed fleets close admission, while unanimous bridge readers can quiesce it.
/// </summary>
internal sealed class ProjectionScopeStatusTerminalCapabilityAdvertisement
    : IRuntimeFleetCapabilityAdvertisement
{
    public RuntimeFleetMemberCapability GetCapability() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            ReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
        };

    public Type GetReaderImplementationType() => typeof(ProjectionScopeStatusGAgent);
}
