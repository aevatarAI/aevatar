using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptGAgentContract(
    string StateTypeUrl,
    string ReadModelTypeUrl,
    IReadOnlyList<string> CommandTypeUrls,
    IReadOnlyList<string> DomainEventTypeUrls,
    IReadOnlyList<string> QueryTypeUrls,
    IReadOnlyDictionary<string, string> QueryResultTypeUrls,
    IReadOnlyList<string> InternalSignalTypeUrls,
    ScriptReadModelDefinition? ReadModelDefinition = null,
    IReadOnlyList<string>? StoreKinds = null)
{
    public static ScriptGAgentContract Empty { get; } = new(
        StateTypeUrl: string.Empty,
        ReadModelTypeUrl: string.Empty,
        CommandTypeUrls: Array.Empty<string>(),
        DomainEventTypeUrls: Array.Empty<string>(),
        QueryTypeUrls: Array.Empty<string>(),
        QueryResultTypeUrls: new Dictionary<string, string>(StringComparer.Ordinal),
        InternalSignalTypeUrls: Array.Empty<string>(),
        ReadModelDefinition: null,
        StoreKinds: Array.Empty<string>());

    public IReadOnlyList<string> CommandTypes => CommandTypeUrls;

    public IReadOnlyList<string> DomainEventTypes => DomainEventTypeUrls;

    public IReadOnlyList<string> QueryTypes => QueryTypeUrls;

    public IReadOnlyDictionary<string, string> QueryResultTypes => QueryResultTypeUrls;

    public IReadOnlyList<string> InternalSignalTypes => InternalSignalTypeUrls;

    public IReadOnlyList<string> ReadModelStoreCapabilities => StoreKinds ?? Array.Empty<string>();
}
