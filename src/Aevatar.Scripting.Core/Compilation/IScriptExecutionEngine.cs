using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptExecutionEngine
{
    Task<ScriptHandlerResult> HandleRequestedEventAsync(
        string source,
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct);

    ValueTask<string> ApplyDomainEventAsync(
        string source,
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);

    ValueTask<string> ReduceReadModelAsync(
        string source,
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);
}
