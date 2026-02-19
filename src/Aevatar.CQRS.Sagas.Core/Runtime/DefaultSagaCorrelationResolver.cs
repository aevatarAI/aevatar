using Aevatar.CQRS.Sagas.Abstractions.Runtime;

namespace Aevatar.CQRS.Sagas.Core.Runtime;

public sealed class DefaultSagaCorrelationResolver : ISagaCorrelationResolver
{
    public string? ResolveCorrelationId(ISaga saga, EventEnvelope envelope)
    {
        _ = saga;
        return string.IsNullOrWhiteSpace(envelope.CorrelationId)
            ? null
            : envelope.CorrelationId;
    }
}
