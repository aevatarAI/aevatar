namespace Sisyphus.Application.Models;

public enum SessionStatus
{
    Created,
    Running,
    Completed,
    Failed,
}

public sealed class ResearchSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Topic { get; init; }
    public string? GraphId { get; set; }
    public string? ActorId { get; set; }
    public string? CommandId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Created;
    public int MaxRounds { get; set; } = 20;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}
