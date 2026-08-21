using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

[ActorStateMigration(
    ProjectionScopeStatusGAgent.AgentKind,
    RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
    RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
    RequiredContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion)]
internal sealed class ProjectionScopeStatusTerminalStateV0ToV1Migration
    : IActorStateMigration<ProjectionScopeStatusTerminalState>
{
    public int FromStateVersion => 0;

    public int ToStateVersion => ProjectionScopeStatusGAgent.SupportedStateSchemaVersion;

    public ProjectionScopeStatusTerminalState Apply(ProjectionScopeStatusTerminalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Clone();
    }
}
