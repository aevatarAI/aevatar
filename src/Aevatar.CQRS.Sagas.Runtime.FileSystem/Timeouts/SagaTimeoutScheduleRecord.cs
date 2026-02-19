namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Timeouts;

internal sealed class SagaTimeoutScheduleRecord
{
    public string TimeoutId { get; set; } = Guid.NewGuid().ToString("N");
    public string SagaName { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string TimeoutName { get; set; } = string.Empty;
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}
