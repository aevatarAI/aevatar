using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Core;

/// <summary>
/// Signals that an activation pending record is durable but its recovery callback could not be
/// armed. The current envelope must remain unacknowledged so a fresh activation can retry it.
/// </summary>
internal sealed class ServiceDeploymentActivationRetrySchedulePendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public ServiceDeploymentActivationRetrySchedulePendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
