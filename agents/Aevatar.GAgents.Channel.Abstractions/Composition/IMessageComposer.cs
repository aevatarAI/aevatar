namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Converts channel-agnostic message intent into one channel-native payload shape.
/// </summary>
/// <remarks>
/// Composers are pure payload translators. They do not own transport lifecycle, auth resolution, or outbound I/O.
/// <see cref="Evaluate"/> is expected to be side-effect free so callers can probe capability before composing.
/// </remarks>
public interface IMessageComposer
{
    /// <summary>
    /// Gets the channel this composer targets.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Produces the channel-native payload for the supplied intent.
    /// </summary>
    /// <param name="intent">The channel-agnostic outbound message intent.</param>
    /// <param name="context">The target conversation and capability context used during composition.</param>
    /// <returns>The channel-native payload ready for the adapter's outbound SDK call.</returns>
    object Compose(MessageContent intent, ComposeContext context);

    /// <summary>
    /// Evaluates whether the supplied intent can be expressed exactly, degraded, or not at all.
    /// </summary>
    /// <param name="intent">The channel-agnostic outbound message intent.</param>
    /// <param name="context">The target conversation and capability context used during evaluation.</param>
    /// <returns>
    /// <see cref="ComposeCapability.Exact"/> when the intent can be rendered losslessly,
    /// <see cref="ComposeCapability.Degraded"/> when a deterministic downgrade exists, or
    /// <see cref="ComposeCapability.Unsupported"/> when composition must be rejected.
    /// </returns>
    ComposeCapability Evaluate(MessageContent intent, ComposeContext context);
}

/// <summary>
/// Converts channel-agnostic message intent into one strongly typed channel-native payload.
/// </summary>
/// <typeparam name="TNativePayload">The adapter-native payload shape produced by this composer.</typeparam>
public interface IMessageComposer<out TNativePayload> : IMessageComposer
{
    /// <summary>
    /// Produces the strongly typed channel-native payload for the supplied intent.
    /// </summary>
    /// <param name="intent">The channel-agnostic outbound message intent.</param>
    /// <param name="context">The target conversation and capability context used during composition.</param>
    /// <returns>The strongly typed adapter-native payload.</returns>
    new TNativePayload Compose(MessageContent intent, ComposeContext context);
}
