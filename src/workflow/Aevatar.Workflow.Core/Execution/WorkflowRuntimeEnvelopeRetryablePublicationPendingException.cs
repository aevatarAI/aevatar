using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core.Execution;

/// <summary>
/// Signals that persisted workflow publication work could not be awakened by either
/// the durable callback scheduler or a typed self continuation.
/// </summary>
internal sealed class WorkflowRuntimeEnvelopeRetryablePublicationPendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
