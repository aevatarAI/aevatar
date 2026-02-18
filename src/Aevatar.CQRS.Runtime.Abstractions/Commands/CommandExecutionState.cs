namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public sealed class CommandExecutionState
{
    public string CommandId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public CommandExecutionStatus Status { get; set; } = CommandExecutionStatus.Accepted;
    public int Attempt { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Error { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
