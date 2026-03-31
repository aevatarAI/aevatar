using Aevatar.Foundation.Abstractions.ExternalLinks;

namespace Aevatar.Foundation.Core.ExternalLinks;

/// <summary>
/// Runtime state for a single external connection managed by <see cref="ExternalLinkManager"/>.
/// </summary>
internal sealed class ManagedLink : IAsyncDisposable
{
    public ExternalLinkDescriptor Descriptor { get; }
    public IExternalLinkTransport Transport { get; }
    public CancellationTokenSource LifetimeCts { get; } = new();
    public int ReconnectAttempt { get; set; }
    public bool IsConnected { get; set; }
    public bool IsClosed { get; set; }

    public ManagedLink(ExternalLinkDescriptor descriptor, IExternalLinkTransport transport)
    {
        Descriptor = descriptor;
        Transport = transport;
    }

    public async ValueTask DisposeAsync()
    {
        LifetimeCts.Cancel();
        LifetimeCts.Dispose();
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
