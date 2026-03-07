namespace Aevatar.Workflow.Core;

/// <summary>
/// Unified contribution contract for workflow primitive executors.
/// Both built-in primitive executors and extension executors use the same pack model.
/// </summary>
public interface IWorkflowPrimitivePack
{
    string Name { get; }

    IReadOnlyList<WorkflowPrimitiveRegistration> Executors { get; }
}
