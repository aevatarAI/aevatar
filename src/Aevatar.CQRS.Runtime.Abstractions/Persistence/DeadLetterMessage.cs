namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public sealed class DeadLetterMessage
{
    public string DeadLetterId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
