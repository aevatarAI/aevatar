using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptExecutionEngine
{
    Task<ScriptHandlerResult> HandleRequestedEventAsync(
        string source,
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct);

    ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        string source,
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);

    ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        string source,
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);
}
