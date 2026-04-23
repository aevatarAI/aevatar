using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Normalized inbound message parsed from a platform-specific webhook payload.
/// </summary>
public sealed class InboundMessage
{
    public required string Platform { get; init; }
    public required string ConversationId { get; init; }
    public required string SenderId { get; init; }
    public required string SenderName { get; init; }
    public required string Text { get; init; }
    public string? MessageId { get; init; }
    public string? ChatType { get; init; }
    public OutboundDeliveryContext? OutboundDelivery { get; init; }
    public TransportExtras? TransportExtras { get; init; }
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}
