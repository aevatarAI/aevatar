namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Propagation;

/// <summary>
/// Encapsulates Orleans publisher-chain based loop prevention.
/// </summary>
public interface IEventLoopGuard
{
    /// <summary>
    /// Applies sender metadata before dispatch.
    /// </summary>
    void BeforeDispatch(string senderActorId, string targetActorId, EventEnvelope envelope);

    /// <summary>
    /// Returns true when current actor should drop the envelope as a loop.
    /// </summary>
    bool ShouldDrop(string selfActorId, EventEnvelope envelope);
}
