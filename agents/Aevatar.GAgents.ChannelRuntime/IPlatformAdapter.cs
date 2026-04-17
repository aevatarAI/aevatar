using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Adapter for a bot platform (Lark, Telegram, Discord, etc.).
/// Each platform implements inbound parsing, verification handling, and outbound reply.
/// </summary>
public interface IPlatformAdapter
{
    /// <summary>Platform identifier (e.g. "lark", "telegram", "discord").</summary>
    string Platform { get; }

    /// <summary>
    /// Handle platform-specific verification challenges (e.g. Lark URL verification).
    /// Returns a non-null <see cref="IResult"/> if this was a verification request;
    /// the caller should return it immediately without further processing.
    /// Returns null if this is a normal message callback.
    /// </summary>
    Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration);

    /// <summary>
    /// Parse the platform-specific webhook payload into a normalized <see cref="InboundMessage"/>.
    /// Returns null if the payload is not a processable message (e.g. unsupported event type).
    /// </summary>
    Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration);

    /// <summary>
    /// Send a reply message to the platform via the Nyx provider proxy.
    /// </summary>
    Task<PlatformReplyDeliveryResult> SendReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct);
}

/// <summary>
/// Optional capability: platforms that support progressive reply delivery.
/// The caller posts an initial placeholder message when the first LLM delta arrives,
/// then patches it as deltas accumulate. Reduces perceived latency for long
/// LLM responses (user sees text stream in, ChatGPT-style) instead of staring
/// at silence until the full response is ready.
/// </summary>
public interface IStreamingPlatformAdapter : IPlatformAdapter
{
    /// <summary>
    /// Post a placeholder message carrying the first partial text.
    /// Returns the platform-assigned message id used for subsequent updates,
    /// or null if the post failed — caller should fall back to <see cref="IPlatformAdapter.SendReplyAsync"/>.
    /// </summary>
    Task<string?> PostStreamingPlaceholderAsync(
        string initialText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct);

    /// <summary>
    /// Replace the content of an existing placeholder message with the accumulated text.
    /// </summary>
    Task<PlatformReplyDeliveryResult> UpdateStreamingMessageAsync(
        string messageId,
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct);
}

public readonly record struct PlatformReplyDeliveryResult(
    bool Succeeded,
    string? Detail = null);
