using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowExternalCapabilityValidationException : InvalidOperationException
{
    public WorkflowExternalCapabilityValidationException(
        string message,
        ExternalCapabilityReadiness? readiness = null)
        : base(message)
    {
        Readiness = readiness?.Clone();
    }

    public ExternalCapabilityReadiness? Readiness { get; }
}
