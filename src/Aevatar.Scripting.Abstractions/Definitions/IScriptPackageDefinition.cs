using Google.Protobuf.WellKnownTypes;
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

    ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);

    ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct);
}
