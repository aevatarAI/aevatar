namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Exposes the plain-text rendering of one composed native payload.
/// </summary>
public interface IPlainTextComposedMessage
{
    /// <summary>
    /// Gets the canonical plain-text form that can be emitted through text-only transports.
    /// </summary>
    string PlainText { get; }
}
