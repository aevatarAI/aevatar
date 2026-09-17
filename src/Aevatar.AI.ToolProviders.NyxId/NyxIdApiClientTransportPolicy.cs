namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Marks an HttpClient transport whose primary handler has automatic redirects disabled, allowing
/// the NyxID client to distinguish a primary pre-connect failure from a redirected-host failure.
/// </summary>
public sealed class NyxIdApiClientTransportPolicy
{
    internal NyxIdApiClientTransportPolicy()
    {
    }
}
