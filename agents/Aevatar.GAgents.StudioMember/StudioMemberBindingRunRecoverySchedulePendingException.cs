using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Signals that committed binding-run state still needs a durable recovery callback.
/// The runtime must keep the current envelope eligible for redelivery.
/// </summary>
internal sealed class StudioMemberBindingRunRecoverySchedulePendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public StudioMemberBindingRunRecoverySchedulePendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
