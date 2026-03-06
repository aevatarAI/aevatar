using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker.Modules;

namespace Aevatar.Workflow.Extensions.Maker;

/// <summary>
/// Unified module pack for maker-specific workflow extensions.
/// </summary>
public sealed class MakerModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<MakerVoteModule>("maker_vote"),
        WorkflowModuleRegistration.Create<MakerRecursiveModule>("maker_recursive", "maker_recursive_solve"),
    ];

    public string Name => "workflow.extensions.maker";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;
}
