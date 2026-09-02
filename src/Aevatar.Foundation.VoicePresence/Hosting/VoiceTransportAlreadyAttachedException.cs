namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceTransportAlreadyAttachedException()
    : Exception(Reason)
{
    public const string Reason = "Voice transport already attached.";
}
