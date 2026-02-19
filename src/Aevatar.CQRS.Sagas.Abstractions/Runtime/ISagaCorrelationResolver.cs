namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaCorrelationResolver
{
    string? ResolveCorrelationId(ISaga saga, EventEnvelope envelope);
}
