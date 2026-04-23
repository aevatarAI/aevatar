namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Looks up channel composers and native-message producers by <see cref="ChannelId"/>.
/// </summary>
/// <remarks>
/// Composers are registered as singletons via DI; the registry indexes them by the composer's
/// <see cref="IMessageComposer.Channel"/> value. Cross-platform dispatchers resolve per-channel
/// composition through this registry so they never depend on a concrete adapter type.
/// </remarks>
public interface IChannelMessageComposerRegistry
{
    /// <summary>
    /// Gets the composer registered for the supplied channel, or <c>null</c> when no composer is registered.
    /// </summary>
    IMessageComposer? Get(ChannelId channel);

    /// <summary>
    /// Gets the composer registered for the supplied channel when it produces the requested native payload type,
    /// or <c>null</c> when no matching composer is registered.
    /// </summary>
    IMessageComposer<TPayload>? Get<TPayload>(ChannelId channel);

    /// <summary>
    /// Gets the native message producer registered for the supplied channel,
    /// or <c>null</c> when no producer is registered.
    /// </summary>
    IChannelNativeMessageProducer? GetNativeProducer(ChannelId channel);
}
