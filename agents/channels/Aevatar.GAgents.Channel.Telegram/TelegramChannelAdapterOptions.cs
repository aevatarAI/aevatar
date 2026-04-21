using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Configures one Telegram adapter instance.
/// </summary>
public sealed class TelegramChannelAdapterOptions
{
    /// <summary>
    /// Gets or sets the inbound transport mode enabled for this adapter instance.
    /// </summary>
    public TransportMode TransportMode { get; init; } = TransportMode.Webhook;

    /// <summary>
    /// Gets or sets the long-polling timeout in seconds.
    /// </summary>
    public int LongPollingTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Gets or sets the debounce window used by streaming edit loops.
    /// </summary>
    public int RecommendedStreamDebounceMs { get; init; } = 3000;

    /// <summary>
    /// Gets or sets the time provider used by long-polling and streaming timers.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
