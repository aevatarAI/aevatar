namespace Aevatar.CQRS.Sagas.Abstractions.State;

public abstract class SagaStateBase : ISagaState
{
    public string SagaId { get; set; } = Guid.NewGuid().ToString("N");

    public string CorrelationId { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
