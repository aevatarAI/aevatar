namespace Aevatar.Workflow.Core;

public sealed class WorkflowExternalCapabilityValidationException : InvalidOperationException
{
    public WorkflowExternalCapabilityValidationException(string message)
        : base(message)
    {
    }
}
