using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Prebuilt clone migration used by dynamically closed durable materialization-scope kinds. The
/// kind is supplied by registration, so this cannot use the static ActorStateMigration attribute.
/// </summary>
internal sealed class ProjectionScopeStateActivationSealMigration
{
    internal static ActorStateMigrationStep Create(string agentKind, int fromStateVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKind);
        return new ActorStateMigrationStep(
            FromStateVersion: fromStateVersion,
            ToStateVersion: checked(fromStateVersion + 1),
            StateContractType: typeof(ProjectionScopeState),
            MigrationType: typeof(ProjectionScopeStateActivationSealMigration),
            Apply: static bytes => ProjectionScopeState.Parser.ParseFrom(bytes).ToByteArray(),
            RequiredCapability: RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RequiredContractId:
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RequiredContractVersion:
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            RequiredGateStatus: RuntimeFleetCapabilityGateStatus.Open);
    }
}
