namespace Aevatar.Foundation.Abstractions.ExternalLinks;

/// <summary>
/// Describes an external long-lived connection that an actor wants to maintain.
/// </summary>
public sealed record ExternalLinkDescriptor(
    string LinkId,
    string TransportType,
    string Endpoint,
    ExternalLinkOptions? Options = null);

/// <summary>
/// Options for external link reconnection and codec behavior.
/// </summary>
public sealed record ExternalLinkOptions
{
    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>0 = unlimited reconnect attempts.</summary>
    public int MaxReconnectAttempts { get; init; }
}
