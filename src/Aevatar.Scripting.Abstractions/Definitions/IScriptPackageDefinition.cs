namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptPackageDefinition
{
    string ScriptId { get; }
    string Revision { get; }
    ScriptContractManifest ContractManifest { get; }

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
