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

/// <summary>
/// Fresh Phase-B proof that a runtime can turn over an active schema-zero projection actor and
/// refuse a sealed actor row on an older binary. It is deliberately separate from both the V2
/// route contract and the historical Phase-A quiescence advertisement.
/// </summary>
internal sealed class ProjectionScopeStatusTerminalActivationSealCapabilityAdvertisement
    : IRuntimeFleetCapabilityAdvertisement
{
    private readonly bool _isAvailable;

    public ProjectionScopeStatusTerminalActivationSealCapabilityAdvertisement(
        IEnumerable<IRuntimeActorStateSchemaActivationSealSupport>? activationSealSupport = null)
    {
        _isAvailable = activationSealSupport?.Any() == true;
    }

    public bool IsAvailable => _isAvailable;

    public RuntimeFleetMemberCapability GetCapability() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            ReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            ContractId =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
        };

    public Type GetReaderImplementationType() => typeof(ProjectionScopeStatusGAgent);
}
