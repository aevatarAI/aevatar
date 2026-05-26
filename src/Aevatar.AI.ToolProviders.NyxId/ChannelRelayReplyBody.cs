namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Carries the reply payload posted to <c>POST /api/v1/channel-relay/reply</c>.
/// </summary>
/// <remarks>
/// NyxID's channel relay reply endpoint accepts a <c>text</c> fallback plus optional
/// <c>metadata.card</c>. The per-platform adapter on the NyxID side is responsible for
/// materializing the card into each platform's native shape; Aevatar only forwards the
/// intent along with the plain-text fallback so that platforms without card support can
/// still deliver a message.
/// </remarks>
/// <param name="Text">
/// Plain-text fallback rendered when the downstream platform does not support cards.
/// Must be non-empty when <see cref="Metadata"/> is <c>null</c>.
/// </param>
/// <param name="Metadata">
/// Optional metadata envelope carrying platform-specific rich payloads; <c>null</c> degrades to text-only.
/// </param>
public sealed record ChannelRelayReplyBody(string? Text, ChannelRelayReplyMetadata? Metadata = null);

/// <summary>
/// Carries optional metadata forwarded by NyxID to each platform adapter.
/// </summary>
/// <param name="Card">
/// Platform-native card payload; the concrete shape is decided by each platform's NyxID adapter
/// (for example, Lark 2.0 interactive card JSON). Callers do not encode platform assumptions here
/// beyond what their composer returned.
/// </param>
public sealed record ChannelRelayReplyMetadata(object? Card);
