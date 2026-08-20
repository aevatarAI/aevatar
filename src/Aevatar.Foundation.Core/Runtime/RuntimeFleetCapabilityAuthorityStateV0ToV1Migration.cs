using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Core.Runtime;

[ActorStateMigration(
    RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
    RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
    RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
    RequiredContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
    RequiredGateStatus = RuntimeFleetCapabilityGateStatus.Quiesced)]
internal sealed class RuntimeFleetCapabilityAuthorityStateV0ToV1Migration
    : IActorStateMigration<RuntimeFleetCapabilityAuthorityState>
{
    public int FromStateVersion => 0;

    public int ToStateVersion => RuntimeFleetCapabilityAuthorityGAgent.SupportedStateSchemaVersion;

    public RuntimeFleetCapabilityAuthorityState Apply(RuntimeFleetCapabilityAuthorityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Clone();
    }
}
