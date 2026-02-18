namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public sealed class OutboxMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public bool Dispatched { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
}
