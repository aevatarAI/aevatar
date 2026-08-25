using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Signals that committed binding-run state still needs its next self continuation published.
/// </summary>
internal sealed class StudioMemberBindingRunRecoveryPublicationPendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public StudioMemberBindingRunRecoveryPublicationPendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
