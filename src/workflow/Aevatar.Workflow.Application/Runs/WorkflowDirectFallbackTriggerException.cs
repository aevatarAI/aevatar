namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Signals a workflow run failure that is eligible for direct fallback.
/// </summary>
public sealed class WorkflowDirectFallbackTriggerException : Exception
{
    /// <summary>
    /// Creates a new fallback trigger exception with a message.
    /// </summary>
    public WorkflowDirectFallbackTriggerException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new fallback trigger exception with a message and inner exception.
    /// </summary>
    public WorkflowDirectFallbackTriggerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
