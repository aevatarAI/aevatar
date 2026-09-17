using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core.Execution;

/// <summary>
/// Signals that an executor already persisted its terminal decision and only durable publication
/// work remains. The bridge must let the inbound message retry instead of inventing another outcome.
/// </summary>
internal sealed class WorkflowDurablePublicationPendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public WorkflowDurablePublicationPendingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
