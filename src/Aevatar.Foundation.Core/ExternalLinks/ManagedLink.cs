using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Foundation.Core.ExternalLinks;

/// <summary>
/// Runtime state for a single external connection managed by <see cref="ExternalLinkManager"/>.
/// </summary>
// Refactor (iter22/cluster-004):
//   Old pattern: background reconnect callbacks mutated these fields while sleeping outside the actor turn.
//   New principle: fields are changed only by ExternalLinkManager while handling typed actor-turn signals.
internal sealed class ManagedLink : IAsyncDisposable
{
    public ExternalLinkDescriptor Descriptor { get; }
    public IExternalLinkTransport Transport { get; }
    public int ReconnectAttempt { get; set; }
    public bool IsConnected { get; set; }
    public bool IsClosed { get; set; }
    public RuntimeCallbackLease? ReconnectLease { get; set; }

    public ManagedLink(ExternalLinkDescriptor descriptor, IExternalLinkTransport transport)
    {
        Descriptor = descriptor;
        Transport = transport;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Transport.DisposeAsync();
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
