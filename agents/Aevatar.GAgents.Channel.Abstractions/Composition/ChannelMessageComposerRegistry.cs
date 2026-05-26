using System.Collections.Generic;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Default <see cref="IChannelMessageComposerRegistry"/> backed by DI-provided composer instances.
/// </summary>
public sealed class ChannelMessageComposerRegistry : IChannelMessageComposerRegistry
{
    private readonly Dictionary<string, IMessageComposer> _composers;
    private readonly Dictionary<string, IChannelNativeMessageProducer> _nativeProducers;

    /// <summary>Initializes a new registry from the supplied composer and native-producer enumerations.</summary>
    public ChannelMessageComposerRegistry(
        IEnumerable<IMessageComposer> composers,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers)
    {
        ArgumentNullException.ThrowIfNull(composers);
        ArgumentNullException.ThrowIfNull(nativeProducers);

        _composers = new Dictionary<string, IMessageComposer>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in composers)
        {
            if (composer is null)
                continue;

            _composers[composer.Channel.Value] = composer;
        }

        _nativeProducers = new Dictionary<string, IChannelNativeMessageProducer>(StringComparer.OrdinalIgnoreCase);
        foreach (var producer in nativeProducers)
        {
            if (producer is null)
                continue;

            _nativeProducers[producer.Channel.Value] = producer;
        }
    }

    /// <inheritdoc />
    public IMessageComposer? Get(ChannelId channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return _composers.TryGetValue(channel.Value, out var composer) ? composer : null;
    }

    /// <inheritdoc />
    public IMessageComposer<TPayload>? Get<TPayload>(ChannelId channel) =>
        Get(channel) as IMessageComposer<TPayload>;

    /// <inheritdoc />
    public IChannelNativeMessageProducer? GetNativeProducer(ChannelId channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return _nativeProducers.TryGetValue(channel.Value, out var producer) ? producer : null;
    }
}
