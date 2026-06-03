using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class NoOpVoicePresenceTransportAttachmentPort : IVoicePresenceTransportAttachmentPort
{
    public Task AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(transport);
        return Task.CompletedTask;
    }

    public Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.CompletedTask;
    }
}
