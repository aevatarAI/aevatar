namespace Aevatar.Workflow.Application.Abstractions.Projections;

/// <summary>
/// Opaque lease for one active workflow projection session.
/// </summary>
public interface IWorkflowExecutionProjectionLease
{
    string ActorId { get; }

    string CommandId { get; }
}
