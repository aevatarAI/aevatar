using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Workflow.Projection.Orchestration;

[ActorStateMigration(
    WorkflowExecutionMaterializationScopeGAgent.AgentKind,
    RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
    RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
    RequiredContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion)]
internal sealed class WorkflowExecutionMaterializationScopeStateV1ToV2Migration
    : IActorStateMigration<ProjectionScopeState>
{
    public int FromStateVersion =>
        WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion;

    public int ToStateVersion =>
        WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion;

    public ProjectionScopeState Apply(ProjectionScopeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Clone();
    }
}
