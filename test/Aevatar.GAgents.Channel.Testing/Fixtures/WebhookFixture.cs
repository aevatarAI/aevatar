using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Drives one webhook-style adapter under test.
/// </summary>
/// <remarks>
/// Conformance tests use this fixture to push synthetic inbound payloads into the adapter and observe the normalized
/// activities it publishes on <see cref="IChannelTransport.InboundStream"/>. Concrete fixtures own the platform-specific
/// body shapes (Lark JSON, Telegram update, Slack events envelope, etc.).
/// </remarks>
public abstract class WebhookFixture
{
    /// <summary>
    /// Dispatches one synthetic inbound webhook call and waits until the adapter has published the resulting activity.
    /// </summary>
    /// <param name="seed">The intent from the test driver. Concrete fixtures translate this into a platform-native body.</param>
    /// <param name="ct">A token that cancels dispatch.</param>
    /// <returns>The normalized inbound activity observed on the adapter's stream.</returns>
    public abstract Task<ChatActivity> DispatchInboundAsync(InboundActivitySeed seed, CancellationToken ct = default);

    /// <summary>
    /// Replays the last synthetic inbound call so the adapter observes a retry of the same payload.
    /// </summary>
    /// <remarks>
    /// Used by deduplication tests. Implementations must replay the exact bytes, headers, and signatures from the previous
    /// call so the adapter cannot distinguish the retry from the original.
    /// </remarks>
    public abstract Task<ChatActivity?> ReplayLastInboundAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the raw payload blob reference captured by the last inbound call, if the adapter persisted one.
    /// </summary>
    public abstract string? LastPersistedBlobRef { get; }

    /// <summary>
    /// Returns the raw payload bytes recorded by the fixture for the last inbound call.
    /// </summary>
    public abstract byte[]? LastRawPayloadBytes { get; }
}
