namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Converts channel-agnostic message intent into one channel-native payload shape.
/// </summary>
public interface IMessageComposer
{
    /// <summary>
    /// Gets the channel this composer targets.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Produces the channel-native payload for the supplied intent.
    /// </summary>
    object Compose(MessageContent intent, ComposeContext context);

    /// <summary>
    /// Evaluates whether the supplied intent can be expressed exactly, degraded, or not at all.
    /// </summary>
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
    new TNativePayload Compose(MessageContent intent, ComposeContext context);
}
