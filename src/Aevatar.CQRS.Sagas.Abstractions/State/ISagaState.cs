namespace Aevatar.CQRS.Sagas.Abstractions.State;

public interface ISagaState
{
    string SagaId { get; set; }

    string CorrelationId { get; set; }

    bool IsCompleted { get; set; }

    int Version { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}
