namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptPackageRuntime
{
    Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct);

    ValueTask<string> ApplyDomainEventAsync(
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);

    ValueTask<string> ReduceReadModelAsync(
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);
}
