namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Exposes an adapter-native interactive payload produced by a channel message composer.
/// </summary>
/// <remarks>
/// Relay transports use this contract when a single plain-text platform message cannot
/// preserve the logical reply and an existing rich payload shape must be forwarded instead.
/// </remarks>
public interface IInteractiveComposedMessage : IPlainTextComposedMessage
{
    /// <summary>Gets the platform message type, for example <c>interactive</c>.</summary>
    string MessageType { get; }

    /// <summary>Gets the serialized adapter-native interactive content.</summary>
    string ContentJson { get; }

    /// <summary>Gets a value indicating whether the payload is interactive.</summary>
    bool IsInteractive { get; }
}
