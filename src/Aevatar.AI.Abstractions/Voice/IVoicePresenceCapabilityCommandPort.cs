using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.AI.Abstractions.Voice;

public interface IVoicePresenceCapabilityCommandPort
{
    Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
        string actorId,
        VoicePresenceEnableRequested command,
        CancellationToken ct = default);
}

public sealed record VoicePresenceCapabilityAcceptedReceipt(
    string ActorId,
    string ModuleName,
    string CommandId,
    string CorrelationId,
    string Stage);

public enum VoicePresenceCapabilityCommandError
{
    InvalidInput = 0,
    ActorNotFound = 1,
    DispatchFailed = 2,
}

public sealed class VoicePresenceCapabilityCommandException : Exception
{
    public VoicePresenceCapabilityCommandException(
        VoicePresenceCapabilityCommandError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public VoicePresenceCapabilityCommandError Error { get; }
}
