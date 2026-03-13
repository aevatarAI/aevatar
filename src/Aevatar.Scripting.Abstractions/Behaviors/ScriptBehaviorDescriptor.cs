using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptBehaviorDescriptor(
    Type StateClrType,
    Type ReadModelClrType,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    IReadOnlyDictionary<string, ScriptCommandRegistration> Commands,
    IReadOnlyDictionary<string, ScriptSignalRegistration> Signals,
    IReadOnlyDictionary<string, ScriptDomainEventRegistration> DomainEvents,
    IReadOnlyDictionary<string, ScriptQueryRegistration> Queries,
    ScriptReadModelDefinition? ReadModelDefinition,
    IReadOnlyList<string> StoreKinds)
{
    public ScriptGAgentContract ToContract()
    {
        var queryResultTypeUrls = Queries.ToDictionary(
            static pair => pair.Key,
            static pair => ScriptMessageTypes.GetTypeUrl(pair.Value.ResultClrType),
            StringComparer.Ordinal);

        return new ScriptGAgentContract(
            StateTypeUrl: StateTypeUrl,
            ReadModelTypeUrl: ReadModelTypeUrl,
            CommandTypeUrls: Commands.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            DomainEventTypeUrls: DomainEvents.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            QueryTypeUrls: Queries.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            QueryResultTypeUrls: queryResultTypeUrls,
            InternalSignalTypeUrls: Signals.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            ReadModelDefinition: ReadModelDefinition,
            StoreKinds: StoreKinds.ToArray());
    }
}
