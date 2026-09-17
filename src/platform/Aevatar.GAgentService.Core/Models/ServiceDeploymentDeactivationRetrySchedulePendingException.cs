using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Core;

/// <summary>
/// Indicates that a durable deactivation checkpoint was committed but its recovery callback
/// could not be armed. Re-activation will retry scheduling from actor-owned state.
/// </summary>
internal sealed class ServiceDeploymentDeactivationRetrySchedulePendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public ServiceDeploymentDeactivationRetrySchedulePendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
