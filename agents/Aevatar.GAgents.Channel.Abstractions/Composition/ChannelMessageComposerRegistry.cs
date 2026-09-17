using System.Collections.Generic;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Default <see cref="IChannelMessageComposerRegistry"/> backed by DI-provided composer instances.
/// Composition rejects conflicting implementations for one channel while allowing repeated
/// references to the same singleton registration.
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

            AddUnique(_composers, composer.Channel.Value, composer, "composer");
        }

        _nativeProducers = new Dictionary<string, IChannelNativeMessageProducer>(StringComparer.OrdinalIgnoreCase);
        foreach (var producer in nativeProducers)
        {
            if (producer is null)
                continue;

            AddUnique(_nativeProducers, producer.Channel.Value, producer, "native producer");
        }
    }

    private static void AddUnique<T>(
        IDictionary<string, T> registrations,
        string channel,
        T registration,
        string registrationKind)
        where T : class
    {
        if (!registrations.TryGetValue(channel, out var existing))
        {
            registrations.Add(channel, registration);
            return;
        }

        // Repeated built-in DI registration resolves the same singleton instance and is
        // idempotent. Distinct implementations for one channel are ambiguous and fail at the
        // composition boundary instead of silently selecting the last enumeration item.
        if (ReferenceEquals(existing, registration))
            return;

        throw new InvalidOperationException(
            $"Duplicate {registrationKind} registration detected for channel '{channel}'. " +
            $"Only one implementation may be composed per channel.");
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
