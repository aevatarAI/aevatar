using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Signals that a committed member deletion still needs its binding-run termination published.
/// </summary>
internal sealed class StudioMemberBindingAuthorityTerminationPublicationPendingException
    : Exception, IRuntimeEnvelopeRetryableException
{
    public StudioMemberBindingAuthorityTerminationPublicationPendingException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
