using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker.PrimitiveExecutors;

namespace Aevatar.Workflow.Extensions.Maker;

/// <summary>
/// Unified primitive pack for maker-specific workflow extensions.
/// </summary>
public sealed class MakerPrimitivePack : IWorkflowPrimitivePack
{
    private static readonly IReadOnlyList<WorkflowPrimitiveRegistration> ExecutorRegistrations =
    [
        WorkflowPrimitiveRegistration.Create<MakerVotePrimitiveExecutor>("maker_vote"),
    ];

    public string Name => "workflow.extensions.maker";

    public IReadOnlyList<WorkflowPrimitiveRegistration> Executors => ExecutorRegistrations;
}
